using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinTools.Services;

namespace WinTools;

/// <summary>应用程序入口：单一实例、启动参数解析、扩展名注册、主窗口与托盘管理。</summary>
public partial class App : Application, IWindowRegistry
{
    private static readonly string StartupTracePath = Path.Combine(Path.GetTempPath(), "WinTools-startup-trace.txt");

    internal static void TraceStartup(string message)
    {
        try { File.AppendAllText(StartupTracePath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
    private const string SingleInstanceKey = "WinTools";
    private Window? _window;
    private TrayIcon? _trayIcon;
    private FloatingStashManager? _stashManager;
    private PerAppImeService? _perAppImeService;
    private DesktopClickService? _desktopClickService;

    #region Win32 错误弹窗

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0;
    private const uint MB_ICONERROR = 0x10;

    /// <summary>显示错误（不依赖 WinUI，发布版崩溃时也能弹出）。</summary>
    private static void ShowError(string message)
    {
        try { MessageBox(IntPtr.Zero, message, "WinTools 启动失败", MB_OK | MB_ICONERROR); } catch { }
    }

    /// <summary>显示错误并落盘到 <c>%TEMP%\WinTools-error-*.log</c>。</summary>
    private static void ReportFatal(string scope, Exception ex)
    {
        ErrorReporter.Log(scope, ex);
        ShowError($"{ex.Message}\n\n{ex.StackTrace}");
    }

    #endregion

    public App()
    {
        TraceStartup("App constructor: before InitializeComponent");
        InitializeComponent();
        TraceStartup("App constructor: after InitializeComponent");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = (Exception)e.ExceptionObject;
            ReportFatal("App.UnhandledException", ex);
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveConfigOnExit();
        TraceStartup("App constructor: completed");
    }

    #region 启动与激活

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        TraceStartup("OnLaunched: entered");
        try
        {
            TraceStartup("OnLaunched: before OnLaunchedCoreAsync");
            await OnLaunchedCoreAsync(args.Arguments);
            TraceStartup("OnLaunched: after OnLaunchedCoreAsync");
        }
        catch (Exception ex)
        {
            ReportFatal("App.OnLaunched", ex);
            Environment.Exit(1);
        }
    }

    private async System.Threading.Tasks.Task OnLaunchedCoreAsync(string? launchArguments)
    {
        TraceStartup("OnLaunchedCore: entered");
        // 在 UI 线程初始化 SettingsService，确保订阅和事件回调都跑在 UI 线程。
        SettingsService.Initialize();
        var startupConfig = SettingsService.Instance.Current;

        // 单一实例：若已有实例，则转交启动参数后退出
        var instance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (!instance.IsCurrent)
        {
            var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            await instance.RedirectActivationToAsync(activatedArgs);
            Environment.Exit(0);
            return;
        }

        instance.Activated += OnAppInstanceActivated;
        TraceStartup("OnLaunchedCore: before MainWindow");
        var mainWindow = new MainWindow(startupConfig);
        TraceStartup("OnLaunchedCore: after MainWindow");
        _window = mainWindow;
        mainWindow.ClosedToTray += (_, _) =>
        {
            SaveConfigOnExit();
            HideToTray();
        };
        // 激活前先把窗口放到可见工作区。不要依赖 Loaded 再从屏幕外移回，
        // 否则启动初始化稍慢时用户只会看到一个无窗口的后台进程。
        mainWindow.PrepareInitialWindowBounds();
        // 主窗口也先在 DWM 合成层隐藏。此前 Activate 会先露出一个完整黑框，
        // XAML 内容随后才出现；现在等两帧内容完成后再显示，并在此后启动桌面卡片。
        WindowHelper.SetWindowCloak(mainWindow, true);
        _window.Activate();
        WindowHelper.UncloakWhenRendered(
            mainWindow,
            mainWindow.InitializeDesktopCardsAfterFirstFrame);

        // WinUI Window 对象只在 UI 线程访问；后台初始化只接收稳定的 HWND 值。
        // 这也避免 .NET 10 更严格的 COM/WinRT 线程检查在启动阶段制造偶发异常。
        var mainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);

        // OnLaunchedCoreAsync 立即返回：4 个 Init 全部推到 background thread，
        // 让 OnLaunched 尽快结束，WinUI 内部首帧布局（~1.4s）可以跟 Init 并行。
        // - InitPerAppIme / InitDesktopClick：内部用独立 thread 跑 hook，background-safe
        // - EnsureTrayIcon / InitFloatingStash：必须 UI 线程（new Window + Mica），
        //   通过 _dispatcherQueue.TryEnqueue 切回 UI 线程，等 WinUI 布局空出 UI 线程即可
        // 用户体验：主窗口先显示，托盘和悬浮暂存 1-2 秒后出现，但 4 个 service
        // 全部 ready 的总时间从 ~2.55s 降到 ~1.4s（受 WinUI 首帧布局制约）。
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { InitPerAppIme(startupConfig, mainWindowHandle); }
            catch (Exception ex) { ErrorReporter.Log("App.InitPerAppIme", ex); }
            try { InitDesktopClick(startupConfig); }
            catch (Exception ex) { ErrorReporter.Log("App.InitDesktopClick", ex); }
            // 旧“桌面整理”已并入桌面分区卡片，清理此前可能注册的右键菜单。
            try { DesktopContextMenuService.SetEnabled(false); }
            catch (Exception ex) { ErrorReporter.Log("App.DisableLegacyDesktopContextMenu", ex); }
            if (startupConfig.EnableQuickMute)
                _ = WarmUpAudioStateAsync();
            TraceStartup("OnLaunchedCore: background init done");
        });
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
        {
            TraceStartup("OnLaunchedCore: before EnsureTrayIcon");
            EnsureTrayIcon();
            TraceStartup("OnLaunchedCore: before InitFloatingStash");
            InitFloatingStash(startupConfig);
            TraceStartup("OnLaunchedCore: ui init done");
        });
    }

    /// <summary>接收其他 WinTools 进程转交的命令行激活。</summary>
    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(BringWindowToForeground);
    }

    private static async System.Threading.Tasks.Task WarmUpAudioStateAsync()
    {
        await System.Threading.Tasks.Task.Delay(800);
        _ = AudioToggleService.QueryIsMuted();
    }

    #endregion

    #region 托盘管理

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null || _window == null) return;
        _trayIcon = new TrayIcon();
        _trayIcon.ShowMainRequested += (_, _) => BringWindowToForeground();
        _trayIcon.DragStashToggleRequested += (_, _) => ShowDragStashWindow();
        _trayIcon.SettingsRequested += (_, _) => OpenSettingsWindow();
        _trayIcon.ExitRequested += (_, _) =>
        {
            SaveConfigOnExit();
            Environment.Exit(0);
        };
    }

    private void HideToTray()
    {
        if (_window == null) return;
        EnsureTrayIcon();
        _window.AppWindow.Hide();
    }

    internal void BringWindowToForeground()
    {
        if (_window == null) return;
        _window.AppWindow.Show();
        _window.Activate();
    }

    /// <summary>把主窗口带到前台并切到内嵌"设置"页。</summary>
    /// <remarks>
    /// 历史上尝试过把设置拆成独立 <c>SettingsWindow</c>，那段代码已经回滚到
    /// <c>_diff_backup\Settings\</c> 并被删除（2026-08-31）。新流程是：
    /// 托盘 / 全局快捷键触发 → <see cref="OpenSettingsWindow"/> → 主窗口
    /// <see cref="MainWindow.NavigateToSettings"/>。不要再添加
    /// <c>sectionId</c> 这类历史参数，需要直跳子页请在
    /// <c>MainWindow</c> 内部扩展。
    /// </remarks>
    internal void OpenSettingsWindow()
    {
        BringWindowToForeground();
        (_window as MainWindow)?.NavigateToSettings();
    }

    /// <summary>供文件选择器等需要父窗口的 API 使用。</summary>
    internal Window MainWindowForPickers => _window ?? throw new InvalidOperationException("主窗口尚未创建。");

    #endregion

    #region 悬浮暂存

    internal void SetDragStashEnabled(bool enabled) => _stashManager?.SetEnabled(enabled);

    internal void SetProgramAssociationEnabled(bool enabled)
        => _stashManager?.SetProgramAssociationEnabled(enabled);

    internal void ShowDragStashWindow() => _stashManager?.ShowWindow();

    internal void RegisterWinToolsWindow(Window window)
    {
        _stashManager?.RegisterWindow(window);
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _perAppImeService?.RegisterExcludedWindow(hwnd);
        }
        catch { /* ignore */ }
    }

    /// <summary>IWindowRegistry 显式实现：让 MainWindow/DesktopCardManager 通过接口注册窗口，
    /// 避免再写 <c>(App.Current as App)?.RegisterWinToolsWindow(...)</c> 的强耦合。</summary>
    void IWindowRegistry.Register(Window window) => RegisterWinToolsWindow(window);

    private void InitFloatingStash(Config config)
    {
        if (_window == null) return;
        _stashManager ??= new FloatingStashManager(_window.DispatcherQueue, _window);
        _stashManager.SetProgramAssociationEnabled(config.EnableProgramAssoc);
        _stashManager.SetEnabled(config.EnableDragStash);
    }

    #endregion

    #region 按应用切换输入法

    internal void SetPerAppImeEnabled(bool enabled)
    {
        if (_perAppImeService == null)
        {
            if (!enabled) return;
            _perAppImeService = new PerAppImeService();
            _perAppImeService.UpdateRules(ConfigService.Load().PerAppImeRules);
            if (_window != null)
            {
                try { _perAppImeService.RegisterExcludedWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window)); }
                catch (Exception ex) { ErrorReporter.Log("App.SetPerAppImeEnabled.RegisterExcludedWindow", ex); }
            }
        }
        _perAppImeService.SetEnabled(enabled);
    }

    internal void UpdatePerAppImeRules(System.Collections.Generic.IEnumerable<ImeRuleEntry> rules)
        => _perAppImeService?.UpdateRules(rules);

    private void InitPerAppIme(Config config, IntPtr mainWindowHandle)
    {
        if (!config.EnablePerAppIme) return;
        _perAppImeService = new PerAppImeService();
        _perAppImeService.UpdateRules(config.PerAppImeRules);
        _perAppImeService.SetEnabled(config.EnablePerAppIme);

        try
        {
            _perAppImeService.RegisterExcludedWindow(mainWindowHandle);
        }
        catch { /* ignore */ }
    }

    #endregion

    internal System.Collections.Generic.List<string> GetDesktopItemNames() =>
        DesktopCollectService.ListAvailableItemNames();

    #region 点击桌面返回桌面

    internal void SetDesktopClickEnabled(bool enabled)
    {
        _desktopClickService ??= new DesktopClickService();
        _desktopClickService.SetEnabled(enabled);
    }

    private void InitDesktopClick(Config config)
    {
        if (!config.EnableDesktopClickToShow) return;
        _desktopClickService = new DesktopClickService();
        _desktopClickService.SetEnabled(true);
    }

    #endregion

    private static void SaveConfigOnExit()
    {
        try
        {
            if ((Current as App)?._window is MainWindow mw)
                mw.FlushAndSaveConfig();
            SettingsService.Instance.FlushNow();
        }
        catch (Exception ex)
        {
            // 退出路径上抛错不能打断 OS 回收进程，但要留痕便于排查"重启后设置没了"
            ErrorReporter.Log("App.SaveConfigOnExit", ex);
        }
    }

    /// <summary>获取主窗口实例。</summary>
    internal static Window GetMainWindow() => (Current as App)!._window!;

    /// <summary>主窗口的 AppWindow，供 TopologyKey 等同程序集内部使用。
    /// 主窗口尚未创建时返回 null（OnLaunched 之前）。</summary>
    internal static Microsoft.UI.Windowing.AppWindow? MainAppWindow
        => (Current as App)?._window?.AppWindow;
}
