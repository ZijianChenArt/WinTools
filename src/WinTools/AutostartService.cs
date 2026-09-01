using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WinTools;

/// <summary>开机自启动管理（使用当前用户 Run 注册表）。</summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinTools";

    /// <summary>检查当前是否已启用自启动。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var raw = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            // Run 项常带引号存储，比较前须去掉，否则切换页面后 IsEnabled 会误判为 false
            var exePath = GetExePath();
            return string.Equals(NormalizePath(raw), NormalizePath(exePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>设置或取消自启动。</summary>
    /// <returns>是否成功；失败时 <paramref name="error"/> 返回错误消息。</returns>
    public static bool SetEnabled(bool enabled, out string? error)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                error = "无法打开注册表项。";
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{GetExePath()}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetExePath()
    {
        try { return Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "WinTools.exe"; }
        catch { return "WinTools.exe"; }
    }

    private static string NormalizePath(string path)
    {
        path = path.Trim().Trim('"');
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}
