using System;
using System.Runtime.InteropServices;
using WinTools.Services;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private int? _floatCenterX, _floatBottomY;
    private int _resolvedFloatCenterX, _resolvedFloatBottomY;
    private bool _draggingFloat, _dragReleasePending;
    private POINT _floatDragStart;
    private int _floatDragCenterX, _floatDragBottomY;

    private bool IsFloatHandleAtCursor()
    {
        return _floating && _visible && GetCursorPos(out var point) && GetWindowRect(_hwnd, out var rect)
            && point.Y >= rect.Top && point.Y < rect.Bottom
            && point.X >= rect.Left && point.X < rect.Left + _buttonStart;
    }

    private bool HandleFloatingDrag(IntPtr hwnd, uint message, IntPtr wParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message == 0x0020 && (_draggingFloat || IsFloatHandleAtCursor())) // WM_SETCURSOR
        {
            SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32646))); // IDC_SIZEALL
            result = new IntPtr(1);
            return true;
        }
        if (message == 0x0201) // WM_LBUTTONDOWN
        {
            // 新的一次按下不会继承上次取消拖动留下的抬起屏蔽。
            _dragReleasePending = false;
            if (!FloatingButtons || !IsFloatHandleAtCursor() || !GetCursorPos(out var point)) return false;
            SetCapture(hwnd);
            if (GetCapture() != hwnd) return true;
            _draggingFloat = _dragReleasePending = true;
            _floatDragStart = point;
            _floatDragCenterX = _resolvedFloatCenterX;
            _floatDragBottomY = _resolvedFloatBottomY;
            // 拖动时冻结当前缩放，手柄和鼠标的相对位置不随悬停动画改变。
            _hoverTimer.Stop();
            _leaveAfter = 0;
            _hoverFrom = _hoverTarget = _floatingScale;
            _hoveredButton = -1;
            return true;
        }
        if (message == 0x0200 && _draggingFloat) // WM_MOUSEMOVE
        {
            if ((wParam.ToInt64() & 1) == 0) { StopFloatingDrag(resumeHover: true); return true; }
            if (GetCursorPos(out var point))
            {
                _floatCenterX = _floatDragCenterX + point.X - _floatDragStart.X;
                _floatBottomY = _floatDragBottomY + point.Y - _floatDragStart.Y;
                SafeTick();
            }
            return true;
        }
        if (message == 0x0202 && _dragReleasePending) // WM_LBUTTONUP
        {
            StopFloatingDrag(resumeHover: true);
            _dragReleasePending = false;
            return true; // 拖动结束不触发任何快捷入口。
        }
        if ((message is 0x0215 or 0x001F) && _draggingFloat) // WM_CAPTURECHANGED / WM_CANCELMODE
        {
            StopFloatingDrag(resumeHover: true);
            return true;
        }
        if (message == 0x02A3 && _draggingFloat) // WM_MOUSELEAVE
        {
            _trackingMouse = false;
            return true;
        }
        return false;
    }

    private void StopFloatingDrag(bool resumeHover = false)
    {
        if (!_draggingFloat) return;
        _draggingFloat = false;
        if (GetCapture() == _hwnd) ReleaseCapture();
        SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512))); // IDC_ARROW
        // 保存实际可达位置，显示器变化或拖过边缘时不会留下屏外坐标。
        _floatCenterX = _resolvedFloatCenterX;
        _floatBottomY = _resolvedFloatBottomY;
        try
        {
            var settings = SettingsService.Instance;
            settings.Current.TaskbarFloatCenterX = _floatCenterX;
            settings.Current.TaskbarFloatBottomY = _floatBottomY;
            settings.FlushNow();
        }
        catch (Exception ex) { ErrorReporter.Log("TaskbarInfo.DragSave", ex); }
        if (resumeHover && _visible && _enabled && !_disposed)
        {
            _pointerInside = GetCursorPos(out var point) && GetWindowRect(_hwnd, out var rect)
                && point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
            AnimateHover(_pointerInside ? 1 : FloatingIdleScale);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
}
