using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace WinTools;

/// <summary>
/// 快捷设置悬浮面板：显示当前状态按钮，点击直接执行切换；样式与悬浮暂存窗口一致。
/// </summary>
public sealed partial class QuickSettingsPopupWindow : Window, IUiStyleShell
{
    private const int MinWindowWidthDip = 1;
    private const int GwlpWndproc = -4;
    private const uint WmActivate = 0x0006;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int WaInactive = 0;
    private const int MinTrackSizePx = 8;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr NativeSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private readonly Dictionary<string, Button> _actionButtons = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>面板按钮底部态是否激活（网络=WiFi，声音=静音）；null 表示无法识别。已知态点击后本地翻转，避免系统 API 读数滞后。</summary>
    private readonly Dictionary<string, bool?> _bottomStateActive = new(StringComparer.OrdinalIgnoreCase);
    private WNDPROC? _originalWndProc;
    private WNDPROC? _popupWndProc;
    private bool _isVisible;

    public QuickSettingsPopupWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        AppWindow.SetIcon("Assets\\AppIcon.ico");
    }

    public bool IsPopupVisible => _isVisible;

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = true;
                p.IsResizable = false;
                p.IsMinimizable = false;
                p.IsMaximizable = false;
            }

            WindowHelper.ConfigurePopupTitleBar(this);
            WindowHelper.DisableWindowTransitions(this);
            ApplyUiStyleSurfaces();

            AppWindow.IsShownInSwitchers = false;
            HookDeactivateOnClickOutside();

            ShellRoot.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                    HidePopup();
            };

            Activated += (_, _) => _isVisible = true;
            AppWindow.Closing += (_, e) =>
            {
                e.Cancel = true;
                HidePopup();
            };
        }
        catch { /* ignore */ }
    }

    private void HookDeactivateOnClickOutside()
    {
        var hwnd = (HWND)WindowNative.GetWindowHandle(this);
        _popupWndProc = PopupWndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_popupWndProc);
        var oldProc = NativeSetWindowLongPtr(hwnd, GwlpWndproc, newProcPtr);
        _originalWndProc = Marshal.GetDelegateForFunctionPointer<WNDPROC>(oldProc);
    }

    private LRESULT PopupWndProc(HWND hwnd, uint uMsg, WPARAM wParam, LPARAM lParam)
    {
        if (uMsg == WmActivate && (wParam.Value & 0xFFFF) == WaInactive)
            DispatcherQueue.TryEnqueue(HidePopup);

        var result = _originalWndProc != null
            ? _originalWndProc(hwnd, uMsg, wParam, lParam)
            : (LRESULT)IntPtr.Zero;

        // 必须在原始窗口过程填好默认值之后再覆写，否则会被系统按窗口样式重新计算覆盖。
        // 放宽系统默认的最小窗口尺寸，使客户区可缩到内容实际大小（否则上下会被强制留白）。
        if (uMsg == WmGetMinMaxInfo && lParam.Value != IntPtr.Zero)
        {
            // MINMAXINFO.ptMinTrackSize 位于偏移 24(x)/28(y)
            Marshal.WriteInt32(lParam.Value, 24, MinTrackSizePx);
            Marshal.WriteInt32(lParam.Value, 28, MinTrackSizePx);
        }

        return result;
    }

    private void BuildActionButtons()
    {
        FeatureListPanel.Children.Clear();
        _actionButtons.Clear();
        _bottomStateActive.Clear();

        var cfg = ConfigService.Load();
        ApplyPaddingFromConfig(cfg);
        foreach (var feature in QuickSettingFeatures.All)
        {
            if (!feature.GetEnabled(cfg)) continue;

            var button = new Button
            {
                Width = 104,
                Height = 104,
                Padding = new Thickness(12),
                FontSize = 15,
                CornerRadius = new CornerRadius(12),
                Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush,
                BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = feature.Id,
                Content = "…",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, feature.DisplayName);
            ToolTipService.SetToolTip(button, feature.Description);
            button.Click += FeatureButton_Click;
            FeatureListPanel.Children.Add(button);
            _actionButtons[feature.Id] = button;
        }

        if (_actionButtons.Count == 0)
        {
            FeatureListPanel.Children.Add(new TextBlock
            {
                Text = "未启用任何功能",
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    private void ApplyPaddingFromConfig(Config cfg)
    {
        ShellRoot.Padding = new Thickness(
            Math.Max(0, cfg.QuickPanelPaddingLeft),
            Math.Max(0, cfg.QuickPanelPaddingTop),
            Math.Max(0, cfg.QuickPanelPaddingRight),
            Math.Max(0, cfg.QuickPanelPaddingBottom));
    }

    /// <summary>用最新配置刷新面板内边距，并在可见时重新适配窗口尺寸。</summary>
    public void ApplyPanelPadding()
    {
        ApplyPaddingFromConfig(ConfigService.Load());
        if (_isVisible)
        {
            ShellRoot.UpdateLayout();
            SizeToContentAndCenter();
        }
    }

    private async Task RefreshButtonLabelsAsync()
    {
        await SyncButtonLabelsFromSystemAsync();
        // Radio / 音频状态有时在窗口刚显示时未就绪，稍后再读一次
        await Task.Delay(250);
        await SyncButtonLabelsFromSystemAsync();
    }

    private async Task SyncButtonLabelsFromSystemAsync()
    {
        foreach (var (featureId, button) in _actionButtons)
        {
            var bottomActive = await QuickSettingActions.QueryBottomStateActiveAsync(featureId);
            _bottomStateActive[featureId] = bottomActive;
            SetButtonLabel(button, featureId, bottomActive);
        }
    }

    private static void SetButtonLabel(Button button, string featureId, bool? bottomIsActive) =>
        button.Content = QuickSettingActions.BuildPanelLabel(featureId, bottomIsActive);

    private void SizeToContentAndCenter()
    {
        try
        {
            // 必须用 DesiredSize 测量，不能用 ActualWidth/Height（会随当前窗口尺寸收缩，越点越小）
            ShellRoot.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var dipW = Math.Max(MinWindowWidthDip, ShellRoot.DesiredSize.Width);
            var dipH = Math.Max(1, ShellRoot.DesiredSize.Height);
            var scale = ShellRoot.XamlRoot?.RasterizationScale ?? 1.0;
            var clientW = (int)Math.Ceiling(dipW * scale);
            var clientH = (int)Math.Ceiling(dipH * scale);

            // 先用 ResizeClient 让宽度精确贴合内容（同时建立有效的窗口/客户区尺寸用于计算边框）。
            AppWindow.ResizeClient(new SizeInt32(clientW, clientH));

            // AppWindow.ResizeClient 内部存在最小高度夹制（约 126 DIP），高度无法缩到内容大小，
            // 导致上下被强制留白。改用 Win32 SetWindowPos 直接按“客户区 + 边框”设定外框尺寸绕过该夹制
            // （配合已放宽的 WM_GETMINMAXINFO 最小追踪尺寸）。
            var frameW = Math.Max(0, AppWindow.Size.Width - AppWindow.ClientSize.Width);
            var frameH = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            var hwnd = WindowNative.GetWindowHandle(this);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, clientW + frameW, clientH + frameH,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);

            WindowHelper.CenterOnScreen(this);
        }
        catch { /* ignore */ }
    }

    public async Task ReloadPanelAsync()
    {
        BuildActionButtons();
        await RefreshButtonLabelsAsync();
        SizeToContentAndCenter();
    }

    public void ShowAtCenter()
    {
        try
        {
            _ = ShowAtCenterAsync();
        }
        catch { /* ignore */ }
    }

    private async Task ShowAtCenterAsync()
    {
        // 先 Cloak 隐藏，渲染完成后再显示，避免白色闪动
        WindowHelper.SetWindowCloak(this, true);
        BuildActionButtons();
        AppWindow.Show();
        Activate();
        _isVisible = true;

        // 窗口显示后再读系统状态（Radio / 音频 API 此时更可靠）
        await RefreshButtonLabelsAsync();

        // 布局完成后再精确适配一次尺寸
        await Task.Yield();
        ShellRoot.UpdateLayout();
        SizeToContentAndCenter();

        WindowHelper.UncloakWhenRendered(this);
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        PopupSurface.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica, elevated: true);
        PopupSurface.BorderBrush = WindowHelper.GetCodexBorderBrush(theme);
        WindowHelper.ApplyWindowBackdrop(this);
    }

    public void HidePopup()
    {
        try
        {
            if (!_isVisible) return;
            AppWindow.Hide();
            _isVisible = false;
        }
        catch { /* ignore */ }
    }

    public void Toggle()
    {
        if (_isVisible)
            HidePopup();
        else
            ShowAtCenter();
    }

    private async void FeatureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string featureId }) return;
        if (!_actionButtons.TryGetValue(featureId, out var button)) return;

        if (!_bottomStateActive.TryGetValue(featureId, out var current))
            current = await QuickSettingActions.QueryBottomStateActiveAsync(featureId);

        if (!await QuickSettingActions.ExecuteAsync(featureId)) return;

        // 已知态：本地翻转避免 API 读数滞后；无法识别：重新读系统（仍可能为 null）
        var next = current is bool known
            ? !known
            : await QuickSettingActions.QueryBottomStateActiveAsync(featureId);
        _bottomStateActive[featureId] = next;
        SetButtonLabel(button, featureId, next);
    }
}
