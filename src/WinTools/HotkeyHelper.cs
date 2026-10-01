using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace WinTools;

/// <summary>快捷键修饰键对应的数值（与 RegisterHotKey 一致）。</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>将快捷键字符串（如 "Ctrl+Win+W"）解析为 RegisterHotKey 所需的修饰键与虚拟键码。</summary>
public static class HotkeyHelper
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = HotkeyModifiers.Control,
        ["Control"] = HotkeyModifiers.Control,
        ["Alt"] = HotkeyModifiers.Alt,
        ["Win"] = HotkeyModifiers.Win,
        ["Windows"] = HotkeyModifiers.Win,
        ["Shift"] = HotkeyModifiers.Shift,
    };

    /// <summary>常用虚拟键码（VK_*）。</summary>
    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45, ["F"] = 0x46,
        ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A, ["K"] = 0x4B, ["L"] = 0x4C,
        ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F, ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52,
        ["S"] = 0x53, ["T"] = 0x54, ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58,
        ["Y"] = 0x59, ["Z"] = 0x5A,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
        ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
        ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["F13"] = 0x7C, ["F14"] = 0x7D, ["F15"] = 0x7E, ["F16"] = 0x7F, ["F17"] = 0x80,
        ["F18"] = 0x81, ["F19"] = 0x82, ["F20"] = 0x83, ["F21"] = 0x84, ["F22"] = 0x85,
        ["F23"] = 0x86, ["F24"] = 0x87,
        ["Space"] = 0x20, ["."] = 0xBE, [","] = 0xBC,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Backspace"] = 0x08,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        [";"] = 0xBA, ["/"] = 0xBF, ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB,
        ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC, ["'"] = 0xDE,
    };

    /// <summary>解析 "Ctrl+Win+W" 格式。返回 (modifiers, vk)；若解析失败则返回 null。</summary>
    public static (HotkeyModifiers Modifiers, ushort VirtualKey)? Parse(string? hotkeyString)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString)) return null;
        var parts = hotkeyString.Trim().Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return null;

        HotkeyModifiers mods = 0;
        ushort vk = 0;
        foreach (var p in parts)
        {
            if (ModifierMap.TryGetValue(p, out var m))
                mods |= m;
            else if (KeyMap.TryGetValue(p, out var k))
                vk = k;
            else if (p.Length == 1)
                vk = (ushort)char.ToUpperInvariant(p[0]);
            else
                return null;
        }
        if (vk == 0) return null;
        return (mods, vk);
    }

    /// <summary>
    /// 解析「要模拟按下」的快捷键。与 <see cref="Parse"/> 的区别是允许只有修饰键（如 "Ctrl+Shift"）：
    /// 这类组合 RegisterHotKey 注册不了，但输入法的语音快捷键常用它。返回的 VirtualKey 为 0 表示没有主键。
    /// </summary>
    public static (HotkeyModifiers Modifiers, ushort VirtualKey)? ParseForSend(string? hotkeyString)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString)) return null;
        // 输入法设置页常把组合显示成「Ctrl Win Shift」，空格与 + 一样当分隔符。
        var parts = hotkeyString.Split(new[] { '+', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (parts.All(p => ModifierMap.ContainsKey(p)))
        {
            HotkeyModifiers mods = 0;
            foreach (var p in parts) mods |= ModifierMap[p];
            return (mods, 0);
        }
        return Parse(string.Join("+", parts));
    }

    /// <summary>将 HotkeyModifiers 转为 Win32 HOT_KEY_MODIFIERS。</summary>
    internal static HOT_KEY_MODIFIERS ToWin32(HotkeyModifiers m)
    {
        var u = (uint)m;
        HOT_KEY_MODIFIERS r = 0;
        if ((u & (uint)HotkeyModifiers.Alt) != 0) r |= HOT_KEY_MODIFIERS.MOD_ALT;
        if ((u & (uint)HotkeyModifiers.Control) != 0) r |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if ((u & (uint)HotkeyModifiers.Shift) != 0) r |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if ((u & (uint)HotkeyModifiers.Win) != 0) r |= HOT_KEY_MODIFIERS.MOD_WIN;
        return r;
    }

    /// <summary>虚拟键码转显示名（如 0x48 → "H"）；不在支持列表里的键返回 null，录制时忽略。</summary>
    public static string? KeyName(ushort virtualKey)
    {
        foreach (var pair in KeyMap)
            if (pair.Value == virtualKey) return pair.Key;
        return null;
    }

    /// <summary>将修饰键与虚拟键格式化成显示字符串（如 "Ctrl+Win+W"）。</summary>
    public static string Format(HotkeyModifiers modifiers, ushort virtualKey)
    {
        var list = new List<string>();
        if ((modifiers & HotkeyModifiers.Control) != 0) list.Add("Ctrl");
        if ((modifiers & HotkeyModifiers.Alt) != 0) list.Add("Alt");
        if ((modifiers & HotkeyModifiers.Shift) != 0) list.Add("Shift");
        if ((modifiers & HotkeyModifiers.Win) != 0) list.Add("Win");
        var keyStr = KeyMap.FirstOrDefault(k => k.Value == virtualKey).Key ?? ((char)virtualKey).ToString();
        list.Add(keyStr);
        return string.Join("+", list);
    }
}
