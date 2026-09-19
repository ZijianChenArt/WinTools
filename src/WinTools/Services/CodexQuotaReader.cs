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
        process.Start();
        using var cancellation = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
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
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await drain; } catch (OperationCanceledException) { }
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
        else return new("Codex 暂无额度数据", "当前账户未提供 Codex 通用额度");

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
        return windows.Count == 0 ? new("Codex 暂无额度数据", "当前账户未提供额度窗口")
            : new("Codex  " + string.Join(" · ", windows.OrderBy(w => w.Minutes).Select(w => w.Text)),
                string.Join("\n", windows.OrderBy(w => w.Minutes).Select(w => w.Detail)));
    }
}
