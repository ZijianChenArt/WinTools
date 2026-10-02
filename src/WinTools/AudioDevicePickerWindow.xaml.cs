using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinTools.Services;

namespace WinTools;

public sealed partial class AudioDevicePickerWindow : Window, IUiStyleShell
{
    private bool _loading, _busy, _closed;
    private int _refreshVersion;


    public AudioDevicePickerWindow()
    {
        InitializeComponent();
        WindowHelper.EnablePersistentMica(this); // 点开后焦点常被任务栏抢走，Mica 不能跟着变灰
        // 窗口外框走与「悬浮暂存」窗口相同的路径（透明标题栏 + 系统阴影 / 圆角 / 关闭按钮）；
        // 不再剥掉 WS_CAPTION / 描边，否则阴影和边缘会与暂存窗口不一样，顶上还会露出一条黑线。
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        WindowHelper.ConfigureTransparentTitleBar(this, AppTitleBar);
        WindowHelper.HookTitleBarPadding(this, AppTitleBar, LeftPaddingColumn, RightPaddingColumn);
        WindowHelper.DisableWindowTransitions(this);
        ThemeService.Register(this);
        UiStyleService.Register(this);
        ApplyUiStyleSurfaces();
        AppWindow.Closing += (_, e) =>
        {
            // 点系统关闭按钮只是收起，实例留给下次复用；进程退出时放行。
            if (App.IsShuttingDown) return;
            e.Cancel = true;
            HidePicker();
        };
        Closed += (_, _) => { StopAnimation(); StopOutsideClicks(); _closed = true; ++_refreshVersion; ThemeService.Unregister(this); UiStyleService.Unregister(this); };
    }

