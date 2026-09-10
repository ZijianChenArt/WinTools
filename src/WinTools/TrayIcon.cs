using System;
using System.IO;
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
/// 系统托盘图标（基于 H.NotifyIcon 的 TaskbarIcon）。右键菜单是真正的 WinUI
/// <see cref="MenuFlyout"/>，挂在隐藏小窗（带 Mica 背景）上，菜单获得毛玻璃外观。
/// 双击托盘 = 唤起主窗口。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private Window? _trayWindow;
    private TaskbarIcon? _taskbarIcon;
    private bool _disposed;
    /// <summary>「同步桌面」项：分区功能没开时置灰，见 <see cref="ShowContextMenu"/>。</summary>
    private MenuFlyoutItem? _syncDesktopItem;

    public event EventHandler? ShowMainRequested;
    public event EventHandler? DragStashToggleRequested;
    public event EventHandler? SyncDesktopRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;

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
            ToolTipText = "WinTools · 双击打开",
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            MenuActivation = PopupActivationMode.None,
            LeftClickCommand = new RelayCommand(() => ShowMainRequested?.Invoke(this, EventArgs.Empty)),
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
    {
        try
        {
            if (_taskbarIcon is null) return;
            EnsureShown();
            // 分区没开时同步无意义（设置页里点会弹"请先打开总开关"），托盘里直接置灰，
            // 不弹对话框——托盘菜单没有可用的 XamlRoot。
            if (_syncDesktopItem is not null)
                _syncDesktopItem.IsEnabled = Services.SettingsService.Instance.Current.EnableDesktopCard;
            var anchor = GetCursorPos(out var p)
                ? new System.Drawing.Point(p.X, p.Y)
                : default;
            _taskbarIcon.ShowContextMenu(anchor);
        }
        catch
        {
            // 极少数系统状态（已销毁、UAC 提权）下调用失败，静默忽略
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false,
            // 托盘图标贴着任务栏，菜单必须向上弹；默认的向下会顶到任务栏再被系统翻转。
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
        };

        var showMain = new MenuFlyoutItem
        {
            Text = "显示主窗口",
            Icon = new SymbolIcon(Symbol.Home)
        };
        showMain.Click += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(showMain);

        var toggleStash = new MenuFlyoutItem
        {
            Text = "显示悬浮暂存",
            Icon = new FontIcon { Glyph = "\uE840" }
        };
        toggleStash.Click += (_, _) => DragStashToggleRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(toggleStash);

        // 与桌面分区页「更多」菜单里的同名项走同一条路径，文案保持一致。
        _syncDesktopItem = new MenuFlyoutItem
        {
            Text = "同步桌面",
            Icon = new SymbolIcon(Symbol.Sync)
        };
        _syncDesktopItem.Click += (_, _) => SyncDesktopRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_syncDesktopItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var settings = new MenuFlyoutItem
        {
            Text = "设置…",
            Icon = new SymbolIcon(Symbol.Setting)
        };
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(settings);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exit = new MenuFlyoutItem
        {
            Text = "退出 WinTools",
            Icon = new SymbolIcon(Symbol.Cancel)
        };
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exit);

        return menu;
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
