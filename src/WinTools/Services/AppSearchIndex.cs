using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinTools.Services;

/// <summary>「悬浮搜索」里可被检索并启动的一项。</summary>
public sealed class AppEntry
{
    /// <summary>显示名称（快捷方式文件名 / 应用显示名）。</summary>
    public string Name { get; init; } = "";

    /// <summary>启动目标：真实文件路径，或 <c>shell:AppsFolder\{AUMID}</c>。</summary>
    public string Target { get; init; } = "";

    /// <summary>取图标用的真实文件路径；应用商店条目没有对应文件，为空字符串。</summary>
    public string IconPath { get; init; } = "";

    /// <summary>列表里的第二行说明（来源目录 / 应用类别）。</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>去重与「常用」计数用的稳定键。</summary>
    public string Key => Target;

    /// <summary>小写名称；匹配时直接用，避免每次按键重新 ToLower。</summary>
    internal string NameLower { get; set; } = "";

    /// <summary>名称的全拼（汉字注音、拉丁字母原样保留、去掉分隔符）："百度网盘" → "baiduwangpan"。</summary>
    internal string Pinyin { get; set; } = "";

    /// <summary>首字母缩写："百度网盘" → "bdwp"，"Visual Studio Code" → "vsc"。</summary>
    internal string Initials { get; set; } = "";

    /// <summary>把连续大写拆开后的另一种首字母写法（"QQ音乐" → "qqyl"）；与 <see cref="Initials"/> 相同时为空。</summary>
    internal string Acronym { get; set; } = "";
}

/// <summary>
/// 已安装程序索引：扫描开始菜单 / 桌面快捷方式，并合并 Shell 的「应用」虚拟文件夹
/// （<c>shell:AppsFolder</c>，即开始菜单「所有应用」那份清单，含应用商店应用）。
/// </summary>
/// <remarks>
/// 索引在后台 STA 线程上构建：<c>Shell.Application</c> 是 STA COM，
/// 与 <see cref="CardItem"/> 解析快捷方式图标的做法一致。
/// </remarks>
internal static class AppSearchIndex
{
    /// <summary>索引有效期。装 / 卸载程序不会通知我们，过期后下次呼出时重建。</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static IReadOnlyList<AppEntry>? _entries;
    private static DateTime _builtAtUtc = DateTime.MinValue;
    private static Task<IReadOnlyList<AppEntry>>? _building;

    /// <summary>明显不是「程序」的快捷方式，避免污染结果。</summary>
    private static readonly string[] NoiseKeywords =
    {
        "uninstall", "卸载", "readme", "自述", "帮助", "help", "license", "许可",
        "changelog", "更新日志", "官网", "website", "反馈", "feedback", "documentation",
    };

