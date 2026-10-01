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
        var scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var width = Math.Min(area.Width, (int)(360 * scale));
        var height = Math.Min(area.Height, (int)(300 * scale));
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

    private async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        _loading = true;
        Output.IsEnabled = Input.IsEnabled = false;
        try
        {
            var devices = await Task.Run(AudioDeviceService.Read);
            if (_closed || version != _refreshVersion) return;
            var outputs = devices.Where(device => device.Flow == 0).ToList();
            var inputs = devices.Where(device => device.Flow == 1).ToList();
            Output.ItemsSource = outputs;
            Input.ItemsSource = inputs;
            Output.SelectedItem = outputs.FirstOrDefault(device => device.Default);
            Input.SelectedItem = inputs.FirstOrDefault(device => device.Default);
            _loading = true;
            LockCheck.IsChecked = SettingsService.Instance.Current.AudioLockDevices;
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
    private void LockCheck_Changed(object sender, RoutedEventArgs args)
    {
        if (_loading) return;
        var locked = LockCheck.IsChecked == true;
        SettingsService.Instance.Current.AudioLockDevices = locked;
        ConfigService.Update(c => c.AudioLockDevices = locked);
    }
    private void Settings_Click(object sender, RoutedEventArgs args)
    { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica);
        WindowHelper.ApplyWindowBackdrop(this);
        WindowHelper.ApplyTitleBarButtonColors(this, theme);
    }
}
