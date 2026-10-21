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
    private int? _hoverAnchorX;
    private int _hoverAnchorButton = -1;
    private float _hoverAnchorFraction;

    private float HoverAnchorLocal(int width)
    {
        var slot = Array.IndexOf(_shownIcons, _hoverAnchorButton);
        if (slot >= 0) return _buttonStart + (slot + .5f) * _buttonWidth;
        return _hoverAnchorButton switch
        {
            3 when _deviceWidth > 0 => _deviceStart + _deviceWidth / 2f,
            4 when _codexDrawWidth > 0 => _codexStart + _codexDrawWidth / 2f,
            5 when _claudeDrawWidth > 0 => _claudeStart + _claudeDrawWidth / 2f,
            _ => width * _hoverAnchorFraction
        };
    }

    private void EnterOverlay(int localX)
    {
        _pointerInside = true;
        _leaveAfter = 0;
        if (!_floating) { SafeTick(); return; }
        if (GetWindowRect(_hwnd, out var rect))
        {
            _hoverAnchorButton = HitAt(localX);
            var width = Math.Max(1, rect.Right - rect.Left);
            _hoverAnchorFraction = Math.Clamp(localX / (float)width, 0, 1);
            _hoverAnchorX = rect.Left + (int)MathF.Round(HoverAnchorLocal(width));
        }
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
            if (_hoverTarget == FloatingIdleScale) _hoverAnchorX = null;
        }
        SafeTick();
    }

    private void ResetHover()
    {
        _hoverTimer.Stop();
        _floatingScale = _hoverFrom = _hoverTarget = FloatingIdleScale;
        _hoverAnchorX = null;
        _leaveAfter = 0;
        _pointerInside = false;
    }
}
