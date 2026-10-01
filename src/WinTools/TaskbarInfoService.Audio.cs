using System;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private AudioDevicePickerWindow? _audioPicker;
    private string _audioName = "";
    private bool _readingAudio;
    private DateTime _nextAudioRead = DateTime.MinValue;

    /// <summary>当前默认输出设备名，显示在音频入口上；每 5 秒后台读取一次。</summary>
    private async System.Threading.Tasks.Task RefreshAudioNameAsync()
    {
        _readingAudio = true;
        _nextAudioRead = DateTime.UtcNow.AddSeconds(5);
        try
        {
            var devices = await System.Threading.Tasks.Task.Run(WinTools.Services.AudioDeviceService.Read);
            if (!_disposed) _audioName = devices.Find(device => device.Flow == 0 && device.Default)?.Name ?? "";
        }
        catch (Exception) { if (!_disposed) _audioName = ""; }
        finally { _readingAudio = false; }
    }

    private System.Threading.Tasks.Task ShowAudioDevicesAsync()
    {
        if (_disposed || !GetWindowRect(_hwnd, out var bounds) || !GetWindowRect(_taskbarOwner, out var bar))
            return System.Threading.Tasks.Task.CompletedTask;
        _audioPicker ??= new AudioDevicePickerWindow();
        if (_audioPicker.IsOpen) { _audioPicker.HidePicker(); return System.Threading.Tasks.Task.CompletedTask; }
        _audioPicker.IsToggleButtonAt = (x, y) => _visible && GetWindowRect(_hwnd, out var rect)
            && y >= rect.Top && y < rect.Bottom && ButtonAt(x - rect.Left) == 3;
        _audioPicker.ShowAt(bounds.Left + _deviceStart, bar.Top);
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
