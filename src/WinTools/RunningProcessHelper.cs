using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinTools;

/// <summary>正在运行且拥有可见顶层窗口的应用程序信息。</summary>
public sealed class RunningAppInfo
{
    public string ProcessName { get; init; } = "";
    public string WindowTitle { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string DisplayName => string.IsNullOrEmpty(WindowTitle)
        ? ProcessName
        : $"{ProcessName} — {WindowTitle}";
}

/// <summary>枚举当前正在运行、拥有可见窗口的应用程序。</summary>
internal static class RunningProcessHelper
{
    private const int MaxTitleLength = 512;

    public static List<RunningAppInfo> GetRunningApps()
    {
        var result = new List<RunningAppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;

                var title = GetWindowTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return true;

                string processName;
                var exePath = "";
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                    try { exePath = process.MainModule?.FileName ?? ""; }
                    catch { /* 管理员进程等读不到路径，图标退回首字母 */ }
                }
                catch
                {
                    return true;
                }

                if (string.IsNullOrEmpty(processName)) return true;
                if (!seen.Add(processName)) return true;

                result.Add(new RunningAppInfo
                {
                    ProcessName = processName,
                    WindowTitle = title.Trim(),
                    ExePath = exePath
                });
            }
            catch
            {
                // ignore single window
            }

            return true;
        }, IntPtr.Zero);

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return "";

        var sb = new StringBuilder(Math.Min(length + 1, MaxTitleLength));
        _ = GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const uint GW_OWNER = 4;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);
}
