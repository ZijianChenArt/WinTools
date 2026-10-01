using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinTools;

public sealed partial class AudioDevicePickerWindow
{
    private IntPtr _mouseHook;
    private MouseHook? _mouseCallback;
    internal Func<int, int, bool>? IsToggleButtonAt { get; set; }
    /// <summary>正在播放收起动画时视为已关闭，这样再点一次任务栏按钮会重新打开而不是被当成“关闭”。</summary>
    internal bool IsOpen => !_closed && !(_animatorInstance?.IsHiding ?? false) && IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(this));

    internal void HidePicker()
    {
        StopOutsideClicks();
        Output.IsDropDownOpen = Input.IsDropDownOpen = false;
        if (!IsOpen) return;
        PlayDismiss();
    }

    private void StartOutsideClicks()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseCallback = OnGlobalMouse;
        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            Services.ErrorReporter.Log("AudioPicker.OutsideClick", new System.ComponentModel.Win32Exception());
    }

    private void StopOutsideClicks()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr OnGlobalMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (message.ToInt64() == 0x201 || message.ToInt64() == 0x204))
        {
            var point = Marshal.PtrToStructure<MousePoint>(data);
            // Never consume the click or query XAML in the system hook.
            var target = GetAncestor(WindowFromPoint(point), 2);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsOpen || IsToggleButtonAt?.Invoke(point.X, point.Y) == true) return;
                var own = WinRT.Interop.WindowNative.GetWindowHandle(this);
                for (var window = target; window != IntPtr.Zero; window = GetWindow(window, 4))
                    if (window == own) return;
                // WinUI ComboBox flyouts can use a separate popup host without an owner chain.
                GetWindowThreadProcessId(target, out var pid);
                if (pid == Environment.ProcessId)
                {
                    var name = new StringBuilder(128);
                    GetClassName(target, name, name.Capacity);
                    if (name.ToString().Contains("PopupWindow", StringComparison.Ordinal)) return;
                }
                HidePicker();
            });
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MousePoint { public int X, Y; }
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(MousePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
}
