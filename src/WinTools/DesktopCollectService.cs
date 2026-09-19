using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinTools.Services;

namespace WinTools;

/// <summary>旧版收纳日志的一条记录：一个文件从哪里被移到了哪里。</summary>
/// <remarks>
/// 新版**不再移动文件**，这个类型只在一次性迁移（<see cref="DesktopCollectService.MigrateLegacyManagedItems"/>）
/// 里读旧日志时用到，读完即弃。
/// </remarks>
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
/// 桌面分区的数据源。**不移动文件、不改文件属性**：桌面上的东西永远待在
/// <c>桌面\xxx</c>，卡片只是按 <see cref="DesktopZoneMatcher"/> 的规则把它们实时分组显示。
/// </summary>
/// <remarks>
/// 桌面之所以看起来干净，是因为 <see cref="DesktopIconVisibilityService"/> 关掉了系统的
/// 「显示桌面图标」，和文件本身无关。
/// <para>
/// 2026-09-07 之前的做法是把桌面项目物理搬进 <c>%USERPROFILE%\WinTools\&lt;分区名&gt;</c>，
/// 并用 <c>collected.json</c> 记账以便还原。那套机制已经删除，原因见 README 第 7.1 节：
/// 它会改变文件真实路径，还会和正在写盘的下载 / 正打开着文件的程序抢占用。
/// 旧数据由 <see cref="MigrateLegacyManagedItems"/> 一次性搬回桌面。
/// </para>
/// </remarks>
public static class DesktopCollectService
{
    private static readonly object Gate = new();

    /// <summary>旧版托管根目录 <c>%USERPROFILE%\WinTools</c>。**只用于迁移**，新逻辑不再写入。</summary>
    public static string ManagedRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WinTools");

    private static string JournalPath => Path.Combine(ManagedRoot, "collected.json");

    #region 桌面枚举与分组

