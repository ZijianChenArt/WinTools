using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinTools;

/// <summary>收纳日志里的一条记录：一个文件从哪里被移到了哪里。</summary>
public sealed class CollectedEntry
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("destination")]
    public string Destination { get; set; } = "";

    [JsonPropertyName("zone")]
    public string Zone { get; set; } = "";

    [JsonPropertyName("movedUtc")]
    public DateTime MovedUtc { get; set; }
}

/// <summary>
/// 桌面收纳：按分区规则把桌面文件<b>物理移动</b>到托管文件夹
/// <c>%USERPROFILE%\WinTools\&lt;分区名&gt;</c>，桌面因此变干净；卡片再从托管文件夹读取真实文件与图标。
/// <para>
/// 每次移动都先写入收纳日志（<c>collected.json</c>），<see cref="RestoreAll"/> 可按日志把文件原样送回桌面。
/// 只处理当前用户桌面，不动「公共桌面」；跳过隐藏 / 系统项与 desktop.ini。
/// </para>
/// </summary>
public static class DesktopCollectService
{
    private static readonly object Gate = new();

    /// <summary>托管根目录：<c>%USERPROFILE%\WinTools</c>。</summary>
    public static string ManagedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WinTools");

    private static string JournalPath => Path.Combine(ManagedRoot, "collected.json");

    /// <summary>某个分区的托管文件夹路径。</summary>
    public static string ZoneFolder(string zoneName) =>
        Path.Combine(ManagedRoot, SanitizeFolderName(zoneName));

