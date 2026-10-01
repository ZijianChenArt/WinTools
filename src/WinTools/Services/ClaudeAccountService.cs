using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;

namespace WinTools.Services;

internal static class ClaudeAccountService
{
    private const string Resource = "WinTools.ClaudeQuota";
    private const string TokenEndpoint = "https://platform.claude.com/v1/oauth/token";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(25) };
    private sealed record Credential(string Access, string? Refresh, DateTimeOffset Expires);

    private static Credential? Load()
    {
        PasswordCredential saved;
        try { saved = new PasswordVault().Retrieve(Resource, "default"); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
        saved.RetrievePassword();
        return JsonSerializer.Deserialize<Credential>(saved.Password);
    }

    private static void Save(Credential value) => new PasswordVault().Add(new PasswordCredential(Resource, "default", JsonSerializer.Serialize(value)));

    internal static async Task<QuotaSnapshot> ConnectAsync(ClaudeOAuthProtocol flow, string code, CancellationToken token)
    {
        var form = flow.ExchangeForm(code);
        await Gate.WaitAsync(token);
        try
        {
            var credential = await ExchangeAsync(form, null, token);
            // Validate quota permission before replacing any working connection.
            var snapshot = await QueryAsync(credential.Access, token);
            token.ThrowIfCancellationRequested();
            Save(credential);
            return snapshot;
        }
        finally { Gate.Release(); }
    }

    internal static async Task<QuotaSnapshot> ReadAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            var value = Load() ?? throw new InvalidOperationException("尚未连接 Claude，请在任务栏信息设置中点击浏览器授权。");
            if (value.Expires <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                if (string.IsNullOrEmpty(value.Refresh)) throw new InvalidOperationException("Claude 授权已过期，请重新连接。");
                value = await ExchangeAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = value.Refresh, ["client_id"] = ClaudeOAuthProtocol.ClientId }, value.Refresh, token);
                Save(value);
            }
            return await QueryAsync(value.Access, token);
        }
        finally { Gate.Release(); }
    }

    internal static async Task DisconnectAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var vault = new PasswordVault();
            try { vault.Remove(vault.Retrieve(Resource, "default")); }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { }
        }
        finally { Gate.Release(); }
    }

    private static async Task<Credential> ExchangeAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await Client.PostAsync(TokenEndpoint, content, token);
        CheckStatus(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previousRefresh;
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("Claude 未返回有效凭证，请重新授权。");
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 3600;
        return new(access, refresh, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 2592000)));
    }

    private static async Task<QuotaSnapshot> QueryAsync(string access, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Client.SendAsync(request, token);
        CheckStatus(response);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return ClaudeOAuthProtocol.ParseUsage(doc.RootElement);
    }

    private static void CheckStatus(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.BadRequest => "Claude 未接受本次授权，授权码可能已使用或过期，请重新点击浏览器授权。",
            HttpStatusCode.Unauthorized => "Claude 授权已失效，请重新连接。",
            HttpStatusCode.Forbidden => "Claude 拒绝额度访问，请检查订阅与授权权限。",
            HttpStatusCode.TooManyRequests => "Claude 查询过于频繁，请稍后重试。",
            _ => $"Claude 服务暂不可用（HTTP {(int)response.StatusCode}），请稍后重试。"
        });
    }
}
