namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private AudioDevicePickerWindow? _audioPicker;
    private System.Threading.Tasks.Task ShowAudioDevicesAsync()
    {
        if (_disposed || !GetWindowRect(_hwnd, out var bounds)) return System.Threading.Tasks.Task.CompletedTask;
        _audioPicker ??= new AudioDevicePickerWindow();
        if (_audioPicker.IsOpen) { _audioPicker.HidePicker(); return System.Threading.Tasks.Task.CompletedTask; }
        _audioPicker.IsToggleButtonAt = (x, y) => _visible && GetWindowRect(_hwnd, out var rect)
            && y >= rect.Top && y < rect.Bottom && ButtonAt(x - rect.Left) == 3;
        _audioPicker.ShowAt(bounds.Left + _deviceStart, bounds.Top);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
