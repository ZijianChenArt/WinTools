using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinTools.Services;

/// <summary>
/// 从 GitHub Releases 检查并安装新版本。只信任本仓库 Release 里的安装包：
/// 资源地址必须落在 <see cref="Repository"/> 的 releases/download 下，并用同名 .sha256 校验后才会运行。
/// </summary>
internal static class UpdateService
{
    internal const string Repository = "orangec0831/WinTools";
    private static readonly string DownloadPrefix = $"https://github.com/{Repository}/releases/download/";
    private static readonly HttpClient Http = CreateClient();

    internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string SetupUrl, string? ChecksumUrl);

    /// <summary>当前运行版本（取 csproj 的 Version，忽略 +build 元数据）。</summary>
    internal static Version CurrentVersion { get; } = ParseVersion(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    internal static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinTools", CurrentVersionText));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        var cut = text.IndexOfAny(new[] { '+', '-' });
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>有更新返回 <see cref="UpdateInfo"/>，已是最新返回 null；网络或解析失败抛异常，由调用方显示。</summary>
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("仓库还没有发布任何版本，或仓库是私有的。");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag) ?? throw new InvalidOperationException($"无法识别版本号：{tag}");
        if (version <= CurrentVersion) return null;

        string? setup = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith("WinTools-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) setup = url;
                else if (name.StartsWith("WinTools-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase)) checksum = url;
            }
        if (setup == null) throw new InvalidOperationException($"{tag} 没有附带安装包（WinTools-Setup-*.exe）。");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, notes, setup, checksum);
    }

    /// <summary>下载安装包到临时目录并校验；返回安装包路径。</summary>
    internal static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancel = default)
    {
        if (!update.SetupUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("安装包地址不在本项目的 Release 下，已拒绝。");
        if (update.ChecksumUrl == null || !update.ChecksumUrl.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("这个版本没有附带校验文件（.sha256），为安全起见不自动安装。");

        var expected = (await Http.GetStringAsync(update.ChecksumUrl, cancel)).Trim().Split(' ', '\t', '\r', '\n')[0];
        var directory = Path.Combine(Path.GetTempPath(), "WinTools-update");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"WinTools-Setup-{update.Version}.exe");

        using (var response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("安装包校验失败（SHA-256 不一致），已删除。");
        }
        return path;
    }

    /// <summary>静默安装：安装程序会关闭正在运行的 WinTools，装完自动重新启动。</summary>
    internal static void LaunchInstaller(string setupPath)
    {
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
    }
}
