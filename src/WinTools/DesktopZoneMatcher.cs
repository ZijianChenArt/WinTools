using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinTools;

/// <summary>
/// 分区匹配规则：判断一个桌面项应归入哪个 <see cref="DesktopZone"/>。
/// 规则与 <see cref="DesktopOrganizeService"/> 中的排列逻辑保持一致，供收纳（移动文件）复用。
/// </summary>
public static class DesktopZoneMatcher
{
    /// <summary>返回首个匹配分区的索引；都不匹配返回 <paramref name="zones"/>.Count。</summary>
    public static int Match(string fileName, string fullPath, bool isDirectory, List<DesktopZone> zones)
    {
        var displayName = fileName ?? "";
        var extension = isDirectory ? "" : (Path.GetExtension(displayName)?.ToLowerInvariant() ?? "");

        // 第一优先：明确指定的文件（名称匹配，容忍是否带扩展名）
        for (var i = 0; i < zones.Count; i++)
        {
            var explicitItems = zones[i].Items;
            if (explicitItems == null) continue;
            foreach (var name in explicitItems)
            {
                if (NameMatches(name, displayName))
                    return i;
            }
        }

        // 其次：名称关键词。软件分类优先于通用 .lnk/.url 扩展名。
        for (var i = 0; i < zones.Count; i++)
        {
            var keywords = zones[i].Keywords;
            if (keywords == null) continue;
            foreach (var raw in keywords)
            {
                var kw = raw?.Trim();
                if (string.IsNullOrEmpty(kw)) continue;
                if (!kw.StartsWith('.') && !IsFolderKeyword(kw)
                    && displayName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        // 游戏协议必须先于通用 .url 扩展名。
        if (!isDirectory && IsGameShortcut(fullPath, extension))
        {
            for (var i = 0; i < zones.Count; i++)
            {
                if (zones[i].Keywords?.Any(kw =>
                    string.Equals(kw?.Trim(), "steam://", StringComparison.OrdinalIgnoreCase)) == true)
                    return i;
            }
        }

        // 最后匹配文件夹和扩展名等通用规则。
        for (var i = 0; i < zones.Count; i++)
        {
            var keywords = zones[i].Keywords;
            if (keywords == null) continue;
            foreach (var raw in keywords)
            {
                var kw = raw?.Trim();
                if (string.IsNullOrEmpty(kw)) continue;
                if (kw.StartsWith('.')
                    && string.Equals(extension, kw, StringComparison.OrdinalIgnoreCase))
                    return i;
                if (IsFolderKeyword(kw) && isDirectory)
                    return i;
            }
        }

        // 未命中任何明确规则的项统一进入末尾兜底分区。
        for (var i = 0; i < zones.Count; i++)
        {
            if (string.Equals(zones[i].Name, "其他", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zones[i].Name, "常用程序与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zones[i].Name, "常用与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zones[i].Name, "杂项", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return zones.Count;
    }

    /// <summary>名称匹配：容忍是否带扩展名。</summary>
    private static bool NameMatches(string? a, string? b)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;

        var xs = Path.GetFileNameWithoutExtension(x);
        var ys = Path.GetFileNameWithoutExtension(y);
        return string.Equals(xs, y, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x, ys, StringComparison.OrdinalIgnoreCase)
            || string.Equals(xs, ys, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFolderKeyword(string kw)
        => kw is "文件夹" or "目录" or "文件夹/目录"
           || string.Equals(kw, "folder", StringComparison.OrdinalIgnoreCase);

    private static bool IsGameShortcut(string path, string extension)
    {
        if (!string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var value = line.Trim();
                if (!value.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                var target = value.Substring(4).Trim();
                return target.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("uplay://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("ubisoftconnect://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* 读不到就退回名称/扩展名规则 */ }
        return false;
    }
}
