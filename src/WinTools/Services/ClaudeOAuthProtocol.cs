using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinTools.Services;

// Compatible with the platform-code flow documented by ipangdz/claudexbar/docs/AUTH.md.
// These are public client parameters, not an application secret. No inference requests are made.
internal sealed class ClaudeOAuthProtocol
{
    internal const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    internal const string Redirect = "https://platform.claude.com/oauth/code/callback";
    private readonly string _verifier = Encode(RandomNumberGenerator.GetBytes(32));
    internal string State { get; } = Encode(RandomNumberGenerator.GetBytes(32));
    private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
    internal bool IsExpired => DateTimeOffset.UtcNow - _created > TimeSpan.FromMinutes(5);
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal string AuthorizationUrl => "https://claude.com/cai/oauth/authorize?" + string.Join("&", new Dictionary<string, string>
    {
        ["code"] = "true", ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = Redirect,
        ["scope"] = "user:profile user:inference user:sessions:claude_code user:mcp_servers user:file_upload",
        ["code_challenge"] = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(_verifier))), ["code_challenge_method"] = "S256", ["state"] = State
    }.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    internal Dictionary<string, string> ExchangeForm(string pasted)
    {
        if (IsExpired) throw new InvalidOperationException("本次授权已超时，请重新点击浏览器授权。");
        var parts = pasted.Trim().Split('#');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || parts[0].Length > 4096 || parts[1] != State)
            throw new InvalidOperationException("授权码不属于本次连接，请复制网页显示的完整授权码（含 # 后面的内容）。");
        return new() { ["grant_type"] = "authorization_code", ["code"] = parts[0], ["state"] = State,
            ["redirect_uri"] = Redirect, ["client_id"] = ClientId, ["code_verifier"] = _verifier };
    }

    internal static QuotaSnapshot ParseUsage(JsonElement root)
    {
        var parts = new List<string>();
        var details = new List<string>();
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Claude 额度响应格式异常。");
        foreach (var (key, label) in new[] { ("five_hour", "5小时"), ("seven_day", "本周") })
        {
            JsonElement window;
            string percentKey;
            if (root.TryGetProperty(key, out window) && window.ValueKind == JsonValueKind.Object) percentKey = "utilization";
            else
            {
                window = default;
                if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                    foreach (var candidate in limits.EnumerateArray())
                        if (candidate.ValueKind == JsonValueKind.Object && candidate.TryGetProperty("kind", out var kind)
                            && kind.ValueKind == JsonValueKind.String && kind.GetString() == (key == "five_hour" ? "session" : "weekly_all")) { window = candidate; break; }
                percentKey = "percent";
            }
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty(percentKey, out var value)
                || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
            string resetText = "";
            if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String
                && reset.TryGetDateTimeOffset(out var time))
            {
                if (time <= DateTimeOffset.UtcNow) continue;
                resetText = $"，{time.ToLocalTime():M/d HH:mm} 重置";
            }
            var text = $"{label} {Math.Clamp(100 - used, 0, 100):0.#}%";
            parts.Add(text);
            details.Add(text + " 剩余" + resetText);
        }
        return parts.Count == 0 ? new("Claude 额度暂不可用", "已连接，但账户未提供当前可用的订阅额度窗口。")
            : new("Claude  " + string.Join(" · ", parts), string.Join("；", details));
    }
}
