using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WinTools.Services;

internal static class QuotaBrowser
{
    internal static void Open(string url)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host is not ("auth.openai.com" or "chatgpt.com" or "claude.com"))
            throw new InvalidOperationException("授权地址不受支持，请更新软件后重试。");
        var chrome = FindChrome() ?? throw new InvalidOperationException("未找到 Chrome，请先安装 Chrome 后重试。");
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        start.ArgumentList.Add(url);
        Process.Start(start)?.Dispose();
    }

    private static string? FindChrome()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        {
            var path = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