    /// <param name="x">弹窗左边缘（物理像素），与任务栏音频入口对齐。</param>
    /// <param name="taskbarTop">任务栏上沿（物理像素）。</param>
    internal void ShowAt(int x, int taskbarTop)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(x, taskbarTop - 1), DisplayAreaFallback.Nearest).WorkArea;
        // 缩放取目标显示器的，而不是窗口当前所在显示器的：预热后窗口停在屏幕外（-10000），
        // 那里的 DPI 可能与任务栏所在显示器不同，按它算高度会让内容在目标显示器上被裁掉一截。
        var scale = Math.Max(1, MonitorDpiAt(x, taskbarTop - 1) / 96.0);
        var width = Math.Min(area.Width, (int)(360 * scale));
        var height = Math.Min(area.Height, MeasureHeight(scale));
        var restY = Math.Clamp(taskbarTop - height - (int)(WindowHelper.TaskbarPopupGapDip * scale), area.Y, area.Y + area.Height - height);
        PrepareReveal(restY, scale);
        // 从终点下方起步，由 PlayReveal 滑到 restY。
        AppWindow.MoveAndResize(new RectInt32(Math.Clamp(x, area.X, area.X + area.Width - width),
            Animator.RevealStartY, width, height));
        Activate();
        AppWindow.IsShownInSwitchers = false;
        StartOutsideClicks();
        PlayReveal();
        _ = RefreshAsync();
    }

    /// <summary>窗口高度贴合内容（标题栏 + 正文），不留固定高度造成的空白。</summary>
    private int MeasureHeight(double scale)
    {
        const int WidthDip = 360, TitleBarDip = 36, FallbackDip = 220;
        try
        {
            Body.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
            var dip = TitleBarDip + Body.DesiredSize.Height;
            if (dip > TitleBarDip + 40) return (int)Math.Ceiling(dip * scale);
        }
        catch { /* 用兜底高度 */ }
        return (int)(FallbackDip * scale);
    }

    /// <summary>
    /// 在屏幕外完成首次 WinUI 初始化（加载 XAML、Mica、ComboBox 模板），否则第一次点击时这些都挤在滑入动画之前。
    /// 与悬浮暂存窗口的预热同理。
    /// </summary>
    internal void EnsureInitialized()
    {
        if (_warmed || _closed) return;
        _warmed = true;
        try
        {
            WindowHelper.SetWindowCloak(this, true);
            AppWindow.Move(new PointInt32(-10000, -10000));
            AppWindow.Show(false); // 不激活，不抢焦点
            WindowHelper.WhenRendered(this, () =>
            {
                if (!_closed && AppWindow.Position.X == -10000) AppWindow.Hide();
                WindowHelper.SetWindowCloak(this, false);
            });
            _ = RefreshAsync(); // 预先填好设备列表，第一次弹出时就是可选状态
        }
        catch (Exception ex) { ErrorReporter.Log("AudioPicker.Warmup", ex); }
    }
    private bool _warmed;

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        // 不再先把下拉框置灰再恢复：那会在弹出时看到「灰色 → 突然可选」的跳变。
        // 控件始终可用；设备列表没变就一个字都不动，变了才原地更新。
        try
        {
            var devices = await Task.Run(AudioDeviceService.Read);
            var signature = string.Join('|', devices.Select(d => $"{d.Id}/{d.Flow}/{d.Default}/{d.Name}"));
            if (_closed || version != _refreshVersion) return;
            if (signature == _shownSignature) { Status.Text = ""; Status.Visibility = Visibility.Collapsed; return; }
            // 滑入动画期间改 ComboBox 会触发布局，打断动画；等动画走完（最多 400ms）再填。
            for (var wait = 0; wait < 13 && Animator.IsBusy; wait++) await Task.Delay(30);
            if (_closed || version != _refreshVersion) return;
            _loading = true;
            _shownSignature = signature;
            var outputs = devices.Where(device => device.Flow == 0).ToList();
            var inputs = devices.Where(device => device.Flow == 1).ToList();
            Output.ItemsSource = outputs;
            Input.ItemsSource = inputs;
            Output.SelectedItem = outputs.FirstOrDefault(device => device.Default);
            Input.SelectedItem = inputs.FirstOrDefault(device => device.Default);
            Output.PlaceholderText = outputs.Count == 0 ? "没有可用扬声器" : "选择扬声器";
            Input.PlaceholderText = inputs.Count == 0 ? "没有可用麦克风" : "选择麦克风";
            Status.Text = ""; Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ErrorReporter.Log("AudioPicker.Read", ex); if (!_closed) { Status.Text = "无法读取设备，请重新打开或进入声音设置。"; Status.Visibility = Visibility.Visible; } }
        finally
        {
            if (!_closed && version == _refreshVersion)
            {
                _loading = false;
                Output.IsEnabled = Input.IsEnabled = !_busy;
            }
        }
    }
    private string? _shownSignature;

    private async void Device_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || _busy || (sender as ComboBox)?.SelectedItem is not AudioDeviceService.Device device) return;
        _busy = true;
        // 先记下选择再切换，避免后台守护在切换完成前把它改回旧设备。
        var cfg = SettingsService.Instance.Current;
        if (device.Flow == 0) cfg.AudioPreferredOutputId = device.Id; else cfg.AudioPreferredInputId = device.Id;
        ConfigService.Update(c => { if (device.Flow == 0) c.AudioPreferredOutputId = device.Id; else c.AudioPreferredInputId = device.Id; });
        Output.IsEnabled = Input.IsEnabled = false;
        Status.Text = "正在切换…"; Status.Visibility = Visibility.Visible;
        string? error = null;
        try
        {
            await Task.Run(() => AudioDeviceService.Select(device));
            var updated = await Task.Run(AudioDeviceService.Read);
            if (!updated.Any(item => item.Id == device.Id && item.Default && item.Communications))
                throw new InvalidOperationException("设备可能已断开。");
        }
        catch (Exception ex) { ErrorReporter.Log("AudioPicker.Select", ex); error = "切换未完成，请重试或进入声音设置。"; }
        finally { _busy = false; }
        if (_closed) return;
        await RefreshAsync();
        if (error != null && !_closed) { Status.Text = error; Status.Visibility = Visibility.Visible; }
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == Windows.System.VirtualKey.Escape) { HidePicker(); args.Handled = true; } }
    private void Settings_Click(object sender, RoutedEventArgs args)
    { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointInt32 point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    private uint MonitorDpiAt(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new PointInt32(x, y), 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) return dpi;
        }
        catch { /* 退回窗口自身 DPI */ }
        return GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }
    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica);
        WindowHelper.ApplyWindowBackdrop(this);
        WindowHelper.ApplyTitleBarButtonColors(this, theme);
    }
}
