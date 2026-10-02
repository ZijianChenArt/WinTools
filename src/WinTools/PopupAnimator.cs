using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WinTools;

/// <summary>
/// 任务栏弹出窗口的出现 / 收起动效：Win11 任务栏弹出面板的节奏——从下方滑入并淡入，收起时下沉淡出。
/// 透明度必须做在窗口级（WS_EX_LAYERED + SetLayeredWindowAttributes）：弹窗的可见外观由 Mica 背景画出，
/// 它在 XAML 内容之下，改 XAML Opacity 对它无效。分层样式与 Mica 不能共存，所以只在动画期间挂上。
/// </summary>
internal sealed class PopupAnimator
{
    private const int OpenDurationMs = 250;
    private const int CloseDurationMs = 150;
    private const int SlideDip = 12;

    private readonly Window _window;
    private DispatcherQueueTimer? _timer;
    private bool _revealPending;
    private byte _alpha = 255;
    private int _restY;
    private int _slidePx;

    internal PopupAnimator(Window window) => _window = window;

    /// <summary>正在播放收起动画。</summary>
    internal bool IsHiding { get; private set; }

    /// <summary>已准备或正在播放动画；此期间窗口位置由动画控制，调用方不应把移动当成用户拖动。</summary>
    internal bool IsBusy => _revealPending || _timer != null;

    /// <summary>滑入的起点 Y（物理像素）：窗口应先放在这里再显示。</summary>
    internal int RevealStartY => _restY + _slidePx;

    /// <summary>窗口在入场前被调高 / 调矮时，同步移动动画终点（向上为负）。</summary>
    internal void ShiftRest(int dy) => _restY += dy;

    /// <summary>decelerate：起步最快、末尾极缓。</summary>
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>accelerate：起步缓、越到后面越快，用于收起。</summary>
    private static double EaseIn(double t) => t * t * t;

    private IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(_window);

    /// <summary>窗口露面前把整窗透明度压到 0，避免显示时闪一帧实体窗口或白底。</summary>
    /// <param name="restY">动画终点 Y（物理像素）。</param>
    internal void PrepareReveal(int restY, double scale)
    {
        Stop();
        IsHiding = false;
        _revealPending = true;
        _restY = restY;
        _slidePx = (int)Math.Round(SlideDip * scale);
        SetLayered(true);
        SetAlpha(0);
    }

    /// <summary>从 <see cref="RevealStartY"/> 滑到终点并淡入。应在内容渲染出第一帧之后调用，否则前段淡入的是空白表面。</summary>
    internal void PlayReveal()
    {
        if (!_revealPending) return; // 期间已被 Reset 或收起，放弃这次入场
        _revealPending = false;
        Animate(RevealStartY, _restY, _alpha, 255, OpenDurationMs, EaseOut,
            () => SetLayered(false)); // 终点不透明：立刻摘掉分层，把 Mica 还回来
    }

    /// <summary>下沉淡出，结束后调用 <paramref name="hide"/> 真正隐藏窗口。</summary>
    internal void PlayDismiss(double scale, Action hide)
    {
        // 入场途中收起：以入场终点为基准下沉，避免停在半路的位置再往下掉一截。
        var fromY = _window.AppWindow.Position.Y;
        var baseY = IsBusy ? _restY : fromY;
        _revealPending = false;
        _slidePx = (int)Math.Round(SlideDip * scale);
        IsHiding = true;
        Animate(fromY, baseY + _slidePx, _alpha, 0, CloseDurationMs, EaseIn, () =>
        {
            IsHiding = false;
            hide();
            // 隐藏之后再恢复不透明，顺序反了会在末尾闪一下完整弹窗。
            SetAlpha(255);
            SetLayered(false);
        });
    }

    /// <summary>立即停止动画并恢复为普通不透明窗口（不改位置、不隐藏）。</summary>
    internal void Reset()
    {
        Stop();
        _revealPending = false;
        IsHiding = false;
        try
        {
            SetAlpha(255);
            SetLayered(false);
        }
        catch { /* 窗口已销毁 */ }
    }

    internal void Stop()
    {
        _timer?.Stop();
        _timer = null;
        if (_rendering != null)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _rendering;
            _rendering = null;
        }
    }

    // 每个渲染帧推进一次（与显示器刷新对齐）。16ms 的 DispatcherQueueTimer 实际精度约 15.6ms，
    // 帧间隔会在 16 / 31ms 间来回跳，肉眼就是掉帧。慢速定时器只作兜底：Rendering 不触发时动画仍能走完。
    private EventHandler<object>? _rendering;

    private void Animate(int fromY, int toY, byte fromAlpha, byte toAlpha, int durationMs,
        Func<double, double> ease, Action completed)
    {
        Stop();
        try
        {
            SetLayered(true);
            SetAlpha(fromAlpha);
            var x = _window.AppWindow.Position.X;
            var clock = Stopwatch.StartNew();
            var timer = _window.DispatcherQueue.CreateTimer();
            _timer = timer;
            var lastY = int.MinValue;
            byte lastAlpha = fromAlpha;
            void Step()
            {
                var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
                var eased = ease(progress);
                var y = (int)Math.Round(fromY + (toY - fromY) * eased);
                var alpha = (byte)Math.Round(fromAlpha + (toAlpha - fromAlpha) * eased);
                if (y != lastY) { _window.AppWindow.Move(new PointInt32(x, y)); lastY = y; }
                if (alpha != lastAlpha) { SetAlpha(alpha); lastAlpha = alpha; }
                if (progress < 1) return;
                Stop();
                completed();
            }
            _rendering = (_, _) => { if (ReferenceEquals(_timer, timer)) Step(); };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _rendering;
            timer.Interval = TimeSpan.FromMilliseconds(33);
            timer.IsRepeating = true;
            timer.Tick += (s, _) => { if (!ReferenceEquals(_timer, s)) { s.Stop(); return; } Step(); };
            timer.Start();
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("PopupAnimator.Animate", ex);
            Stop();
            try { _window.AppWindow.Move(new PointInt32(_window.AppWindow.Position.X, toY)); } catch { /* ignore */ }
            completed();
        }
    }

    private void SetLayered(bool layered)
    {
        var hwnd = Hwnd;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        var updated = layered ? style | WS_EX_LAYERED : style & ~WS_EX_LAYERED;
        if (updated != style) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(updated));
    }

    private void SetAlpha(byte alpha)
    {
        _alpha = alpha;
        SetLayeredWindowAttributes(Hwnd, 0, alpha, LWA_ALPHA);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
}