    /// <summary>读取某个分区托管文件夹下的条目（供卡片显示）。</summary>
    public static List<string> ListZoneItems(string zoneName)
    {
        try
        {
            var dir = ZoneFolder(zoneName);
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.EnumerateFileSystemEntries(dir)
                .Where(p => !IsJournalOrSystem(p))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>卡片只显示已进入托管分区的项目，避免与桌面原生图标重复。</summary>
    public static List<string> ListCardItems(string zoneName) => ListZoneItems(zoneName);

    /// <summary>桌面上按当前分区规则「将会被收纳」的项数量（供确认对话框预览）。</summary>
    public static int PreviewCount(out List<string> samples)
    {
        var plan = BuildPlan();
        samples = plan.Take(8).Select(p => Path.GetFileName(p.Source)).ToList();
        return plan.Count;
    }

    /// <summary>执行收纳。返回成功移动的条数；失败项跳过并计入 <paramref name="failed"/>。</summary>
    public static int CollectAll(out int failed)
    {
        lock (Gate)
        {
            failed = 0;
            var plan = BuildPlan();
            if (plan.Count == 0) return 0;

            var journal = LoadJournal();
            var moved = 0;

            foreach (var item in plan)
            {
                try
                {
                    var zoneDir = ZoneFolder(item.Zone);
                    Directory.CreateDirectory(zoneDir);

                    var dest = ResolveCollision(Path.Combine(zoneDir, Path.GetFileName(item.Source)));
                    if (Directory.Exists(item.Source))
                        Directory.Move(item.Source, dest);
                    else
                        File.Move(item.Source, dest);

                    journal.Add(new CollectedEntry
                    {
                        Source = item.Source,
                        Destination = dest,
                        Zone = item.Zone,
                        MovedUtc = DateTime.UtcNow,
                    });
                    // 每移动一项立即记录，避免进程意外终止后出现无法还原的文件。
                    SaveJournal(journal);
                    moved++;
                }
                catch
                {
                    failed++;
                }
            }

            return moved;
        }
    }

    /// <summary>
    /// 将 DeskBox 旧版桌面文件区中的顶层项目导入当前分区。只读取用户目录下已知的
    /// “我的桌面”和“DeskBox”两个旧文件夹，不递归触碰 DeskBox 的其它应用数据。
    /// 每项仍写入收纳日志，因此“全部还原”可将其放回原来的 DeskBox 目录。
    /// </summary>
    public static int ImportLegacyDeskBoxItems(out int failed)
    {
        lock (Gate)
        {
            failed = 0;
            var zones = ConfigService.Load().DesktopZones;
            if (zones is not { Count: > 0 }) return 0;

            var deskBoxRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeskBox");
            var sourceDirectories = new[] { "我的桌面", "DeskBox" }
                .Select(name => Path.Combine(deskBoxRoot, name))
                .Where(Directory.Exists)
                .ToList();
            if (sourceDirectories.Count == 0) return 0;

            var journal = LoadJournal();
            var moved = 0;
            foreach (var sourceDirectory in sourceDirectories)
            {
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos().ToList(); }
                catch { failed++; continue; }

                foreach (var entry in entries)
                {
                    try
                    {
                        if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                            continue;
                        var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                        var zoneIndex = DesktopZoneMatcher.Match(entry.Name, entry.FullName, isDirectory, zones);
                        if (zoneIndex < 0 || zoneIndex >= zones.Count) { failed++; continue; }

                        var zone = zones[zoneIndex].Name;
                        var zoneDir = ZoneFolder(zone);
                        Directory.CreateDirectory(zoneDir);
                        var destination = ResolveCollision(Path.Combine(zoneDir, entry.Name));
                        if (isDirectory)
                            Directory.Move(entry.FullName, destination);
                        else
                            File.Move(entry.FullName, destination);

                        journal.Add(new CollectedEntry
                        {
                            Source = entry.FullName,
                            Destination = destination,
                            Zone = zone,
                            MovedUtc = DateTime.UtcNow,
                        });
                        SaveJournal(journal);
                        moved++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            return moved;
        }
    }

    /// <summary>
    /// 按当前分类规则重新整理已经在 WinTools 托管根目录中的项目。分类改版后旧目录不会
    /// 自动消失，因此需要物理迁移并同步修改 journal.destination，保证“全部还原”仍有效。
    /// </summary>
    public static int ReclassifyManagedItems(out int failed)
    {
        lock (Gate)
        {
            failed = 0;
            var zones = ConfigService.Load().DesktopZones;
            if (zones is not { Count: > 0 } || !Directory.Exists(ManagedRoot)) return 0;

            var journal = LoadJournal();
            var moved = 0;
            var zoneDirectories = Directory.EnumerateDirectories(ManagedRoot).ToList();
            foreach (var sourceDirectory in zoneDirectories)
            {
                IEnumerable<FileSystemInfo> entries;
                try { entries = new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos().ToList(); }
                catch { failed++; continue; }

                foreach (var entry in entries)
                {
                    try
                    {
                        if (IsJournalOrSystem(entry.FullName)) continue;
                        var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                        var zoneIndex = DesktopZoneMatcher.Match(entry.Name, entry.FullName, isDirectory, zones);
                        if (zoneIndex < 0 || zoneIndex >= zones.Count) { failed++; continue; }

                        var zone = zones[zoneIndex].Name;
                        var destinationDirectory = ZoneFolder(zone);
                        if (string.Equals(
                            Path.TrimEndingDirectorySeparator(sourceDirectory),
                            Path.TrimEndingDirectorySeparator(destinationDirectory),
                            StringComparison.OrdinalIgnoreCase))
                            continue;

                        Directory.CreateDirectory(destinationDirectory);
                        var previousPath = entry.FullName;
                        var destination = ResolveCollision(Path.Combine(destinationDirectory, entry.Name));
                        if (isDirectory)
                            Directory.Move(previousPath, destination);
                        else
                            File.Move(previousPath, destination);

                        foreach (var record in journal.Where(record =>
                                     string.Equals(record.Destination, previousPath, StringComparison.OrdinalIgnoreCase)))
                        {
                            record.Destination = destination;
                            record.Zone = zone;
                        }
                        SaveJournal(journal);
                        moved++;
                    }
                    catch
                    {
                        failed++;
                    }
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any()
                        && !zones.Any(zone => string.Equals(
                            ZoneFolder(zone.Name), sourceDirectory, StringComparison.OrdinalIgnoreCase)))
                        Directory.Delete(sourceDirectory, recursive: false);
                }
                catch { /* 空旧目录清理失败不影响分类结果 */ }
            }

            return moved;
        }
    }

    /// <summary>按收纳日志把所有文件送回原位置。返回成功还原的条数。</summary>
    public static int RestoreAll(out int failed)
    {
        lock (Gate)
        {
            failed = 0;
            var journal = LoadJournal();
            if (journal.Count == 0) return 0;

            var remaining = new List<CollectedEntry>();
            var restored = 0;

            foreach (var entry in journal)
            {
                try
                {
                    if (!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination))
                        continue; // 已被用户手动移走：从日志里丢弃，不报错

                    var back = ResolveCollision(entry.Source);
                    Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                    if (Directory.Exists(entry.Destination))
                        Directory.Move(entry.Destination, back);
                    else
                        File.Move(entry.Destination, back);
                    restored++;
                }
                catch
                {
                    failed++;
                    remaining.Add(entry); // 还原失败的留在日志里，下次可重试
                }
            }

            SaveJournal(remaining);
            return restored;
        }
    }

    /// <summary>当前收纳日志里的条目数（>0 表示桌面上有文件已被收走）。</summary>
    public static int CollectedCount()
    {
        try { return LoadJournal().Count; }
        catch { return 0; }
    }

    #region 删除到回收站

    // SHFileOperation 是 Win32 "移到回收站" 的标准入口，FOF_ALLOWUNDO 标志让
    // 删除可撤销（即真正移到回收站），同时把静默模式打开（FOF_NOCONFIRMATION），
    // 避免弹系统确认对话框——我们的右键菜单已经算用户主动操作。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOERRORUI = 0x0400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>删除结果。</summary>
    public enum RecycleBinResult
    {
        Success = 0,
        NotFound = 1,
        Aborted = 2,
        Failed = 3,
    }

    /// <summary>把文件 / 文件夹移到回收站。卡片右键菜单调用。</summary>
    /// <remarks>
    /// 通过 SHFileOperation + FOF_ALLOWUNDO 走系统回收站（不是真删除），用户可在资源管理器
    /// 「回收站」里还原。文件路径必须以双 null 结尾（SHFileOperation 的字符串约定）。
    /// 同时从收纳日志（collected.json）里移除相关条目，否则用户点"全部还原"会把已删除的文件
    /// 当作"已收走"再创建一份空壳到桌面。
    /// </remarks>
    public static RecycleBinResult DeleteToRecycleBin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RecycleBinResult.NotFound;
        var existsAsFile = File.Exists(path);
        var existsAsDir = Directory.Exists(path);
        if (!existsAsFile && !existsAsDir)
            return RecycleBinResult.NotFound;

        // SHFileOperation 要求 pFrom 是双 null 结尾的 Unicode 字符串。
        var pathWithDoubleNull = path + "\0\0";
        var pFrom = Marshal.StringToHGlobalUni(pathWithDoubleNull);
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                pFrom = pFrom,
                pTo = IntPtr.Zero,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = IntPtr.Zero,
            };
            var rc = SHFileOperationW(ref op);
            // SHFileOperation 返回 0 = 成功；非 0 多数是用户取消或系统错误。
            if (rc != 0 || op.fAnyOperationsAborted)
                return RecycleBinResult.Aborted;

            // 移完后文件应该已经不在原位置。如果还在（例如回收站被禁用），按失败处理。
            if (File.Exists(path) || Directory.Exists(path))
                return RecycleBinResult.Failed;

            RemoveJournalEntriesFor(path);
            return RecycleBinResult.Success;
        }
        catch
        {
            return RecycleBinResult.Failed;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
        }
    }

    /// <summary>从收纳日志里剔除路径与已删除项匹配的条目（按 Source 或 Destination 任一字段）。</summary>
    private static void RemoveJournalEntriesFor(string path)
    {
        try
        {
            var journal = LoadJournal();
            var full = System.IO.Path.GetFullPath(path);
            var remaining = journal.Where(entry =>
                !string.Equals(System.IO.Path.GetFullPath(entry.Source), full, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(System.IO.Path.GetFullPath(entry.Destination), full, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count != journal.Count)
                SaveJournal(remaining);
        }
        catch
        {
            // 日志清理失败不影响主流程。
        }
    }

    #endregion

    #region 跨分区移动

    /// <summary>跨分区移动结果。</summary>
    public enum MoveZoneResult
    {
        Success = 0,
        SourceNotFound = 1,
        SameZone = 2,
        TargetInvalid = 3,
        PhysicalFailed = 4,
    }

    /// <summary>把托管文件/文件夹从当前所在分区移动到另一个分区。
    /// 物理移动 + 更新 collected.json。FileSystemWatcher 会自动触发双方分区卡片刷新。</summary>
    public static MoveZoneResult MoveItemToZone(string sourcePath, string targetZoneName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetZoneName))
            return MoveZoneResult.TargetInvalid;
        var isFile = File.Exists(sourcePath);
        var isDir = Directory.Exists(sourcePath);
        if (!isFile && !isDir) return MoveZoneResult.SourceNotFound;

        // 目标分区不能是"其他"——用户拖动时"其他"分区是兜底，不应该手动塞东西进去。
        // 也不允许把项目移到与当前所在分区相同的"其他"分区（即自己所在的分区）。
        // 通过检查 sourcePath 是否在目标分区目录下判断。
        var targetDir = ZoneFolder(targetZoneName);
        if (string.IsNullOrEmpty(targetDir)) return MoveZoneResult.TargetInvalid;
        var sourceFull = Path.GetFullPath(sourcePath).TrimEnd('\\');
        var targetFull = Path.GetFullPath(targetDir).TrimEnd('\\');
        if (sourceFull.Equals(targetFull, StringComparison.OrdinalIgnoreCase) ||
            sourceFull.StartsWith(targetFull + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return MoveZoneResult.SameZone;
        }

        Directory.CreateDirectory(targetDir);
        var fileName = Path.GetFileName(sourcePath);
        var dest = ResolveCollision(Path.Combine(targetDir, fileName));
        try
        {
            if (isDir)
                Directory.Move(sourcePath, dest);
            else
                File.Move(sourcePath, dest);
        }
        catch
        {
            return MoveZoneResult.PhysicalFailed;
        }

        // 更新 collected.json：把旧条目的 Destination 改成新路径（Source 仍是桌面原路径）。
        // 如果原条目不存在（用户手动放进去的），添加新条目。
        var journal = LoadJournal();
        var updated = false;
        for (var i = 0; i < journal.Count; i++)
        {
            var entry = journal[i];
            if (string.Equals(
                Path.GetFullPath(entry.Destination).TrimEnd('\\'),
                sourceFull,
                StringComparison.OrdinalIgnoreCase))
            {
                journal[i] = new CollectedEntry
                {
                    Source = entry.Source,
                    Destination = dest,
                    Zone = targetZoneName,
                    MovedUtc = DateTime.UtcNow,
                };
                updated = true;
                break;
            }
        }
        if (!updated)
        {
            journal.Add(new CollectedEntry
            {
                Source = sourcePath,
                Destination = dest,
                Zone = targetZoneName,
                MovedUtc = DateTime.UtcNow,
            });
        }
        SaveJournal(journal);
        return MoveZoneResult.Success;
    }

    #endregion

    /// <summary>读取桌面（用户 + 公共）与已托管分区中的项目名称，供分区规则编辑器选择。</summary>
    public static List<string> ListAvailableItemNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var desktop in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(desktop))
                    if (!IsJournalOrSystem(entry)) names.Add(Path.GetFileName(entry));
            }

            foreach (var zone in ConfigService.Load().DesktopZones ?? new List<DesktopZone>())
                foreach (var path in ListZoneItems(zone.Name))
                    names.Add(Path.GetFileName(path));
        }
        catch { /* 返回当前已读取的名称 */ }

        return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    #region 计划与匹配

    private readonly record struct PlanItem(string Source, string Zone);

    /// <summary>扫描用户桌面 + 公共桌面，按分区规则决定每一项应归入哪个分区。
    /// 公共桌面（<c>C:\Users\Public\Desktop</c>）所有用户共享，之前只扫用户桌面
    /// 导致公共桌面的快捷方式无法被收纳——2026-08-31 修复。</summary>
    private static List<PlanItem> BuildPlan()
    {
        var result = new List<PlanItem>();
        var zones = ConfigService.Load().DesktopZones;
        if (zones is not { Count: > 0 }) return result;

        var desktops = new List<string>();
        try
        {
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(userDesktop)) desktops.Add(userDesktop);
        }
        catch { /* 单独 try，整体不挂 */ }
        try
        {
            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrEmpty(commonDesktop)
                && !desktops.Contains(commonDesktop, StringComparer.OrdinalIgnoreCase))
                desktops.Add(commonDesktop);
        }
        catch { /* 公共桌面目录某些系统不存在或没权限 */ }

        foreach (var desktop in desktops)
        {
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(desktop).EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                try
                {
                    if (IsJournalOrSystem(entry.FullName)) continue;
                    // 不移动包含当前可执行文件的桌面工程/安装目录，否则会破坏正在运行的程序。
                    if (IsSamePathOrAncestor(entry.FullName, AppContext.BaseDirectory)) continue;
                    var attrs = entry.Attributes;
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;

                    var isDir = (attrs & FileAttributes.Directory) == FileAttributes.Directory;
                    var name = entry.Name;
                    var zoneIndex = DesktopZoneMatcher.Match(name, entry.FullName, isDir, zones);
                    if (zoneIndex < 0 || zoneIndex >= zones.Count) continue;

                    // 同一个文件路径（用户桌面 / 公共桌面 解析到同一 source）已经入过则跳过
                    if (result.Any(p => string.Equals(p.Source, entry.FullName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    result.Add(new PlanItem(entry.FullName, zones[zoneIndex].Name));
                }
                catch { /* 单项失败不影响整体 */ }
            }
        }

        return result;
    }

    #endregion

    #region 日志读写

    private static List<CollectedEntry> LoadJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return new List<CollectedEntry>();
            var json = File.ReadAllText(JournalPath);
            return JsonSerializer.Deserialize<List<CollectedEntry>>(json) ?? new List<CollectedEntry>();
        }
        catch { return new List<CollectedEntry>(); }
    }

    private static void SaveJournal(List<CollectedEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(ManagedRoot);
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            var tmp = JournalPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, JournalPath, overwrite: true);
        }
        catch { /* 写日志失败不阻断，但下次无法还原这批 */ }
    }

    #endregion

    #region 辅助

    /// <summary>目标已存在时追加 " (2)"、" (3)"… 直到不冲突。</summary>
    private static string ResolveCollision(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;

        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    private static bool IsJournalOrSystem(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "collected.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "collected.json.tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSamePathOrAncestor(string candidate, string child)
    {
        try
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            var fullChild = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
            return string.Equals(parent, fullChild, StringComparison.OrdinalIgnoreCase)
                || fullChild.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "未命名" : clean;
    }

    #endregion
}
