using System;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private AudioDevicePickerWindow? _audioPicker;
    private bool _audioWarmQueued;

    /// <summary>信息条第一次显示后，在空闲时预先创建音频弹窗，避免第一次点击时现建窗口造成掉帧。</summary>
    private void WarmAudioPicker()
    {
        if (_audioWarmQueued || Array.IndexOf(_iconIds, AudioId) < 0) return;
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

    internal void ShowAudioDevices()
    {
        if (Array.IndexOf(_iconIds, AudioId) < 0
            || (GetShortcutAnchor(AudioId) ?? GetShortcutAnchor(MenuId)) is not { } anchor) return;
        _audioPicker ??= new AudioDevicePickerWindow();
        if (_audioPicker.IsOpen) { _audioPicker.HidePicker(); return; }
        _audioPicker.IsToggleButtonAt = (x, y) => _visible && GetWindowRect(_hwnd, out var rect)
            && y >= rect.Top && y < rect.Bottom && ButtonAt(x - rect.Left) == AudioId;
        _audioPicker.ShowAt(anchor.CenterX - _buttonWidth / 2, anchor.Top);
    }
}
