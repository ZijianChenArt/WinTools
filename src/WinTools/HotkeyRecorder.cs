using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTools;

/// <summary>
/// 把快捷键输入框变成「录制」模式：聚焦后直接按下组合键即可，不用手打。
/// 用低级键盘钩子而不是 KeyDown 事件，因为 Win、Alt+Space 这类组合在窗口层面拦不住，
/// 会被系统抢先处理（弹出开始菜单、系统菜单）。钩子只在输入框拿到焦点期间存在。
/// </summary>
internal sealed class HotkeyRecorder
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private readonly TextBox _box;
    private readonly bool _allowModifierOnly;
    private readonly HookProc _proc;
    private IntPtr _hook;
    private string _before = "";
    private HotkeyModifiers _held;   // 当前按住的修饰键
    private HotkeyModifiers _peak;   // 本次录制期间同时按住过的修饰键（仅修饰键组合用）
    private bool _hadMainKey;

    private HotkeyRecorder(TextBox box, bool allowModifierOnly)
    {
        _box = box;
        _allowModifierOnly = allowModifierOnly;
        _proc = Callback;
    }

    /// <param name="allowModifierOnly">是否允许只有修饰键的组合（如 Ctrl+Win）；语音小球的输入法快捷键需要，全局注册的快捷键不行。</param>
    internal static void Attach(TextBox box, bool allowModifierOnly)
    {
        var recorder = new HotkeyRecorder(box, allowModifierOnly);
        box.IsReadOnly = true;
        box.PlaceholderText = "点击后直接按下快捷键";
        box.GotFocus += (_, _) => recorder.Start();
        box.PointerPressed += (_, _) => recorder.Start();
        box.LostFocus += (_, _) => recorder.Stop();
        box.Unloaded += (_, _) => recorder.Stop();
    }

    private void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _before = _box.Text;
        _held = _peak = 0;
        _hadMainKey = false;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        _box.Text = "";
        _box.PlaceholderText = "请按下快捷键…（Esc 取消，退格清除）";
    }

    private void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _box.PlaceholderText = "点击后直接按下快捷键";
        if (_box.Text.Length == 0 && _before.Length > 0 && !_cleared) _box.Text = _before;
        _cleared = false;
    }

    private bool _cleared;

    /// <summary>录完一次：拿掉焦点，钩子随之卸载，之后的按键恢复正常。</summary>
    private void Finish(string? text)
    {
        if (text != null) _box.Text = text;
        _cleared = text == "";
        Stop(); // 输入框仍有焦点，再点一下（PointerPressed）即可重新录制
    }

    private static HotkeyModifiers ModifierOf(uint vk) => vk switch
    {
        0xA0 or 0xA1 or 0x10 => HotkeyModifiers.Shift,
        0xA2 or 0xA3 or 0x11 => HotkeyModifiers.Control,
        0xA4 or 0xA5 or 0x12 => HotkeyModifiers.Alt,
        0x5B or 0x5C => HotkeyModifiers.Win,
        _ => 0,
    };

    private static string ModifiersText(HotkeyModifiers m)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((m & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((m & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((m & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((m & HotkeyModifiers.Win) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || _hook == IntPtr.Zero) return CallNextHookEx(_hook, code, wParam, lParam);
        var message = (int)wParam;
        var vk = (uint)Marshal.ReadInt32(lParam);
        var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        var up = message is WM_KEYUP or WM_SYSKEYUP;
        if (!down && !up) return CallNextHookEx(_hook, code, wParam, lParam);
        // 本程序自己发出的模拟按键（如语音小球）不参与录制。
        if ((Marshal.ReadInt32(lParam, 8) & 0x10) != 0) return CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            var modifier = ModifierOf(vk);
            if (modifier != 0)
            {
                if (down)
                {
                    _held |= modifier;
                    _peak |= _held;
                    _box.Text = ModifiersText(_held) + (_held != 0 ? "+…" : "");
                }
                else
                {
                    _held &= ~modifier;
                    // 只有修饰键的组合：全部松开时才确定。
                    if (_allowModifierOnly && !_hadMainKey && _held == 0 && _peak != 0
                        && System.Numerics.BitOperations.PopCount((uint)_peak) >= 2)
                    { Finish(ModifiersText(_peak)); return (IntPtr)1; }
                    if (_held == 0) { _peak = 0; _box.Text = ""; }
                }
                return (IntPtr)1;
            }

            if (!down) return (IntPtr)1; // 吞掉主键的抬起，避免系统只看到半截按键
            if (_held == 0 && vk == 0x1B) { Finish(_before); return (IntPtr)1; }                    // Esc：取消
            if (_held == 0 && vk is 0x08 or 0x2E) { Finish(""); return (IntPtr)1; }                  // 退格 / Delete：清除
            var name = HotkeyHelper.KeyName((ushort)vk);
            if (name == null) return (IntPtr)1;
            _hadMainKey = true;
            // 不带修饰键的单个普通字母 / 数字会在全局注册后吃掉正常输入，这里仍允许 F 键和功能键，其余要求至少一个修饰键。
            var isFunctionOrNav = name.Length > 1 && !name.Equals("Space", StringComparison.Ordinal);
            if (_held == 0 && !isFunctionOrNav)
            {
                _box.Text = "";
                _box.PlaceholderText = "请带上 Ctrl / Alt / Shift / Win";
                _hadMainKey = false;
                return (IntPtr)1;
            }
            Finish(_held == 0 ? name : ModifiersText(_held) + "+" + name);
        }
        catch (Exception ex) { Services.ErrorReporter.Log("HotkeyRecorder", ex); }
        return (IntPtr)1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
