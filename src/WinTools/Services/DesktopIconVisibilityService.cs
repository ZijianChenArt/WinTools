using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinTools.Services;

/// <summary>
/// 桌面图标的显示 / 隐藏（等同于右键桌面 →「查看」→「显示桌面图标」）。
/// </summary>
/// <remarks>
/// 桌面分区用这个开关让桌面看起来干净，**而不是移动文件或改文件属性**：
/// 用户新建、下载到桌面的东西，路径永远还是 <c>桌面\xxx</c>，一个字节都不会动。
///
/// 实现要点（2026-09-07 在 Win11 26340 实测）：
/// - 切换靠给 <c>SHELLDLL_DefView</c> 发 <c>WM_COMMAND 0x7402</c>，也就是那条右键菜单项本身的命令；
///   它是**翻转**语义，所以必须先读当前状态再决定发不发。
/// - 当前状态读注册表 <c>HKCU\...\Explorer\Advanced\HideIcons</c>（1 = 已隐藏）。发完命令后
///   Explorer 会同步更新这个值，可用来确认是否生效。
/// - <c>SHELLDLL_DefView</c> 通常挂在 <c>Progman</c> 下；开了壁纸动画 / 多屏时会被挪到某个
///   <c>WorkerW</c> 下，所以找不到时要遍历 <c>WorkerW</c>。
/// - 卡片是独立 HWND，**不受这个开关影响**（实测隐藏图标后卡片照常显示）。
///
/// 程序异常终止会让桌面图标保持隐藏。用户右键桌面 →「查看」→「显示桌面图标」即可恢复，
/// 不依赖本程序；正常退出路径（托盘退出 / 注销关机）都会主动恢复，见 <c>App</c>。
/// </remarks>
internal static class DesktopIconVisibilityService
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsValue = "HideIcons";
    private const uint WM_COMMAND = 0x0111;
    private const int ToggleDesktopIconsCommand = 0x7402;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint LvmGetEditControl = 0x1018;
    private const uint NewFolderNameResource = 16888;
    private static readonly Lazy<string> NewFolderName = new(ReadNewFolderName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadStringW(IntPtr module, uint id, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    /// <summary>当前桌面图标是否处于隐藏状态。读不到注册表时按“显示”处理。</summary>
    public static bool AreIconsHidden
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                return key?.GetValue(HideIconsValue) is int value && value != 0;
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("DesktopIconVisibility.Read", ex);
                return false;
            }
        }
    }

    internal static bool IsEditingIconName()
    {
        var view = FindDefView();
        if (view == IntPtr.Zero) return false;
        var list = FindWindowExW(view, IntPtr.Zero, "SysListView32", null);
        return list != IntPtr.Zero && SendMessageTimeoutW(list, LvmGetEditControl, IntPtr.Zero,
            IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var editor) != IntPtr.Zero && editor != IntPtr.Zero;
    }

    internal static bool IsDefaultFolderName(string name)
    {
        var baseName = NewFolderName.Value;
        if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) return true;
        // Explorer appends " (2)", " (3)", and so on when a default name already exists.
        return name.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(')')
            && int.TryParse(name.AsSpan()[(baseName.Length + 2)..^1], out var number) && number >= 2;
    }

    private static string ReadNewFolderName()
    {
        var text = new StringBuilder(260);
        var shell = GetModuleHandleW("shell32.dll");
        if (shell != IntPtr.Zero && LoadStringW(shell, NewFolderNameResource, text, text.Capacity) > 0)
            return text.ToString();
        return System.Globalization.CultureInfo.CurrentUICulture.Name is "zh-CN" ? "新建文件夹" : "New folder";
    }

    /// <summary>把桌面图标设为隐藏 / 显示。已经是目标状态时不做任何事。</summary>
    /// <returns>最终状态是否等于 <paramref name="hidden"/>。</returns>
    public static bool SetHidden(bool hidden)
    {
        try
        {
            if (AreIconsHidden == hidden) return true;

            var view = FindDefView();
            if (view == IntPtr.Zero)
            {
                ErrorReporter.Log("DesktopIconVisibility.SetHidden",
                    new InvalidOperationException("找不到 SHELLDLL_DefView，桌面图标开关未生效。"));
                return false;
            }

            // 跨进程同步发消息必须带超时 + SMTO_ABORTIFHUNG：Explorer 卡住时
            // SendMessage 会无限阻塞（见第 9 节「低级鼠标钩子的红线」同款教训）。
            _ = SendMessageTimeoutW(view, WM_COMMAND, (IntPtr)ToggleDesktopIconsCommand,
                IntPtr.Zero, SMTO_ABORTIFHUNG, 3000, out _);

            return AreIconsHidden == hidden;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopIconVisibility.SetHidden", ex);
            return false;
        }
    }

    private static IntPtr FindDefView()
    {
        var progman = FindWindowW("Progman", null);
        var view = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;

        // 壁纸动画 / 多显示器时 DefView 会被挪到某个 WorkerW 下。
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var name = new StringBuilder(64);
            GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() != "WorkerW") return true;

            var child = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero) return true;
            found = child;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
