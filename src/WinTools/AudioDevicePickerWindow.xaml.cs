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

public sealed partial class AudioDevicePickerWindow : Window
{
    private bool _loading, _busy, _closed;
    private int _refreshVersion;


    public AudioDevicePickerWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null);
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        SystemBackdrop = new MicaBackdrop();
        ThemeService.Register(this);
        WindowHelper.DisableWindowTransitions(this);
        WindowHelper.RemoveSystemWindowBorder(this);
        ApplyPopupFrame();
        Closed += (_, _) => { StopOutsideClicks(); _closed = true; ++_refreshVersion; ThemeService.Unregister(this); };
    }

    internal void ShowAt(int x, int bottom)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(x, bottom), DisplayAreaFallback.Nearest).WorkArea;
        var scale = Math.Max(1, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
        var width = Math.Min(area.Width, (int)(360 * scale));
        var height = Math.Min(area.Height, (int)(300 * scale));
        AppWindow.MoveAndResize(new RectInt32(Math.Clamp(x, area.X, area.X + area.Width - width),
            Math.Clamp(bottom - height - (int)(8 * scale), area.Y, area.Y + area.Height - height), width, height));
        Activate();
        ApplyPopupFrame();
        AppWindow.IsShownInSwitchers = false;
        StartOutsideClicks();
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
            Output.PlaceholderText = outputs.Count == 0 ? "没有可用扬声器" : "选择扬声器";
            Input.PlaceholderText = inputs.Count == 0 ? "没有可用麦克风" : "选择麦克风";
            Status.Text = "选择后同时用于默认音频和通话。";
        }
        catch (Exception ex) { ErrorReporter.Log("AudioPicker.Read", ex); if (!_closed) Status.Text = "无法读取设备，请重新打开或进入声音设置。"; }
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
        Output.IsEnabled = Input.IsEnabled = false;
        Status.Text = "正在切换…";
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
        if (error != null && !_closed) Status.Text = error;
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == Windows.System.VirtualKey.Escape) { HidePicker(); args.Handled = true; } }
    private void Settings_Click(object sender, RoutedEventArgs args)
    { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private void ApplyPopupFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Remove the classic non-client frame as well as the DWM outline.
        var style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, new IntPtr(style & ~0x00C40000L)); // WS_CAPTION | WS_THICKFRAME
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, no move/size/activation/Z-order
        WindowHelper.RemoveSystemWindowBorder(this);
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