    /// <summary>用户桌面 + 公共桌面。公共桌面放的是所有用户共享的快捷方式（装软件时留下的）。</summary>
    private static List<string> DesktopFolders()
    {
        var folders = new List<string>();
        try
        {
            var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(userDesktop)) folders.Add(userDesktop);
        }
        catch { /* 单独 try，整体不挂 */ }
        try
        {
            var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if (!string.IsNullOrWhiteSpace(commonDesktop)
                && !folders.Contains(commonDesktop, StringComparer.OrdinalIgnoreCase))
                folders.Add(commonDesktop);
        }
        catch { /* 某些系统没有公共桌面或没权限 */ }
        return folders;
    }

    /// <summary>枚举桌面上应当由卡片显示的项目。跳过 desktop.ini 和用户自己设了隐藏/系统属性的项。</summary>
    /// <remarks>
    /// **不再跳过本程序所在目录**：旧版跳过它是因为会物理移动文件，搬走正在运行的程序会出事；
    /// 现在不移动任何东西，而且桌面图标是整体隐藏的——不显示它反而会让人找不到。
    /// </remarks>
    private static List<FileSystemInfo> EnumerateDesktopEntries()
    {
        var result = new List<FileSystemInfo>();
        // 按**名称**去重，不是按完整路径。装软件时很多安装程序会同时往「当前用户桌面」和
        // 「公共桌面」各写一个同名快捷方式（例如极空间，两个 .lnk 指向同一个 exe），
        // 按路径去重的话两个都会进卡片，用户看到的就是一模一样的两个图标。
        // DesktopFolders() 把用户桌面排在前面，所以先到先得 = 保留用户桌面那一个。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var desktop in DesktopFolders())
        {
            if (!Directory.Exists(desktop)) continue;

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(desktop).EnumerateFileSystemInfos(); }
            catch (Exception ex)
            {
                ErrorReporter.Log($"DesktopCollectService.Enumerate({desktop})", ex);
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    if (IsShellSystemFile(entry.FullName)) continue;
                    if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (!seen.Add(entry.Name)) continue;
                    result.Add(entry);
                }
                catch { /* 单项失败不影响整体 */ }
            }
        }

        return result;
    }

    /// <summary>把桌面上的项目按分区规则分组。返回的每个分区都存在于 <paramref name="zones"/> 中。</summary>
    /// <remarks>
    /// 桌面图标整体隐藏后，**任何一项都必须落进某张卡片**，否则它在桌面上就彻底没有入口了。
    /// 所以规则没命中时兜底到最后一个分区，而不是丢弃。
    /// </remarks>
    public static Dictionary<string, List<string>> GroupByZone(List<DesktopZone>? zones)
    {
        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (zones is not { Count: > 0 }) return grouped;

        foreach (var zone in zones)
            grouped[zone.Name] = new List<string>();

        foreach (var entry in EnumerateDesktopEntries())
        {
            try
            {
                var isDirectory = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                var index = DesktopZoneMatcher.Match(entry.Name, entry.FullName, isDirectory, zones);
                if (index < 0 || index >= zones.Count) index = zones.Count - 1;   // 兜底，见上面的注释
                grouped[zones[index].Name].Add(entry.FullName);
            }
            catch { /* 单项失败不影响整体 */ }
        }

        // 系统虚拟图标（此电脑 / 回收站 / 网络…）在桌面文件夹里没有文件，上面的枚举看不到它们。
        // 桌面图标整体隐藏后必须由卡片接管，否则用户再也点不到。按**显示名**参与规则匹配
        // （默认分区「文件夹与文件」的关键词里本来就有「此电脑 / 回收站 / 网络 / 控制面板」）。
        foreach (var (parsingName, displayName) in DesktopShellItems.EnumerateVisible())
        {
            try
            {
                var index = DesktopZoneMatcher.Match(displayName, parsingName, isDirectory: true, zones);
                if (index < 0 || index >= zones.Count) index = zones.Count - 1;
                grouped[zones[index].Name].Add(parsingName);
            }
            catch { /* 单项失败不影响整体 */ }
        }

        foreach (var (zone, list) in grouped)
        {
            var sorted = CardItemOrderStore.Apply(zone, list);
            list.Clear();
            list.AddRange(sorted);
        }

        return grouped;
    }

    /// <summary>读取单个分区当前应显示的桌面项目。</summary>
    public static List<string> ListCardItems(string zoneName)
    {
        var zones = SettingsService.Instance.Current.DesktopZones;
        return GroupByZone(zones).TryGetValue(zoneName, out var items) ? items : new List<string>();
    }

    /// <summary>桌面上被卡片接管的项目总数（= 桌面上可见项目数）。</summary>
    public static int ManagedItemCount()
    {
        try { return EnumerateDesktopEntries().Count; }
        catch { return 0; }
    }

    #endregion

    #region 跨分区归属

    /// <summary>跨分区移动结果。</summary>
    public enum MoveZoneResult
    {
        Success = 0,
        SourceNotFound = 1,
        SameZone = 2,
        TargetInvalid = 3,
        PhysicalFailed = 4,
    }

    /// <summary>判断能否把某个桌面项目改判到另一个分区。</summary>
    /// <remarks>
    /// **不动文件**。真正的归属写入是把名字加进目标分区的显式清单
    /// （<c>DesktopZone.Items</c>，由 <c>DesktopCardManager.Card_ItemMovedIn</c> 落盘），
    /// 下一次 <see cref="DesktopZoneMatcher.Match"/> 就会把它算进目标分区。
    /// </remarks>
    public static MoveZoneResult CanAssignToZone(string sourcePath, string targetZoneName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetZoneName))
            return MoveZoneResult.TargetInvalid;
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            return MoveZoneResult.SourceNotFound;

        var zones = SettingsService.Instance.Current.DesktopZones;
        if (zones is not { Count: > 0 }) return MoveZoneResult.TargetInvalid;
        if (!zones.Any(z => string.Equals(z.Name, targetZoneName, StringComparison.Ordinal)))
            return MoveZoneResult.TargetInvalid;

        try
        {
            var name = Path.GetFileName(sourcePath);
            var isDirectory = Directory.Exists(sourcePath);
            var index = DesktopZoneMatcher.Match(name, sourcePath, isDirectory, zones);
            if (index >= 0 && index < zones.Count
                && string.Equals(zones[index].Name, targetZoneName, StringComparison.Ordinal))
                return MoveZoneResult.SameZone;
        }
        catch { /* 判不出来就当作可以改判 */ }

        return MoveZoneResult.Success;
    }

    #endregion

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

    private const uint FO_RENAME = 0x0004;
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

    /// <summary>重命名结果。</summary>
    public enum RenameResult
    {
        Success = 0,
        /// <summary>原文件已经不在了（可能刚被别处移走 / 删掉）。</summary>
        NotFound = 1,
        /// <summary>新名字为空、只有空白，或含有 Windows 不允许的字符。</summary>
        InvalidName = 2,
        /// <summary>同目录下已经有同名文件或文件夹。</summary>
        NameExists = 3,
        /// <summary>Shell 拒绝或没有权限（例如公共桌面下的项目需要管理员）。</summary>
        Failed = 4,
    }

    /// <summary>重命名桌面上的一个文件 / 文件夹。卡片右键菜单「重命名」调用。</summary>
    /// <param name="path">原完整路径。</param>
    /// <param name="newFileName">新的**完整文件名**（含扩展名），不是路径。</param>
    /// <param name="newPath">成功时返回新的完整路径。</param>
    /// <remarks>
    /// 和删除一样走 <c>SHFileOperation</c>（<c>FO_RENAME</c>）而不是 <c>File.Move</c>：
    /// Shell 会自己发变更通知，资源管理器桌面上的图标同步更新；指向它的快捷方式也由 Shell 处理。
    /// 校验放在托管侧先做，这样能把「名字非法」「重名」和「没权限」区分开来给用户不同提示，
    /// SHFileOperation 的返回码做不到这一点。
    /// </remarks>
    public static RenameResult RenameItem(string path, string newFileName, out string newPath)
    {
        newPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return RenameResult.NotFound;
        if (!File.Exists(path) && !Directory.Exists(path)) return RenameResult.NotFound;

        newFileName = newFileName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newFileName)) return RenameResult.InvalidName;
        // 目录分隔符也在 GetInvalidFileNameChars 里，所以"输入了一整条路径"同样会被挡住。
        if (newFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return RenameResult.InvalidName;
        if (newFileName is "." or "..") return RenameResult.InvalidName;
        // 尾部的点和空格会被文件系统悄悄吃掉，直接按非法处理，免得改完名字和用户输入的不一样。
        if (newFileName.EndsWith(' ') || newFileName.EndsWith('.')) return RenameResult.InvalidName;

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return RenameResult.Failed;
        var target = Path.Combine(directory, newFileName);

        // 只改大小写（a.txt → A.txt）时目标"已存在"的就是它自己，不能当成重名。
        var sameItem = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!sameItem && (File.Exists(target) || Directory.Exists(target))) return RenameResult.NameExists;

        var pFrom = Marshal.StringToHGlobalUni(path + "  ");
        var pTo = Marshal.StringToHGlobalUni(target + "  ");
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_RENAME,
                pFrom = pFrom,
                pTo = pTo,
                fFlags = FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = IntPtr.Zero,
            };
            var rc = SHFileOperationW(ref op);
            if (rc != 0 || op.fAnyOperationsAborted)
            {
                ErrorReporter.Log("DesktopCollectService.RenameItem",
                    new InvalidOperationException($"SHFileOperation(FO_RENAME) 返回 {rc}，目标 {target}"));
                return RenameResult.Failed;
            }
            if (!File.Exists(target) && !Directory.Exists(target)) return RenameResult.Failed;

            newPath = target;
            return RenameResult.Success;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopCollectService.RenameItem", ex);
            return RenameResult.Failed;
        }
        finally
        {
            Marshal.FreeHGlobal(pFrom);
            Marshal.FreeHGlobal(pTo);
        }
    }

    /// <summary>把文件 / 文件夹移到回收站。卡片右键菜单调用。</summary>
    /// <remarks>
    /// 通过 SHFileOperation + FOF_ALLOWUNDO 走系统回收站（不是真删除），用户可在资源管理器
    /// 「回收站」里还原。文件路径必须以双 null 结尾（SHFileOperation 的字符串约定）。
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

    #endregion

    /// <summary>读取桌面上的项目名称，供分区规则编辑器选择。</summary>
    public static List<string> ListAvailableItemNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in EnumerateDesktopEntries())
                names.Add(entry.Name);
        }
        catch { /* 返回当前已读取的名称 */ }

        return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    #region 旧版数据迁移（一次性）

    /// <summary>旧版托管目录里还有没有文件（有就说明需要迁移）。</summary>
    public static bool HasLegacyManagedItems()
    {
        try
        {
            if (!Directory.Exists(ManagedRoot)) return false;
            return Directory.EnumerateDirectories(ManagedRoot)
                .Any(dir => Directory.EnumerateFileSystemEntries(dir).Any(p => !IsShellSystemFile(p)));
        }
        catch { return false; }
    }

    /// <summary>
    /// 把旧版搬进 <c>%USERPROFILE%\WinTools\&lt;分区名&gt;</c> 的项目**搬回桌面**，
    /// 并返回「文件名 → 分区名」的归属，交给调用方写进配置的显式清单。
    /// </summary>
    /// <remarks>
    /// 优先还原到 <c>collected.json</c> 记的原路径；公共桌面来源统一落到当前用户桌面
    /// （改公共桌面要管理员权限）。目标重名时自动追加 " (2)"。
    /// 迁移前会把旧日志另存为 <c>collected.json.migrated-&lt;时间戳&gt;</c>，不直接删。
    /// </remarks>
    public static int MigrateLegacyManagedItems(out int failed, out Dictionary<string, string> zoneByName)
    {
        lock (Gate)
        {
            failed = 0;
            zoneByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var moved = 0;

            try
            {
                if (!Directory.Exists(ManagedRoot)) return 0;

                var zones = SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>();
                var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(userDesktop) || !Directory.Exists(userDesktop)) return 0;

                foreach (var zoneDir in Directory.EnumerateDirectories(ManagedRoot).ToList())
                {
                    var folderName = Path.GetFileName(zoneDir);
                    // 目录名是分区名的“合法化”结果，反查回真正的分区名。
                    var zoneName = zones.FirstOrDefault(z =>
                        string.Equals(SanitizeFolderName(z.Name), folderName, StringComparison.OrdinalIgnoreCase))?.Name;

                    List<FileSystemInfo> entries;
                    try { entries = new DirectoryInfo(zoneDir).EnumerateFileSystemInfos().ToList(); }
                    catch (Exception ex) { ErrorReporter.Log($"Migrate.Enumerate({zoneDir})", ex); failed++; continue; }

                    foreach (var entry in entries)
                    {
                        try
                        {
                            if (IsShellSystemFile(entry.FullName)) continue;

                            // **一律落到当前用户桌面**，不要照搬日志里的原路径。
                            // 2026-09-07 实测：62 项里有 13 项的 Source 指向旧 DeskBox 目录
                            // （`%USERPROFILE%\DeskBox\...`，早年从那个软件导入的），另有 2 项来自
                            // 公共桌面（改那里要管理员权限）。按原路径还原会把它们送回桌面**之外**，
                            // 于是既不在桌面、也不在卡片里，等于凭空消失。
                            var destination = ResolveCollision(Path.Combine(userDesktop, entry.Name));
                            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                            if (entry is DirectoryInfo)
                                Directory.Move(entry.FullName, destination);
                            else
                                File.Move(entry.FullName, destination);

                            if (!string.IsNullOrEmpty(zoneName))
                                zoneByName[Path.GetFileName(destination)] = zoneName;
                            moved++;
                        }
                        catch (Exception ex)
                        {
                            ErrorReporter.Log($"Migrate.Move({entry.FullName})", ex);
                            failed++;
                        }
                    }

                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(zoneDir).Any())
                            Directory.Delete(zoneDir, recursive: false);
                    }
                    catch { /* 空目录删不掉不影响迁移结果 */ }
                }

                ArchiveJournal();
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("DesktopCollectService.MigrateLegacyManagedItems", ex);
            }

            return moved;
        }
    }

    /// <summary>迁移遗留的分区目录改名。新逻辑没有分区目录，没有遗留目录时直接成功。</summary>
    public static bool RenameZone(string oldName, string newName)
    {
        lock (Gate)
        {
            try
            {
                var source = Path.Combine(ManagedRoot, SanitizeFolderName(oldName));
                var destination = Path.Combine(ManagedRoot, SanitizeFolderName(newName));
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return true;
                if (!Directory.Exists(source)) return true;      // 常态：早就没有分区目录了
                if (Directory.Exists(destination)) return false;

                Directory.Move(source, destination);
                return true;
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("DesktopCollectService.RenameZone", ex);
                return false;
            }
        }
    }

    private static List<CollectedEntry> LoadJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return new List<CollectedEntry>();
            return JsonSerializer.Deserialize<List<CollectedEntry>>(File.ReadAllText(JournalPath))
                ?? new List<CollectedEntry>();
        }
        catch { return new List<CollectedEntry>(); }
    }

    /// <summary>迁移完成后把旧日志改名留档，而不是删除——万一迁移有问题还能人工对账。</summary>
    private static void ArchiveJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            var archived = JournalPath + $".migrated-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(JournalPath, archived, overwrite: false);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopCollectService.ArchiveJournal", ex);
        }
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

    private static bool IsShellSystemFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "collected.json", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("collected.json.", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "未命名" : clean;
    }

    #endregion
}