    /// <summary>取索引；已有且未过期时直接返回缓存。并发调用共享同一次构建。</summary>
    public static Task<IReadOnlyList<AppEntry>> GetAsync(bool forceRefresh = false)
    {
        lock (Gate)
        {
            if (!forceRefresh && _entries != null && DateTime.UtcNow - _builtAtUtc < Ttl)
                return Task.FromResult(_entries);
            if (_building is { IsCompleted: false }) return _building;

            var completion = new TaskCompletionSource<IReadOnlyList<AppEntry>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                IReadOnlyList<AppEntry> result;
                try
                {
                    result = Build();
                }
                catch (Exception ex)
                {
                    ErrorReporter.Log("AppSearchIndex.Build", ex);
                    result = Array.Empty<AppEntry>();
                }

                lock (Gate)
                {
                    _entries = result;
                    _builtAtUtc = DateTime.UtcNow;
                }

                completion.TrySetResult(result);
            })
            {
                IsBackground = true,
                Name = "WinTools app index",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            _building = completion.Task;
            return _building;
        }
    }

    /// <summary>已构建好的索引；尚未构建时返回空表（呼出瞬间先渲染空列表，随后再填充）。</summary>
    public static IReadOnlyList<AppEntry> Snapshot
    {
        get { lock (Gate) return _entries ?? Array.Empty<AppEntry>(); }
    }

    #region 构建

    private static IReadOnlyList<AppEntry> Build()
    {
        // 后加入的同名条目会被丢弃，所以来源顺序即优先级：
        // 用户开始菜单 > 公共开始菜单 > 桌面 > Shell 应用文件夹（主要用来补应用商店应用）。
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 用 Programs 而不是 StartMenu：后者下面挂着中文系统的「程序」联接点（junction），
        // 递归枚举到它必抛 UnauthorizedAccessException，整个开始菜单就一条都扫不出来。
        AddShortcutFolder(byName, Environment.GetFolderPath(Environment.SpecialFolder.Programs), "开始菜单", recursive: true);
        AddShortcutFolder(byName, Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "开始菜单", recursive: true);
        // 桌面只扫一层：再往下就是用户自己的文件夹，不是「已安装的程序」。
        AddShortcutFolder(byName, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面", recursive: false);
        AddShortcutFolder(byName, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "桌面", recursive: false);
        AddAppsFolder(byName);

        var list = byName.Values.ToList();
        foreach (var entry in list)
        {
            entry.NameLower = entry.Name.ToLowerInvariant();
            var keys = Pinyin.Build(entry.Name);
            entry.Pinyin = keys.Full;
            entry.Initials = keys.Initials;
            entry.Acronym = keys.Acronym;
        }

        return list;
    }

    private static void AddShortcutFolder(Dictionary<string, AppEntry> byName, string root, string sourceLabel, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        // IgnoreInaccessible 只跳过读不了的项而不中断枚举；跳过联接点/符号链接则避免
        // 顺着 Windows 为兼容旧版留下的目录联接绕圈（中文系统的「程序」就是其中之一）。
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", options)
                .Where(IsLaunchableShortcut)
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"AppSearchIndex.Enumerate({root})", ex);
            return;
        }

        foreach (var file in files)
        {
            string name;
            try { name = Path.GetFileNameWithoutExtension(file); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(name) || IsNoise(name)) continue;
            if (byName.ContainsKey(name)) continue;

            // 子目录名（如「Microsoft Office」）比统一的“开始菜单”更有信息量。
            var folder = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            var subtitle = string.IsNullOrWhiteSpace(folder)
                || string.Equals(folder, "Programs", StringComparison.OrdinalIgnoreCase)
                    ? sourceLabel
                    : folder;

            byName[name] = new AppEntry
            {
                Name = name,
                Target = file,
                IconPath = file,
                Subtitle = subtitle,
            };
        }
    }

    private static bool IsLaunchableShortcut(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 合并 <c>shell:AppsFolder</c>：这是开始菜单「所有应用」的数据源，
    /// 应用商店 / UWP 应用只有在这里才能被发现（它们没有 .lnk 快捷方式）。
    /// </summary>
    private static void AddAppsFolder(Dictionary<string, AppEntry> byName)
    {
        object? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return;

            shell = Activator.CreateInstance(type);
            if (shell == null) return;

            dynamic application = shell;
            dynamic folder = application.NameSpace("shell:AppsFolder");
            if (folder == null) return;

            foreach (dynamic item in folder.Items())
            {
                try
                {
                    string name = item.Name;
                    string parsing = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parsing)) continue;
                    if (IsNoise(name) || byName.ContainsKey(name)) continue;

                    // Win32 程序在这里的 Path 就是 .lnk / .exe 路径，能取到真实图标；
                    // 应用商店应用的 Path 是 AUMID，只能交给 explorer 的 shell:AppsFolder 启动。
                    var isFile = parsing.Contains(Path.DirectorySeparatorChar) && File.Exists(parsing);
                    byName[name] = new AppEntry
                    {
                        Name = name,
                        Target = isFile ? parsing : "shell:AppsFolder\\" + parsing,
                        IconPath = isFile ? parsing : "",
                        Subtitle = isFile ? "已安装程序" : "应用",
                    };
                }
                catch { /* 单项失败不影响其余条目 */ }
            }
        }
        catch (Exception ex)
        {
            // 部分系统禁用了 Shell 自动化；此时只剩快捷方式索引，功能降级但仍可用。
            ErrorReporter.Log("AppSearchIndex.AddAppsFolder", ex);
        }
        finally
        {
            if (shell != null)
            {
                try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
                catch { /* ignore */ }
            }
        }
    }

    private static bool IsNoise(string name)
    {
        foreach (var keyword in NoiseKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    #endregion

    #region 检索

    /// <summary>按查询串打分排序，返回前 <paramref name="limit"/> 条。空查询返回最常用的若干条。</summary>
    public static List<AppEntry> Search(IReadOnlyList<AppEntry> entries, string query, int limit)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0)
        {
            return entries
                .OrderByDescending(e => AppUsageStore.Count(e.Key))
                .ThenBy(e => e.Name, StringComparer.CurrentCulture)
                .Take(limit)
                .ToList();
        }

        var lower = query.ToLowerInvariant();
        // 拼音键里没有分隔符。这里压平两种情况：用户自己敲的空格（"bai du"），
        // 以及中文输入法在候选未上屏时塞进输入框的音节分隔撇号（"b'd'w'p"）——
        // 压平之后，中英文两种输入法状态下打 bdwp 都能命中「百度网盘」。
        var compact = Compact(lower);

        var scored = new List<(AppEntry Entry, int Score)>();
        foreach (var entry in entries)
        {
            var score = Score(entry, lower, compact);
            if (score <= 0) continue;
            // 用过的排前面，但加成有上限，避免常用项永远压住精确匹配。
            score += Math.Min(AppUsageStore.Count(entry.Key), 20) * 12;
            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .ThenBy(x => x.Entry.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>去掉空格与拼音音节分隔符，其余原样保留。</summary>
    private static string Compact(string text)
    {
        return text.Any(IsSeparator) ? new string(text.Where(c => !IsSeparator(c)).ToArray()) : text;
    }

    private static bool IsSeparator(char c) => c is ' ' or '\'' or '’' or '·';

    /// <summary>
    /// 单项打分，0 表示不匹配。分四档，取最高分：
    /// 原名精确/前缀/包含 > 拼音全拼与首字母 > 子序列 > 容错（编辑距离）。
    /// </summary>
    private static int Score(AppEntry entry, string queryLower, string queryCompact)
    {
        var name = entry.NameLower;
        if (name.Length == 0) return 0;

        // 先按原样匹配：名称里真的带撇号 / 点号时，它就该排在「忽略符号才能对上」的前面。
        var best = Literal(name, queryLower, 0);

        // 再用去掉分隔符的写法匹配一次，只扣一点分——中文输入法在候选未上屏时会往
        // 输入框里塞音节撇号，用户并没有真的输入它们。
        if (!string.Equals(queryCompact, queryLower, StringComparison.Ordinal))
            best = Math.Max(best, Literal(name, queryCompact, 15));

        // 拼音键比原名多一层转换，同分时让原名赢，所以统一扣一点分。
        if (entry.Pinyin.Length > 0 && !string.Equals(entry.Pinyin, name, StringComparison.Ordinal))
            best = Math.Max(best, Literal(entry.Pinyin, queryCompact, 20));

        best = Math.Max(best, Initials(entry.Initials, queryCompact));
        // 连续大写拆出来的写法只是补充（"Windows PowerShell" 也能凑出 wps），
        // 分数压到名称前缀命中之下，免得抢了真正叫这个名字的程序。
        if (entry.Acronym.Length > 0) best = Math.Max(best, Initials(entry.Acronym, queryCompact) - 120);
        if (best > 0) return best;

        // 子序列："vscd" 命中 Visual Studio Code、"bdwp" 之外的漏字也能救回来。
        best = Math.Max(SubsequenceScore(name, queryLower), SubsequenceScore(entry.Pinyin, queryCompact) - 20);
        if (best > 0) return best;

        return FuzzyScore(entry, queryCompact);
    }

    /// <summary>字面匹配：完全相等 / 前缀 / 词首包含 / 词中包含。</summary>
    private static int Literal(string haystack, string query, int penalty)
    {
        if (haystack.Length == 0 || query.Length == 0) return 0;

        if (haystack.Equals(query, StringComparison.Ordinal)) return 1000 - penalty;
        // 名字就是这么开头的，比「首字母凑得出来」更可信：
        // 输入 wps 时「WPS Office」必须压过首字母同样是 wps 的「Windows PowerShell」。
        if (haystack.StartsWith(query, StringComparison.Ordinal)) return 950 - Math.Min(haystack.Length, 40) - penalty;

        var index = haystack.IndexOf(query, StringComparison.Ordinal);
        if (index <= 0) return 0;

        // 词首命中（"code" 命中 "Visual Studio Code"）比词中命中更值钱。
        var atWordStart = !char.IsLetterOrDigit(haystack[index - 1]);
        return (atWordStart ? 700 : 500) - Math.Min(index, 60) - penalty;
    }

    /// <summary>首字母缩写匹配："bdwp" → 百度网盘，"vsc" → Visual Studio Code。</summary>
    private static int Initials(string initials, string query)
    {
        if (initials.Length == 0 || query.Length == 0) return 0;
        if (initials.Equals(query, StringComparison.Ordinal)) return 930;
        if (initials.StartsWith(query, StringComparison.Ordinal)) return 850 - Math.Min(initials.Length, 40);

        // 中间命中容易误伤（两个字母能撞上一堆条目），只给很低的分并要求长一点。
        if (query.Length >= 3 && initials.Contains(query, StringComparison.Ordinal)) return 620;
        return 0;
    }

    /// <summary>子序列匹配：命中越连续分越高。</summary>
    private static int SubsequenceScore(string haystack, string query)
    {
        if (haystack.Length == 0 || query.Length == 0) return 0;

        var position = 0;
        var score = 200;
        var lastHit = -2;
        foreach (var c in query)
        {
            var hit = haystack.IndexOf(c, position);
            if (hit < 0) return 0;
            if (hit != lastHit + 1) score -= 8;
            lastHit = hit;
            position = hit + 1;
        }

        return Math.Max(score, 1);
    }

    /// <summary>
    /// 容错档：允许少量拼错 / 漏字 / 多字 / 相邻字母打反，对原名、全拼、首字母各算一次。
    /// 只在前面几档全都没命中时才走这里，分数也压得最低，不会挤掉真正的精确匹配。
    /// </summary>
    private static int FuzzyScore(AppEntry entry, string query)
    {
        // 3 个字符以内容错就是在乱猜（"wor" 能变成一堆词），直接放弃。
        var tolerance = query.Length >= 8 ? 2 : query.Length >= 4 ? 1 : 0;
        if (tolerance == 0) return 0;

        var distance = ApproximateDistance(entry.NameLower, query, tolerance);
        distance = Math.Min(distance, ApproximateDistance(entry.Pinyin, query, tolerance));
        if (entry.Initials.Length >= 3)
            distance = Math.Min(distance, ApproximateDistance(entry.Initials, query, tolerance));

        return distance > tolerance ? 0 : 180 - distance * 60;
    }

    /// <summary>
    /// 近似子串距离：query 与 haystack 任意一段的最小编辑距离（增删改各记 1 分，
    /// 相邻两字打反也只记 1 分）。超过 <paramref name="tolerance"/> 就提前收工返回一个超标值。
    /// </summary>
    private static int ApproximateDistance(string haystack, string query, int tolerance)
    {
        var over = tolerance + 1;
        if (haystack.Length == 0 || query.Length == 0) return over;
        // 目标比查询短这么多时，光删就已经超预算了。
        if (haystack.Length + tolerance < query.Length) return over;

        var width = haystack.Length + 1;
        // 首行全 0：允许在 haystack 的任意位置开始匹配（近似子串，而不是近似前缀）。
        var beforePrevious = new int[width];
        var previous = new int[width];
        var current = new int[width];
        for (var j = 0; j < width; j++) previous[j] = 0;

        for (var i = 1; i <= query.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j < width; j++)
            {
                var cost = query[i - 1] == haystack[j - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(previous[j] + 1, current[j - 1] + 1),
                    previous[j - 1] + cost);

                // 相邻字母打反（"chrome" → "chorme"）按一次编辑算，不是两次。
                if (i > 1 && j > 1
                    && query[i - 1] == haystack[j - 2]
                    && query[i - 2] == haystack[j - 1])
                {
                    value = Math.Min(value, beforePrevious[j - 2] + 1);
                }

                current[j] = value;
                if (value < rowBest) rowBest = value;
            }

            // 整行都已经超预算，后面只会更差。
            if (rowBest > tolerance) return over;

            var spare = beforePrevious;
            beforePrevious = previous;
            previous = current;
            current = spare;
        }

        var best = over;
        // previous 此时是最后一行：query 全部用完，haystack 可以停在任意位置。
        for (var j = 0; j < width; j++)
            if (previous[j] < best) best = previous[j];
        return best;
    }

    #endregion
}

/// <summary>「悬浮搜索」的启动次数统计，用于把常用项排到前面。</summary>
internal static class AppUsageStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinTools", "search-usage.json");

    private static Dictionary<string, int>? _counts;

    private static Dictionary<string, int> Counts
    {
        get
        {
            if (_counts != null) return _counts;
            try
            {
                var data = File.Exists(StorePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(StorePath))
                    : null;
                _counts = new(data ?? new(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("AppUsageStore.Load", ex);
                _counts = new(StringComparer.OrdinalIgnoreCase);
            }

            return _counts;
        }
    }

    public static int Count(string key) =>
        string.IsNullOrEmpty(key) ? 0 : Counts.GetValueOrDefault(key, 0);

    public static void Bump(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        Counts[key] = Count(key) + 1;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temporary = StorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Counts));
            File.Move(temporary, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("AppUsageStore.Bump", ex);
        }
    }
}
