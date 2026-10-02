using System;
using System.Runtime.InteropServices;

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
            FitHeightToContent();
            Animator.PlayReveal();
        });
    }

    /// <summary>
    /// 按真实布局核对窗口高度：入场前预估用的是目标显示器的缩放，窗口落到那里后若实际缩放不同，
    /// 内容会比窗口高而被裁。不够就向上加高（下沿位置不变）。
    /// </summary>
    private void FitHeightToContent()
    {
        try
        {
            var scale = Content.XamlRoot?.RasterizationScale ?? _popupScale;
            var needed = (int)Math.Ceiling((TitleBarDip + Body.ActualHeight) * scale);
            var size = AppWindow.Size;
            var pos = AppWindow.Position;
            // 窗口矩形底部有一圈不可见的边框（约 8 像素），内容最下面那段会落在它下面被裁掉，
            // 所以要以 DWM 报告的「可见边界」为准，而不是 AppWindow.Size。
            var diff = needed - size.Height;
            if (DwmGetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 9, out var frame, Marshal.SizeOf<FrameRect>()) == 0)
                diff = pos.Y + needed - frame.Bottom + 2 * DwmBorderPx; // 上下各有一条 1 像素的系统边框，内容夹在中间
            if (diff <= 0) return;
            // 向上加高：可见下沿（与任务栏的间距）保持不变。
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(pos.X, pos.Y - diff, size.Width, size.Height + diff));
            Animator.ShiftRest(-diff);
        }
        catch (Exception ex) { Services.ErrorReporter.Log("AudioPicker.FitHeight", ex); }
    }
    private const int TitleBarDip = 36;
    private const int DwmBorderPx = 1;

    [StructLayout(LayoutKind.Sequential)] private struct FrameRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out FrameRect value, int size);

    private void PlayDismiss() => Animator.PlayDismiss(_popupScale, () => AppWindow.Hide());

    private void StopAnimation() => _animatorInstance?.Stop();
}
