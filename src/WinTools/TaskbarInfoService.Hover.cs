using System;
using Microsoft.UI.Dispatching;
using WinTools.Services;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private readonly DispatcherQueueTimer _hoverTimer;
    private float _floatingScale = FloatingIdleScale;
    private float _hoverFrom = FloatingIdleScale, _hoverTarget = FloatingIdleScale;
    private long _hoverStarted, _leaveAfter;
    private void EnterOverlay()
    {
        _pointerInside = true;
        _leaveAfter = 0;
        if (!_floating) { SafeTick(); return; }
        AnimateHover(1);
    }

    private void AnimateHover(float target)
    {
        if (_hoverTarget == target && _hoverTimer.IsRunning) return;
        _hoverFrom = _floatingScale;
        _hoverTarget = target;
        _hoverStarted = Environment.TickCount64;
        _hoverTimer.Start();
        SafeTick();
    }

    private void HoverFrame()
    {
        if (_disposed || !_enabled || !_visible) { ResetHover(); return; }
        if (_leaveAfter != 0 && Environment.TickCount64 >= _leaveAfter)
        {
            _leaveAfter = 0;
            AnimateHover(FloatingIdleScale);
        }
        var elapsed = Environment.TickCount64 - _hoverStarted;
        _floatingScale = TaskbarHoverMotion.ScaleAt(_hoverFrom, _hoverTarget, elapsed);
        if (elapsed >= 220 && _leaveAfter == 0)
        {
            _hoverTimer.Stop();
        }
        SafeTick();
    }

    private void ResetHover()
    {
        _hoverTimer.Stop();
        _floatingScale = _hoverFrom = _hoverTarget = FloatingIdleScale;
        _leaveAfter = 0;
        _pointerInside = false;
    }
}
