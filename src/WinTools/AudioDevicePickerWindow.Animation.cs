using System;

namespace WinTools;

/// <summary>音频设备弹窗的出现 / 收起动效，节奏与实现见 <see cref="PopupAnimator"/>。</summary>
public sealed partial class AudioDevicePickerWindow
{
    private PopupAnimator? _animatorInstance;
    private PopupAnimator Animator => _animatorInstance ??= new PopupAnimator(this);
    private double _popupScale = 1;

    private void PrepareReveal(int restY, double scale)
    {
        _popupScale = scale;
        Animator.PrepareReveal(restY, scale);
    }

    /// <summary>等内容渲染出第一帧后再开始滑入，否则动画前段会淡入一块空白表面。</summary>
    private void PlayReveal()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowHelper.WhenRendered(this, () =>
        {
            if (_closed || !IsWindowVisible(hwnd)) return;
            Animator.PlayReveal();
        });
    }

    private void PlayDismiss() => Animator.PlayDismiss(_popupScale, () => AppWindow.Hide());

    private void StopAnimation() => _animatorInstance?.Stop();
}
