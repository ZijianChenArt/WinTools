using System;
using System.Text.Json;
using System.Threading;
using WinTools.Services;

static void Check(string json, string expected)
{
    using var doc = JsonDocument.Parse(json);
    var actual = CodexQuotaReader.Parse(doc.RootElement).Text;
    if (actual != expected) throw new Exception($"Expected {expected}; got {actual}");
}
Check("""{"rateLimits":{"primary":{"usedPercent":25,"windowDurationMins":300},"secondary":{"usedPercent":60,"windowDurationMins":10080}}}""", "Codex  5小时 75% · 本周 40%");
Check("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":100,"windowDurationMins":10080}}},"rateLimits":{"primary":{"usedPercent":0,"windowDurationMins":300}}}""", "Codex  本周 0%");
Check("""{"rateLimits":{"primary":null,"secondary":null}}""", "Codex 额度暂不可用");
Check("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300}}}""", "Codex 额度暂不可用");
Check("""{"rateLimits":{"primary":{"usedPercent":-5,"windowDurationMins":60},"secondary":{"usedPercent":150,"windowDurationMins":15}}}""", "Codex  15分钟 0% · 1小时 100%");
Check("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":20}}}""", "Codex 额度暂不可用");
Check("""{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":20}}}}""", "Codex 额度暂不可用");
Console.WriteLine("7 quota parsing checks passed.");
void CheckClaude(string json, string expected)
{
    using var doc = JsonDocument.Parse(json);
    var actual = ClaudeOAuthProtocol.ParseUsage(doc.RootElement).Text;
    if (actual != expected) throw new Exception($"Expected {expected}; got {actual}");
}
CheckClaude("""{"five_hour":{"utilization":25,"resets_at":"2099-01-01T00:00:00Z"},"seven_day":{"utilization":60,"resets_at":null}}""", "Claude  5小时 75% · 本周 40%");
CheckClaude("""{"limits":[{"kind":"session","percent":30},{"kind":"weekly_all","percent":70},{"kind":"weekly_scoped","percent":100}]}""", "Claude  5小时 70% · 本周 30%");
CheckClaude("""{"five_hour":{"utilization":25,"resets_at":"2020-01-01T00:00:00Z"}}""", "Claude 额度暂不可用");
CheckClaude("""{"context_window":{"remaining_percentage":90}}""", "Claude 额度暂不可用");
CheckClaude("""{"five_hour":{"utilization":null},"seven_day":null}""", "Claude 额度暂不可用");
CheckClaude("""{"five_hour":{"utilization":-5},"seven_day":{"utilization":120}}""", "Claude  5小时 100% · 本周 0%");
var flow = new ClaudeOAuthProtocol();
var exchange = flow.ExchangeForm("code+test#" + flow.State);
if (exchange["code"] != "code+test" || exchange["code_verifier"].Length < 43 || !flow.AuthorizationUrl.StartsWith("https://claude.com/cai/oauth/authorize?")) throw new Exception("Invalid PKCE flow");
var expectedChallenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(exchange["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
if (!flow.AuthorizationUrl.Contains("code_challenge=" + expectedChallenge)) throw new Exception("PKCE challenge mismatch");
foreach (var invalid in new[] { "code", "code#wrong-state", "#" + flow.State, "code#" + flow.State + "#extra" })
{
    bool rejected = false;
    try { flow.ExchangeForm(invalid); } catch (InvalidOperationException) { rejected = true; }
    if (!rejected) throw new Exception("Invalid callback accepted");
}
Console.WriteLine("Claude quota parsing, PKCE challenge and callback state checks passed.");
if (Array.IndexOf(args, "--live") >= 0)
{
    var result = await CodexQuotaReader.ReadAsync(CancellationToken.None);
    Console.WriteLine(result.Text);
    Console.WriteLine(result.Detail);
}

if (CodexProcessNetwork.Parse("127.0.0.1:7897") != "http://127.0.0.1:7897"
    || CodexProcessNetwork.Parse("http=proxy-a:8080;https=proxy-b:8090") != "http://proxy-b:8090"
    || CodexProcessNetwork.Parse("http=proxy-a:8080") != null
    || CodexProcessNetwork.Parse("http://proxy:8080/path") != null
    || CodexProcessNetwork.Parse("http://user:password@proxy:8080") != null)
    throw new Exception("System proxy parsing failed");
Console.WriteLine("5 system-proxy parsing checks passed.");

// 瞄准任一入口后，展开全程应保持该入口的屏幕横坐标（允许像素取整误差）。
foreach (var dpi in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var iconCenter in new[] { 26f, 62f, 98f, 138f, 250f })
{
    var screenAnchor = 900;
    var previousScale = .75f;
    for (var ms = 0; ms <= 220; ms++)
    {
        var scale = TaskbarHoverMotion.ScaleAt(.75f, 1, ms);
        if (scale < previousScale || scale > 1) throw new Exception("Hover scale overshot or reversed");
        var local = iconCenter * dpi * scale;
        var left = TaskbarHoverMotion.Left(700, screenAnchor, local, 1, 0, 2000);
        if (Math.Abs(left + local - screenAnchor) > .51f) throw new Exception("Aimed shortcut moved horizontally");
        previousScale = scale;
    }
}
if (TaskbarHoverMotion.ScaleAt(.75f, 1, 0) != .75f || TaskbarHoverMotion.ScaleAt(.75f, 1, 220) != 1
    || TaskbarHoverMotion.ScaleAt(1, .75f, 220) != .75f
    || TaskbarHoverMotion.Left(700, 900, 150, 0, 0, 2000) != 700
    || TaskbarHoverMotion.Left(0, 5, 30, 1, 0, 2000) != 0)
    throw new Exception("Hover endpoints, alignment restore or screen clamp failed");
var interrupted = TaskbarHoverMotion.ScaleAt(.75f, 1, 80);
if (TaskbarHoverMotion.ScaleAt(interrupted, .75f, 0) != interrupted)
    throw new Exception("Reversing hover motion jumped at its first frame");
Console.WriteLine("Hover anchor stability, easing, interruption, alignment and screen-edge checks passed.");
