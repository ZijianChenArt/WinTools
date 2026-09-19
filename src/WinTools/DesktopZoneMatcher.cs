using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinTools;

/// <summary>
/// 分区匹配规则：判断一个桌面项应归入哪个 <see cref="DesktopZone"/>。
/// 桌面分区卡片的收纳（物理移动文件）与还原都走这一套规则。
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

        // 目录的「文件夹」规则要排在名称关键词**之前**。
        // 2026-09-12 用户反馈：桌面上一个叫「AI Project Unity」的工程文件夹被分到了
        // 「三维与引擎」，因为名字里含 Unity。但对文件夹来说，名字里出现某个软件名，
        // 多半说明它是那个软件的**工程 / 数据目录**，而不是那个软件本身——用户的预期是
        // 「它是个文件夹，就该进文件夹分区」。想让某个文件夹归到软件分区，把它拖过去即可：
        // 显式清单的优先级仍然最高（上面那一段），不受这条影响。
        if (isDirectory)
        {
            for (var i = 0; i < zones.Count; i++)
            {
                var folderKeywords = zones[i].Keywords;
                if (folderKeywords == null) continue;
                foreach (var raw in folderKeywords)
                {
                    var kw = raw?.Trim();
                    if (!string.IsNullOrEmpty(kw) && IsFolderKeyword(kw))
                        return i;
                }
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
