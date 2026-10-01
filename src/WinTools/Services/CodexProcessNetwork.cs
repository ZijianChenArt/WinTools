using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WinTools.Services;

internal static class CodexProcessNetwork
{
    // A desktop-launched Rust child does not necessarily inherit Windows Internet Settings.
    // Only bridge an enabled static system proxy when the user supplied no proxy environment.
    internal static void Apply(ProcessStartInfo start)
    {
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            if (start.Environment.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) return;
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key?.GetValue("ProxyEnable") is not int enabled || enabled != 1) return;
        var proxy = Parse(key.GetValue("ProxyServer") as string);
        if (proxy == null) return;
        start.Environment["HTTPS_PROXY"] = proxy;
        start.Environment["HTTP_PROXY"] = proxy;
        // Local authorization callbacks must remain local. Do not change global settings.
        start.Environment.TryGetValue("NO_PROXY", out var bypass);
        start.Environment["NO_PROXY"] = string.IsNullOrWhiteSpace(bypass) ? "localhost,127.0.0.1,::1" : bypass + ",localhost,127.0.0.1,::1";
    }

    internal static string? Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        string? address = null;
        if (!setting.Contains('=')) address = setting.Trim();
        else
            foreach (var part in setting.Split(';'))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase)) address = pair[1].Trim();
            }
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (!address.Contains("://")) address = "http://" + address;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}
