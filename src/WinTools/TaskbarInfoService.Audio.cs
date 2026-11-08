using System;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private AudioDevicePickerWindow? _audioPicker;
    private bool _audioWarmQueued;

    /// <summary>信息条第一次显示后，在空闲时预先创建音频弹窗，避免第一次点击时现建窗口造成掉帧。</summary>
    private void WarmAudioPicker()
    {
        if (_audioWarmQueued || !_showAudio) return;
        _audioWarmQueued = true;
        _dispatcher.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_disposed) return;
            try
            {
                _audioPicker ??= new AudioDevicePickerWindow();
                _audioPicker.EnsureInitialized();
            }
            catch (Exception ex) { WinTools.Services.ErrorReporter.Log("AudioPicker.Warmup", ex); }
        });
    }

    private System.Threading.Tasks.Task ShowAudioDevicesAsync()
    {
        if (_disposed || !GetWindowRect(_hwnd, out var bounds) || !GetWindowRect(_taskbarOwner, out var bar))
            return System.Threading.Tasks.Task.CompletedTask;
        _audioPicker ??= new AudioDevicePickerWindow();
        if (_audioPicker.IsOpen) { _audioPicker.HidePicker(); return System.Threading.Tasks.Task.CompletedTask; }
        _audioPicker.IsToggleButtonAt = (x, y) => _visible && GetWindowRect(_hwnd, out var rect)
            && y >= rect.Top && y < rect.Bottom && ButtonAt(x - rect.Left) == 3;
        _audioPicker.ShowAt(bounds.Left + _deviceStart, Math.Min(bounds.Top, bar.Top));
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
