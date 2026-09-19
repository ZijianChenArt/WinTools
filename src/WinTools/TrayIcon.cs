using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinTools;

/// <summary>
/// 系统托盘图标（基于 H.NotifyIcon 的 TaskbarIcon）。菜单是真正的 WinUI
/// <see cref="MenuFlyout"/>，挂在隐藏小窗（带 Mica 背景）上，菜单获得毛玻璃外观。
/// **左键单击和右键都弹这个菜单**，主界面从菜单底部「设置」上方的「显示主界面」进。菜单里全是「点一下就执行」的动作，
/// 没有勾选开关——功能的开 / 关统一放在主界面的设置页里。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private Window? _trayWindow;
    private TaskbarIcon? _taskbarIcon;
    private bool _disposed;
    /// <summary>「同步桌面」项：分区功能没开时置灰，见 <see cref="ShowContextMenu"/>。</summary>
    private MenuFlyoutItem? _syncDesktopItem;
    /// <summary>「悬浮搜索」项：功能在设置里关掉时置灰。</summary>
    private MenuFlyoutItem? _spotlightItem;

    public event EventHandler? ShowMainRequested;
    public event EventHandler? SpotlightRequested;
    public event EventHandler? DragStashToggleRequested;
    public event EventHandler? ShowDesktopCardsRequested;
    public event EventHandler? SyncDesktopRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? QuotaRefreshRequested;

    internal void SetStatusText(string text)
    {
        if (_taskbarIcon == null) return;
        var tooltip = "WinTools\n" + text;
        _taskbarIcon.ToolTipText = tooltip.Length > 127 ? tooltip[..126] + "…" : tooltip;
    }

    internal bool QuickMenuFromLeft { get; private set; }
    internal void ShowQuickMenu() => ShowContextMenuCore(fromLeft: true);

    public TrayIcon()
    {
        BuildTrayWindow();
    }

    private void BuildTrayWindow()
    {
        _trayWindow = new Window
        {
            Title = "WinTools Tray"
        };
        try
        {
            _trayWindow.AppWindow.IsShownInSwitchers = false;
            _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        }
        catch { /* 早期失败也不影响主流程 */ }

        ApplyTrayBackdrop(_trayWindow);

        // 隐藏容器：1×1 透明 Grid；TaskbarIcon 是 FrameworkElement，必须挂在可视树上。
        var host = new Grid
        {
            Width = 1,
            Height = 1,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        _trayWindow.Content = host;

        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "WinTools",
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            MenuActivation = PopupActivationMode.None,
            // 左右键都弹同一个菜单，不用记"左键开窗、右键菜单"两套操作。
            LeftClickCommand = new RelayCommand(ShowContextMenu),
            RightClickCommand = new RelayCommand(ShowContextMenu)
        };

        // 加载应用图标
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icoPath))
            {
                _taskbarIcon.Icon = new System.Drawing.Icon(icoPath);
            }
        }
        catch
        {
            // 图标加载失败不阻塞托盘创建
        }

        _taskbarIcon.ContextFlyout = BuildContextMenu();

        host.Children.Add(_taskbarIcon);

        ThemeService.Register(_trayWindow);
        // 不在这里 Activate —— 直接 Activate 会让 1×1 隐形窗口在某些 Win11 版本
        // 短暂出现在任务栏 / Alt-Tab，造成"打开软件有弹窗"的体验问题。
        // 第一次 ShowContextMenu / ShowMainRequested 时再 Activate（见 EnsureShown 方法）。
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>在真正需要可见之前确保托盘窗口已 Activate + Show 过。
    /// 重复调用是幂等的——只在第一次实际激活。</summary>
    private void EnsureShown()
    {
        if (_trayWindow == null) return;
        try
        {
            // 1×1 窗口被创建后从未 Activate，AppWindow.Presenter 可能为 null
            // 这里直接 Show 一次（幂等），同时把标题 / 图标 / 任务栏归属设稳。
            _trayWindow.Activate();

            // Activate() 才是真正把这个宿主窗口显示出来的那一步。IsShownInSwitchers
            // 与 1×1 尺寸是在**首次显示之前**设的，部分 Win11 版本会在窗口显示时把它们
            // 重置，于是 Alt-Tab / 任务栏里会冒出一个标题为「WinTools Tray」的空窗口。
            // 显示后立刻重设一次，并挪到屏幕外，避免它以任何形式露出来。
            _trayWindow.AppWindow.IsShownInSwitchers = false;
            _trayWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
            _trayWindow.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        }
        catch { /* 早期失败也不影响主流程 */ }
    }

    private static void ApplyTrayBackdrop(Window window)
    {
        try
        {
            // 全应用统一只用 Mica，不回退亚克力。
            if (MicaController.IsSupported())
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }
        }
        catch
        {
            // 回退：透明背景
        }
    }

    private void ShowContextMenu()
        => ShowContextMenuCore(fromLeft: false);

    private void ShowContextMenuCore(bool fromLeft)
    {
        try
        {
            if (_taskbarIcon is null) return;
            QuickMenuFromLeft = fromLeft;
            if (_taskbarIcon.ContextFlyout is MenuFlyout menu)
                menu.Placement = fromLeft
                    ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft
                    : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight;
            EnsureShown();
            RefreshMenuState();
            var anchor = GetCursorPos(out var p)
                ? new System.Drawing.Point(p.X, p.Y)
                : default;
            _taskbarIcon.ShowContextMenu(anchor);
            FixMenuWindowSize(anchor, fromLeft);
        }
        catch
        {
            // 极少数系统状态（已销毁、UAC 提权）下调用失败，静默忽略
        }
    }

    /// <summary>菜单宽度（DIP）。放得下最长的「显示桌面分区」并留出呼吸感。</summary>
    private const double MenuWidthDip = 188;
    // 下面几项与 H.NotifyIcon 2.2.0 及 WinUI generic.xaml 的取值对应。
    private const double MenuItemHeightDip = 32;       // H.NotifyIcon 给每个菜单项强制设的 Height
    private const double MenuSeparatorHeightDip = 3;   // MenuFlyoutSeparatorHeight 1 + ThemePadding 上下各 1
    private const double MenuChromeHeightDip = 6;      // MenuFlyoutPresenter 上下 Padding 2 + 边框 1

    /// <summary>
    /// 纠正 H.NotifyIcon 给菜单宿主窗口算出的尺寸——不纠正的话菜单时宽时窄、还会冒出滚动条。
    /// </summary>
    /// <remarks>
    /// SecondWindow 模式下菜单以 Full 方式铺满一个独立小窗，小窗尺寸由库内部的
    /// <c>MeasureFlyout</c> 决定：它把每个菜单项单独 Measure 后取最大宽度、累加高度。两个缺陷：
    /// ① 首次弹出（以及换主题后）菜单项还没套上模板，量出来的宽度几乎为 0，菜单被挤窄；
    /// ② 分隔线和 Presenter 的内边距 / 边框从不计入，高度总差十来个 DIP，于是出现滚动条。
    /// 库在 <c>ShowContextMenu</c> 里同步完成 MoveAndResize，菜单要等窗口激活消息到达才真正
    /// ShowAt，所以紧接着按我们自己算的尺寸再摆一次窗口，菜单打开时用的就是正确尺寸。
    /// 靠近光标的那条边保持不动（任务栏在底部时就是底边），菜单依旧贴着托盘图标。
    /// </remarks>
    private void FixMenuWindowSize(System.Drawing.Point cursor, bool fromLeft)
    {
        try
        {
            if (_taskbarIcon?.ContextFlyout is not MenuFlyout menu) return;
            var handle = typeof(TaskbarIcon)
                .GetProperty("ContextMenuWindowHandle", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(_taskbarIcon) as IntPtr?;
            if (handle is not { } hwnd || hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var old)) return;

            var separators = menu.Items.Count(i => i is MenuFlyoutSeparator);
            var items = menu.Items.Count - separators;
            var heightDip = items * MenuItemHeightDip + separators * MenuSeparatorHeightDip + MenuChromeHeightDip;

            var monitor = MonitorFromPoint(new POINT { X = cursor.X, Y = cursor.Y }, MONITOR_DEFAULTTONEAREST);
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
            var width = (int)Math.Ceiling(MenuWidthDip * scale);
            var height = (int)Math.Ceiling(heightDip * scale);

            // 库把菜单摆在光标哪一侧，就保持贴光标的那条边不动。
            var x = fromLeft ? cursor.X : old.Left >= cursor.X - 1 ? old.Left : old.Right - width;
            var y = fromLeft ? cursor.Y - height : old.Top >= cursor.Y - 1 ? old.Top : old.Bottom - height;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                x = Math.Clamp(x, info.rcMonitor.Left, Math.Max(info.rcMonitor.Left, info.rcMonitor.Right - width));
                y = Math.Clamp(y, info.rcMonitor.Top, Math.Max(info.rcMonitor.Top, info.rcMonitor.Bottom - height));
            }

            // 只改位置尺寸，不碰 Z 序和激活状态——库靠窗口激活事件来弹出、失活来收起菜单。
            SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("TrayIcon.FixMenuWindowSize", ex);
        }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 托盘菜单：两组——上面是「快捷功能」，下面是「显示主界面 / 设置 / 退出」。
    /// </summary>
    /// <remarks>
    /// 每一项都是点一下就执行的动作，刻意不用 <see cref="ToggleMenuFlyoutItem"/>：
    /// 只要菜单里有一个可勾选项，WinUI 会给**所有**项预留勾选列，整个菜单被推宽、文字右移。
    /// 同理不显示快捷键文字，右侧那一列也会把菜单撑宽。
    /// </remarks>
    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false,
            // 托盘图标贴着任务栏，菜单必须向上弹；默认的向下会顶到任务栏再被系统翻转。
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
        };

        _spotlightItem = AddItem(menu, "悬浮搜索", FeatureIcons.Create(FeatureIcons.Search), () => SpotlightRequested?.Invoke(this, EventArgs.Empty));
        AddItem(menu, "悬浮暂存", FeatureIcons.Create(FeatureIcons.DragStash), () => DragStashToggleRequested?.Invoke(this, EventArgs.Empty));
        AddItem(menu, "显示桌面分区", FeatureIcons.Create(FeatureIcons.DesktopCards), () => ShowDesktopCardsRequested?.Invoke(this, EventArgs.Empty));
        // 与桌面分区页「更多」菜单里的同名项走同一条路径，文案保持一致。
        _syncDesktopItem = AddItem(menu, "同步桌面", new SymbolIcon(Symbol.Sync), () => SyncDesktopRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.Add(new MenuFlyoutSeparator());

        AddItem(menu, "刷新 Codex 额度", new SymbolIcon(Symbol.Refresh), () => QuotaRefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new MenuFlyoutSeparator());

        AddItem(menu, "显示主界面", new SymbolIcon(Symbol.Home), () => ShowMainRequested?.Invoke(this, EventArgs.Empty));
        AddItem(menu, "设置", FeatureIcons.Create(FeatureIcons.Settings), () => SettingsRequested?.Invoke(this, EventArgs.Empty));
        AddItem(menu, "退出", new SymbolIcon(Symbol.Cancel), () => ExitRequested?.Invoke(this, EventArgs.Empty));

        return menu;
    }

    /// <summary>加一个普通菜单项。<paramref name="onClick"/> 在点击时才读事件，构建菜单时订阅者还没挂上也没关系。</summary>
    private static MenuFlyoutItem AddItem(MenuFlyout menu, string text, IconElement icon, Action onClick)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = icon };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
        return item;
    }

    /// <summary>每次弹出前按当前配置刷新菜单的置灰状态。</summary>
    /// <remarks>
    /// 菜单只在启动时构建一次，而配置随时会变（设置页里改了开关），
    /// 所以动态部分必须在弹出前重算，不能在 <see cref="BuildContextMenu"/> 里一次写死。
    /// </remarks>
    private void RefreshMenuState()
    {
        try
        {
            var config = Services.SettingsService.Instance.Current;

            // 分区没开时同步无意义（设置页里点会弹"请先打开总开关"），托盘里直接置灰，
            // 不弹对话框——托盘菜单没有可用的 XamlRoot。
            // 「显示桌面分区」不置灰：分区没开时点它会顺带打开。
            if (_syncDesktopItem is not null)
                _syncDesktopItem.IsEnabled = config.EnableDesktopCard;

            if (_spotlightItem is not null)
                _spotlightItem.IsEnabled = config.EnableSpotlight;
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("TrayIcon.RefreshMenuState", ex);
        }
    }

    /// <summary>主题切换时刷新托盘背景。</summary>
    public void RefreshBackdrop()
    {
        if (_trayWindow is not null) ApplyTrayBackdrop(_trayWindow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _taskbarIcon?.Dispose(); } catch { }
        try { _trayWindow?.Close(); } catch { }
    }
}
