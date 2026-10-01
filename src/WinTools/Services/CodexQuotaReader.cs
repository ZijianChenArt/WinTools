using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinTools.Services;

internal sealed record QuotaSnapshot(string Text, string Detail);

/// <summary>Read-only Codex app-server client. Never opens a model turn or reads credentials.</summary>
internal static class CodexQuotaReader
{
    internal static async Task LoginAsync(Action<string> openBrowser, CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new InvalidOperationException("未找到 Codex，请先安装 Codex 桌面版或 CLI。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        }};
        CodexProcessNetwork.Apply(process.StartInfo);
        process.Start();
        using var cancel = token.Register(() => KillQuietly(process));
        var drain = DrainAsync(process.StandardError, token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"wintools_quota\",\"version\":\"1.0.0\"}}}");
            await ReadResponseAsync(process, 1, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/login/start\",\"params\":{\"type\":\"chatgpt\"}}");
            var start = await ReadResponseAsync(process, 2, token);
            var loginId = start.GetProperty("loginId").GetString();
            openBrowser(start.GetProperty("authUrl").GetString()!);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var method) || method.GetString() != "account/login/completed") continue;
                var result = root.GetProperty("params");
                if (result.GetProperty("loginId").GetString() != loginId) continue;
                if (!result.GetProperty("success").GetBoolean())
                {
                    var failure = result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
                    throw new InvalidOperationException(failure?.Contains("Token exchange failed", StringComparison.OrdinalIgnoreCase) == true
                        ? "Codex 换取登录凭证失败，请确认系统代理可用，再重新点击浏览器授权；旧回调页面不能重复使用。"
                        : "Codex 授权未完成，请重新连接。");
                }
                return;
            }
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Codex 授权服务已退出，请重试。");
        }
        finally
        {
            KillQuietly(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await drain; } catch (OperationCanceledException) { }
        }
    }

    internal static string? FindExecutable()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(path)) return path;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
            return Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return null;
    }

    internal static async Task<QuotaSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var executable = FindExecutable() ?? throw new InvalidOperationException("未找到 Codex，请先安装并登录 Codex 桌面版或 CLI");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        using var process = new Process { StartInfo = new ProcessStartInfo(executable, "app-server")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath()
        }};
        CodexProcessNetwork.Apply(process.StartInfo);
        process.Start();
        using var cancellation = token.Register(() => KillQuietly(process));
        // Drain diagnostics without retaining authentication or unrelated server output.
        var drain = DrainAsync(process.StandardError, token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"wintools_quota\",\"version\":\"1.0.0\"}}}");
            await ReadResponseAsync(process, 1, token);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\"}");
            await process.StandardInput.WriteLineAsync("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
            return Parse(await ReadResponseAsync(process, 2, token));
        }
        finally
        {
            KillQuietly(process);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await drain; } catch (OperationCanceledException) { }
        }
    }

    // Best effort, never throws. The process can exit between HasExited and Kill, and
    // Kill(entireProcessTree) throws AggregateException ("Not all processes in process tree could be
    // terminated", Win32Exception 5) when a descendant cannot be killed. From the timeout callbacks above
    // that exception fires on a thread-pool timer where nothing catches it, so it took the whole app down
    // (2026-09-24: quota refresh timed out, dialog "WinTools 启动失败"). Fall back to the app-server itself.
    private static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch
        {
            try { if (!process.HasExited) process.Kill(); }
            catch { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) != null) { }
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt32(out var number) || number != id) continue;
            if (root.TryGetProperty("error", out _))
                throw new InvalidOperationException("额度读取失败，请确认 Codex 已登录且网络可用");
            return root.GetProperty("result").Clone();
        }
        throw new InvalidOperationException("Codex 额度服务已退出");
    }

    internal static QuotaSnapshot Parse(JsonElement result)
    {
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codex)) bucket = codex;
        else if (result.TryGetProperty("rateLimits", out var legacy)
            && legacy.ValueKind == JsonValueKind.Object
            && (!legacy.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null || id.GetString() == "codex")) bucket = legacy;
        else return new("Codex 额度暂不可用", "当前账户未提供 Codex 通用额度");

        var windows = new List<(int Minutes, string Text, string Detail)>();
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (bucket.ValueKind != JsonValueKind.Object || !bucket.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (!window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent)) continue;
            var minutes = window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var value) ? value : 0;
            var label = minutes switch { 300 => "5小时", 10080 => "本周", > 0 when minutes % 1440 == 0 => $"{minutes / 1440}天", > 0 when minutes % 60 == 0 => $"{minutes / 60}小时", > 0 => $"{minutes}分钟", _ => "额度" };
            var text = $"{label} {Math.Clamp(100 - percent, 0, 100):0.#}%";
            var reset = "";
            if (window.TryGetProperty("resetsAt", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var timestamp)
                && timestamp is >= -62135596800 and <= 253402300799)
                reset = $"，{DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime():M/d HH:mm} 重置";
            windows.Add((minutes, text, $"{text} 剩余{reset}"));
        }
        return windows.Count == 0 ? new("Codex 额度暂不可用", "当前账户未提供额度窗口")
            : new("Codex  " + string.Join(" · ", windows.OrderBy(w => w.Minutes).Select(w => w.Text)),
                string.Join("\n", windows.OrderBy(w => w.Minutes).Select(w => w.Detail)));
    }
}
