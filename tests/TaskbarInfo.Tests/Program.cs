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
Check("""{"rateLimits":{"primary":null,"secondary":null}}""", "Codex 暂无额度数据");
Check("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":300}}}""", "Codex 暂无额度数据");
Check("""{"rateLimits":{"primary":{"usedPercent":-5,"windowDurationMins":60},"secondary":{"usedPercent":150,"windowDurationMins":15}}}""", "Codex  15分钟 0% · 1小时 100%");
Check("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":20}}}""", "Codex 暂无额度数据");
Check("""{"rateLimitsByLimitId":{"other":{"primary":{"usedPercent":20}}}}""", "Codex 暂无额度数据");
Console.WriteLine("7 quota parsing checks passed.");
if (Array.IndexOf(args, "--live") >= 0)
{
    var result = await CodexQuotaReader.ReadAsync(CancellationToken.None);
    Console.WriteLine(result.Text);
    Console.WriteLine(result.Detail);
}
