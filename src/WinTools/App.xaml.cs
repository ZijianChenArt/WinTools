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

    /// <summary>
    /// 启动埋点默认**关闭**：它是同步 <see cref="File.AppendAllText"/>，每次调用都要开关一次文件；
    /// 打开时连图标加载这种每项目多次的热路径都会写盘，日志还会无限增长（实测 1.6MB / 13000 行）。
    /// 需要排查启动问题时设环境变量 <c>WINTOOLS_TRACE=1</c>，或在 %TEMP% 放一个
    /// <c>WinTools-trace.on</c> 空文件即可打开。
    /// </summary>
    private static readonly bool StartupTraceEnabled = IsStartupTraceEnabled();

    private static bool IsStartupTraceEnabled()
    {
        try
        {
            if (string.Equals(Environment.GetEnvironmentVariable("WINTOOLS_TRACE"), "1", StringComparison.Ordinal))
                return true;
            return File.Exists(Path.Combine(Path.GetTempPath(), "WinTools-trace.on"));
        }
        catch { return false; }
    }

    internal static void TraceStartup(string message)
    {
        if (!StartupTraceEnabled) return;
        try { File.AppendAllText(StartupTracePath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
    private const string SingleInstanceKey = "WinTools";

    /// <summary>进程正在退出：托盘「退出 WinTools」或系统注销 / 关机。
    /// 所有「关闭时只隐藏」的窗口都必须查这个标志再决定要不要 <c>e.Cancel = true</c>，
    /// 否则关机界面上的「结束任务」和不带 <c>/F</c> 的 taskkill 都关不掉它们。</summary>
    internal static bool IsShuttingDown { get; private set; }

    private Window? _window;
    private TrayIcon? _trayIcon;
    private FloatingStashManager? _stashManager;
    private PerAppImeService? _perAppImeService;
    private DesktopClickService? _desktopClickService;
    private ExplorerPreviewService? _explorerPreviewService;
    private SpotlightWindow? _spotlightWindow;
    private VoiceBallService? _voiceBallService;
    internal TaskbarInfoService? TaskbarInfo { get; private set; }

    internal void ApplyTaskbarInfo(Config config)
    {
        if (TaskbarInfo == null && config.EnableTaskbarInfo)
        {
            TaskbarInfo = new TaskbarInfoService();
            TaskbarInfo.StashCountProvider = () => _stashManager?.ItemCount ?? 0;
            TaskbarInfo.VoiceActiveProvider = () => _voiceBallService?.IsListening ?? false;
            TaskbarInfo.StatusChanged += () =>
            {
                (_window as MainWindow)?.UpdateTaskbarInfoStatus();
            };
            TaskbarInfo.ActionRequested += action =>
            {
                if (action == "library") (_window as MainWindow)?.ToggleDesktopLibrary();
                else if (action == "stash") ToggleDragStashWindow();
                else if (action == "voice")
                {
                    SettingsService.Instance.Current.Hotkeys.TryGetValue(MainWindow.VoiceBallHotkeyKey, out var hotkey);
                    VoiceBallService.TriggerHotkey(hotkey);
                }
                else _trayIcon?.ShowQuickMenu();
            };
        }
        TaskbarInfo?.Apply(config);
    }
    // UI 线程的队列：语音小球的回调来自后台线程，不能在那里访问 Window.DispatcherQueue。
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

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
        // WinUI 的 UI 线程异常**不走** AppDomain 那条：XAML 侧未处理就直接 failfast
        // （事件查看器里表现为 Microsoft.UI.Xaml.dll + 0xc000027b，托管日志里一个字都没有）。
        // 对一个常驻托盘的小工具来说，「记下来继续跑」永远好过「窗口凭空消失」，
        // 所以这里统一接住并标记已处理；真正致命的启动失败仍走 ReportFatal 弹窗。
        UnhandledException += (_, e) =>
        {
            ErrorReporter.Log("App.XamlUnhandledException", e.Exception);
            e.Handled = true;
        };
        // 后台 Task 里没被 await 的异常，终结器线程上抛出来同样会带走整个进程。
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorReporter.Log("App.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // 退出路径（托盘退出 / 会话结束）已经各自落过盘了。会话结束那条还处在
            // csrss 的同步 SendMessage 里，此时读 XAML 必抛 COMException，不能再走一遍。
            if (IsShuttingDown) return;
            SaveConfigOnExit();
        };
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
        _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        UiHangWatchdog.Start(_uiQueue);
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

        // 分区归属现在是**实时匹配**（枚举桌面 + DesktopZoneMatcher），配置里的分类规则改了
        // 下一次刷新就生效，不需要启动时再整理一遍目录。旧版数据的一次性迁移放在
        // DesktopCardManager.InitializeAsync 的后台线程里，不占首帧。

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
        // 资源管理器空格预览需要 UI 线程的 DispatcherQueue，必须在后台任务开始前从这里取。
        var uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { InitPerAppIme(startupConfig, mainWindowHandle); }
            catch (Exception ex) { ErrorReporter.Log("App.InitPerAppIme", ex); }
            try { InitDesktopClick(startupConfig); }
            catch (Exception ex) { ErrorReporter.Log("App.InitDesktopClick", ex); }
            try { InitExplorerPreview(uiDispatcher, startupConfig); }
            catch (Exception ex) { ErrorReporter.Log("App.InitExplorerPreview", ex); }
            try { InitVoiceBall(startupConfig); }
            catch (Exception ex) { ErrorReporter.Log("App.InitVoiceBall", ex); }
            // 旧“桌面整理”已并入桌面分区卡片，清理此前可能注册的右键菜单。
            try { DesktopContextMenuService.SetEnabled(false); }
            catch (Exception ex) { ErrorReporter.Log("App.DisableLegacyDesktopContextMenu", ex); }
            // 提前建好应用索引：第一次按 Alt+Space 就该立刻出结果，
            // 而不是等几百毫秒扫完开始菜单。索引自己跑在独立 STA 线程上。
            try { if (startupConfig.EnableSpotlight) _ = AppSearchIndex.GetAsync(); }
            catch (Exception ex) { ErrorReporter.Log("App.WarmUpAppSearchIndex", ex); }
            TraceStartup("OnLaunchedCore: background init done");
        });
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
        {
            TraceStartup("OnLaunchedCore: before EnsureTrayIcon");
            EnsureTrayIcon();
            try { ApplyTaskbarInfo(startupConfig); }
            catch (Exception ex) { ErrorReporter.Log("App.TaskbarInfo", ex); }
            TraceStartup("OnLaunchedCore: before InitFloatingStash");
            InitFloatingStash(startupConfig);
            AudioDeviceKeeper.Start();
            TraceStartup("OnLaunchedCore: ui init done");
        });
    }

    /// <summary>接收其他 WinTools 进程转交的命令行激活。</summary>
    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        _window?.DispatcherQueue.TryEnqueue(BringWindowToForeground);
    }

    #endregion

    #region 托盘管理

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null || _window == null) return;
        _trayIcon = new TrayIcon();
        _trayIcon.ShowMainRequested += (_, _) => BringWindowToForeground();
        _trayIcon.SpotlightRequested += (_, _) => ToggleSpotlight();
        _trayIcon.DragStashToggleRequested += (_, _) => ToggleDragStashWindow();
        _trayIcon.SyncDesktopRequested += (_, _) => (_window as MainWindow)?.SyncDesktopCardsFromTray();
        _trayIcon.ShowDesktopCardsRequested += (_, _) => (_window as MainWindow)?.ShowDesktopCardsFromTray(fromLeft: _trayIcon.QuickMenuFromLeft);
        _trayIcon.SettingsRequested += (_, _) => OpenSettingsWindow();
        _trayIcon.ExitRequested += (_, _) => ShutdownNow();
        _trayIcon.QuotaRefreshRequested += (_, _) => TaskbarInfo?.Refresh();
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

    /// <summary>桌面卡片右键菜单「分区设置」：把主窗口带到前台并切到桌面分区页。</summary>
    internal void OpenDesktopZoneSettings()
    {
        BringWindowToForeground();
        (_window as MainWindow)?.NavigateToDesktopOrganize();
    }

    /// <summary>供文件选择器等需要父窗口的 API 使用。</summary>
    internal Window MainWindowForPickers => _window ?? throw new InvalidOperationException("主窗口尚未创建。");

    #endregion

    #region 悬浮暂存

    internal void SetDragStashEnabled(bool enabled) => _stashManager?.SetEnabled(enabled);

    internal void SetProgramAssociationEnabled(bool enabled)
        => _stashManager?.SetProgramAssociationEnabled(enabled);

    internal void ShowDragStashWindow() => _stashManager?.ShowWindow();

    /// <summary>任务栏图标 / 托盘菜单：已打开则收起，否则打开（与音频设备按钮一致）。</summary>
    internal void ToggleDragStashWindow() => _stashManager?.ToggleWindow();

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
        _stashManager.StashAnchorProvider = () => TaskbarInfo?.GetStashAnchor();
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

    /// <summary>
    /// 空格预览（仿 QuickLook）的启动入口。钩子始终安装，开关只控制它是否接管按键，
    /// 所以在设置页里切换不需要重新安装钩子。窗口在 UI 线程上预热，第一次按空格时不必等它创建。
    /// </summary>
    private void InitExplorerPreview(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Config config)
    {
        FilePreviewService.CardPreviewEnabled = config.EnableCardPreview;
        dispatcher.TryEnqueue(FilePreviewService.Warmup);

        _explorerPreviewService = new ExplorerPreviewService(dispatcher) { Enabled = config.EnableExplorerPreview };
        _explorerPreviewService.Start();
    }

    /// <summary>设置页切换空格预览时调用，立即对运行中的服务生效。</summary>
    internal void SetSpacePreviewEnabled(bool explorer, bool card)
    {
        FilePreviewService.CardPreviewEnabled = card;
        if (_explorerPreviewService is { } service) service.Enabled = explorer;
    }

    #endregion

    #region 语音小球

    private void InitVoiceBall(Config config)
    {
        if (!config.EnableVoiceBall) return;
        config.Hotkeys.TryGetValue(MainWindow.VoiceBallHotkeyKey, out var hotkey);
        SetVoiceBallEnabled(true, hotkey, MainWindow.GetVoiceBallOffset(config));
    }

    /// <summary>开关语音小球；服务内部自带后台线程，UI 线程调用只做启停。</summary>
    internal void SetVoiceBallEnabled(bool enabled, string? hotkey, (double X, double Y)? offset)
    {
        if (_voiceBallService == null)
        {
            _voiceBallService = new VoiceBallService();
            // 拖动松手的回调来自小球的后台线程，配置与设置页都只能在 UI 线程上动。
            _voiceBallService.OffsetDragged += (x, y) =>
                _uiQueue?.TryEnqueue(() => (_window as MainWindow)?.OnVoiceBallOffsetDragged(x, y));
        }
        _voiceBallService.SetHotkey(hotkey);
        _voiceBallService.SetCustomOffset(offset);
        _voiceBallService.SetEnabled(enabled);
    }

    internal void SetVoiceBallOffset((double X, double Y)? offset) => _voiceBallService?.SetCustomOffset(offset);

    internal void SetVoiceBallHotkey(string? hotkey) => _voiceBallService?.SetHotkey(hotkey);

    #endregion

    #region 悬浮搜索

    /// <summary>全局快捷键入口：呼出 / 收起悬浮搜索窗口。</summary>
    /// <remarks>窗口按需创建后常驻（只隐藏不销毁），第二次呼出无需重新走 WinUI 初始化。</remarks>
    internal void ToggleSpotlight()
    {
        try
        {
            if (_spotlightWindow == null)
            {
                _spotlightWindow = new SpotlightWindow();
                RegisterWinToolsWindow(_spotlightWindow);
            }

            _spotlightWindow.Toggle();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("App.ToggleSpotlight", ex);
        }
    }

    #endregion

    #region 退出与会话结束

    /// <summary>会话结束的两条消息是 csrss 用 <c>SendMessage</c> 跨进程同步发过来的，
    /// 处理它们期间**禁止 COM 调出**（HRESULT <c>RPC_E_CANTCALLOUT_ININPUTSYNCCALL</c>）。
    /// 所以这两个入口只做纯文件 I/O，读 XAML 控件的那一份必须排队到消息循环里跑。</summary>
    /// <remarks>2026-09-06 实测：直接在窗口过程里调 <see cref="SaveConfigOnExit"/>，
    /// 会在 <c>FlushAndSaveConfig</c> → <c>FindName</c> 处抛 <c>COMException</c>，配置一点没存上。</remarks>
    private static void FlushSettingsWithoutUi()
    {
        try { SettingsService.Instance.FlushNow(); }
        catch (Exception ex) { ErrorReporter.Log("App.FlushSettingsWithoutUi", ex); }
    }

    /// <summary>收到 <c>WM_QUERYENDSESSION</c>：会话仍可能被别的应用取消，只落盘不退出。</summary>
    internal static void SaveBeforeSessionEnd()
    {
        // 读 XAML 的完整保存排进 DispatcherQueue：回到消息循环（两条消息之间）才执行，
        // 那时已经不在同步 SendMessage 里，COM 可以正常调用。队列没来得及跑也不要紧，
        // 下面的纯 I/O 落盘就是兜底。
        try { (Current as App)?._window?.DispatcherQueue.TryEnqueue(SaveConfigOnExit); }
        catch (Exception ex) { ErrorReporter.Log("App.SaveBeforeSessionEnd", ex); }
        FlushSettingsWithoutUi();
    }

    /// <summary>收到 <c>WM_ENDSESSION</c>(wParam≠0)：落盘后立刻结束进程。</summary>
    /// <remarks>
    /// WinUI 3 收到 <c>WM_ENDSESSION</c> **不会**自己退出。不主动退的后果是 Windows 等满
    /// <c>WaitToKillAppTimeout</c>（默认 5s），然后把本进程所有可见顶层窗口列到
    /// 「这些应用阻止关机」那一屏上——分区卡片有几张就列几行同名的条目。
    /// 关机时进程由 csrss 结束，<c>AppDomain.ProcessExit</c> 也不保证执行，所以配置必须
    /// 在这里落盘。托盘图标**不摘**：<c>TaskbarIcon</c> 是 XAML 元素，同样受 COM 调出限制，
    /// 而且注销 / 关机时整个 Shell 都会消失，不存在幽灵图标。
    /// </remarks>
    internal static void ShutdownForSessionEnd()
    {
        IsShuttingDown = true;
        FlushSettingsWithoutUi();
        RestoreDesktopIcons();
        Environment.Exit(0);
    }

    /// <summary>退出前把桌面图标还给用户。</summary>
    /// <remarks>
    /// 桌面分区靠关闭系统的「显示桌面图标」让桌面变干净。程序都退出了还留着一个空桌面，
    /// 用户会以为文件没了——虽然右键桌面就能恢复，但不该让他自己去发现。
    /// 这里只调 Win32 SendMessage + 读注册表，不碰 COM/XAML，会话结束路径也能安全执行。
    /// </remarks>
    private static void RestoreDesktopIcons()
    {
        try { DesktopIconVisibilityService.SetHidden(false); }
        catch (Exception ex) { ErrorReporter.Log("App.RestoreDesktopIcons", ex); }
    }

    /// <summary>托盘「退出 WinTools」：普通上下文，可以读 XAML、摘托盘图标。</summary>
    internal static void ShutdownNow()
    {
        if (!IsShuttingDown)
        {
            IsShuttingDown = true;
            SaveConfigOnExit();
            (Current as App)?.TaskbarInfo?.Dispose();
            RestoreDesktopIcons();
            // 托盘图标由 Shell 持有，进程直接结束会在通知区域留下幽灵图标。
            try { (Current as App)?._trayIcon?.Dispose(); }
            catch (Exception ex) { ErrorReporter.Log("App.ShutdownNow.Tray", ex); }
        }
        Environment.Exit(0);
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
