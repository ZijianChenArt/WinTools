using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinTools;

/// <summary>
/// 桌面整理：开启时备份当前桌面图标排布，再按文件类型分组、纵向排列；
/// 关闭时按备份还原回原本排布，不破坏用户原有布局。
///
/// 实现方式：通过 Win32 操作桌面 SysListView32（属于 explorer.exe），
/// 跨进程读取/写入图标位置与名称，并临时关闭“自动排列”使自定义坐标生效。
/// </summary>
public sealed class DesktopOrganizeService
{
    private readonly object _sync = new();
    private readonly DesktopIconPositionService _iconPositionService = new();
    private int _dragRestoreVersion;
    private bool _organized;

    public DesktopOrganizeService()
    {
        _iconPositionService.DesktopDragEnded += (_, _) => RestoreOrganizedPositionAfterDrag();
    }

    /// <summary>当前是否处于已整理状态。</summary>
    public bool IsOrganized
    {
        get { lock (_sync) return _organized; }
    }

    /// <summary>备份文件路径（与 config.json 同目录）。</summary>
    private static string BackupPath
    {
        get
        {
            var dir = Path.GetDirectoryName(ConfigService.ConfigFilePath);
            if (string.IsNullOrEmpty(dir))
                dir = AppContext.BaseDirectory;
            return Path.Combine(dir!, "desktop_layout_backup.json");
        }
    }

    /// <summary>切换启用状态。enabled=true 整理桌面；false 还原桌面。</summary>
    /// <returns>操作是否成功。失败时 error 含原因。</returns>
    public bool SetEnabled(bool enabled, out string? error)
    {
        lock (_sync)
        {
            if (enabled)
            {
                var organized = OrganizeCore(out error);
                if (organized || _organized)
                    _iconPositionService.SetEnabled(true);
                return organized;
            }

            Interlocked.Increment(ref _dragRestoreVersion);
            _iconPositionService.SetEnabled(false);
            return RestoreCore(out error);
        }
    }

    /// <summary>
    /// 保留完整的 Shell 拖放过程；鼠标松开后立即恢复当前分区坐标。
    /// 恢复在后台线程执行，避免阻塞全局鼠标钩子和 Shell 的 Drop 处理。
    /// </summary>
    private void RestoreOrganizedPositionAfterDrag()
    {
        var version = Interlocked.Increment(ref _dragRestoreVersion);
        _ = Task.Run(() =>
        {
            if (version != Volatile.Read(ref _dragRestoreVersion)) return;

            lock (_sync)
            {
                if (_organized)
                    OrganizeCore(out _);
            }
        });
    }

    /// <summary>
    /// 启动时调用：若上次异常退出残留了备份（孤儿备份）且本次未启用，则还原并清理。
    /// </summary>
    public void RecoverOrphanBackupIfNeeded()
    {
        lock (_sync)
        {
            if (_organized) return;
            if (File.Exists(BackupPath))
                RestoreCore(out _);
        }
    }

    /// <summary>读取当前桌面“已选中”图标的显示名称（供桌面多选后分组用）。</summary>
    /// <summary>读取当前桌面所有图标的显示名称（供界面“指定文件”勾选用）。</summary>
    public List<string> GetDesktopItemNames()
    {
        var names = new List<string>();
        var hList = GetDesktopListView();
        if (hList == IntPtr.Zero) return names;

        var count = (int)SendMessage(hList, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return names;

        GetWindowThreadProcessId(hList, out var pid);
        var hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return names;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var name = ReadItemText(hProc, hList, i);
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }
        finally
        {
            CloseHandle(hProc);
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    #region 整理 / 还原

    private bool OrganizeCore(out string? error)
    {
        error = null;
        var hList = GetDesktopListView();
        if (hList == IntPtr.Zero)
        {
            error = "未找到桌面图标列表（请确认桌面已显示图标）。";
            return false;
        }

        var items = ReadDesktopItems(hList);
        if (items.Count == 0)
        {
            error = "桌面上没有可整理的图标。";
            return false;
        }

        var creatingBackup = !File.Exists(BackupPath);

        // 关键：通过 Shell 接口可靠关闭“自动排列 / 对齐网格”，否则自定义坐标会被系统吸附回默认网格。
        // 同时保留窗口样式方式作为兜底。读取并记录原始标志，供还原。
        DesktopFolderView.TryClearAutoArrangeAndSnap(out var originalFlags);
        SetAutoArrange(hList, false);

        // 仅在没有备份时才记录原始排布与原始标志，避免重复整理覆盖原始坐标
        if (creatingBackup)
        {
            var backup = new LayoutBackup
            {
                OriginalFolderFlags = originalFlags,
                Items = items.Select(i => new BackupItem { Name = i.Name, X = i.X, Y = i.Y }).ToList()
            };
            SaveBackup(backup);
        }

        // ListView 每改一个图标坐标都会立即重绘。布局从第一列开始写入，
        // 如果让 Explorer 逐项绘制，松手恢复时会看到第一列短暂散开的中间状态。
        // 更新期间暂停绘制，所有坐标写完后再一次性刷新。
        SendMessage(hList, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        try
        {
            ApplyZonedVerticalLayout(hList, items);
        }
        finally
        {
            SendMessage(hList, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            RedrawWindow(hList, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
        }

        _organized = true;
        return true;
    }

    private bool RestoreCore(out string? error)
    {
        error = null;
        var backup = LoadBackup();
        if (backup == null)
        {
            // 没有备份则视为已是原始状态
            _organized = false;
            return true;
        }

        var hList = GetDesktopListView();
        if (hList == IntPtr.Zero)
        {
            error = "未找到桌面图标列表，无法还原。";
            return false;
        }

        var current = ReadDesktopItems(hList);
        var posByName = new Dictionary<string, BackupItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in backup.Items)
            posByName[b.Name] = b;

        foreach (var item in current)
            if (posByName.TryGetValue(item.Name, out var b))
                SetItemPosition(hList, item.Index, b.X, b.Y);

        // 还原原始的“自动排列 / 对齐网格”标志（若原本开启，系统会按原方式重新排布回原样）
        DesktopFolderView.TryRestoreFlags(backup.OriginalFolderFlags);

        TryDeleteBackup();
        _organized = false;
        return true;
    }

    /// <summary>
    /// 按功能分区纵向排列：分区内图标按正常图标间距自上而下排列，一列排满（到达屏幕底部）
    /// 后在“同一分区内”换到下一列（列间为正常图标间距）；不同分区之间使用设置的较大间距。
    /// 未匹配的图标归到最后一组。
    /// </summary>
    private static void ApplyZonedVerticalLayout(IntPtr hList, List<DesktopItem> items)
    {
        var (cx, cy) = GetItemSpacing(hList);
        if (cx <= 0) cx = 75;
        if (cy <= 0) cy = 90;

        // 桌面 ListView 覆盖整个虚拟桌面。多显示器时不能直接使用它的完整客户区，
        // 否则图标可能从副显示器的坐标原点开始排列。将主显示器工作区转换为
        // ListView 客户区坐标，确保所有分区始终落在当前主显示器内。
        var layoutBounds = GetPrimaryWorkAreaInClientCoordinates(hList);
        var layoutHeight = layoutBounds.Bottom - layoutBounds.Top;

        // Explorer 的交互式拖放会把首列 x<16 的坐标强制夹到 x=16。
        // 如果首列从 x=8 开始，夹取时会触发 ListView 的碰撞避让，导致整列被打散。
        const int horizontalMargin = 16;
        const int topMargin = 0;
        var (fromRight, gap, spacingXAdjustment, spacingYAdjustment) = LoadLayoutOptions();
        gap = Math.Max(0, gap);
        cx = Math.Max(48, cx + spacingXAdjustment);
        cy = Math.Max(48, cy + spacingYAdjustment);

        // 一列最多容纳的行数（按屏幕高度），至少 1
        var maxRows = Math.Max(1, (layoutHeight - topMargin) / cy);

        // 按分区顺序分组（ZoneIndex 越小越靠前，未匹配项 ZoneIndex 最大，排在最后）
        var grouped = items
            .GroupBy(i => i.ZoneIndex)
            .OrderBy(g => g.Key)
            .ToList();

        // colX = 当前列左侧 x。左排从左向右推进，右排从右向左推进。
        var colX = fromRight
            ? layoutBounds.Right - horizontalMargin - cx
            : layoutBounds.Left + horizontalMargin;

        foreach (var group in grouped)
        {
            var row = 0;
            foreach (var item in group.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (row >= maxRows)
                {
                    // 同一分区内换列：正常图标间距
                    row = 0;
                    colX = fromRight ? colX - cx : colX + cx;
                }

                var y = layoutBounds.Top + topMargin + row * cy;
                SetItemPosition(hList, item.Index, colX, y);
                row++;
            }

            // 进入下一个分区：从本分区最后一列移开一列，并额外加上分区间距
            colX = fromRight ? colX - (cx + gap) : colX + (cx + gap);
        }
    }

    /// <summary>读取排列方向、分区间距与图标间距调整配置。</summary>
    private static (bool fromRight, int gap, int spacingXAdjustment, int spacingYAdjustment) LoadLayoutOptions()
    {
        try
        {
            var config = ConfigService.Load();
            return (
                config.DesktopOrganizeFromRight,
                config.DesktopZoneGap,
                config.DesktopIconSpacingXAdjustment,
                config.DesktopIconSpacingYAdjustment);
        }
        catch
        {
            return (false, 80, 0, 0);
        }
    }

    #endregion

    #region 桌面项读取与分区匹配

    private sealed class DesktopItem
    {
        public int Index;
        public string Name = "";
        public int X;
        public int Y;
        public int ZoneIndex;
    }

    private readonly struct ItemMeta
    {
        public ItemMeta(string extension, bool isDirectory, bool isGameShortcut)
        {
            Extension = extension;
            IsDirectory = isDirectory;
            IsGameShortcut = isGameShortcut;
        }

        /// <summary>小写扩展名（含点，如 ".docx"）；文件夹或未知为 ""。</summary>
        public string Extension { get; }
        public bool IsDirectory { get; }
        public bool IsGameShortcut { get; }
    }

    private List<DesktopItem> ReadDesktopItems(IntPtr hList)
    {
        var result = new List<DesktopItem>();
        var count = (int)SendMessage(hList, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0) return result;

        GetWindowThreadProcessId(hList, out var pid);
        var hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
        if (hProc == IntPtr.Zero) return result;

        var metaMap = BuildDesktopMetaMap();
        var zones = LoadZones();

        try
        {
            for (var i = 0; i < count; i++)
            {
                var name = ReadItemText(hProc, hList, i);
                if (string.IsNullOrEmpty(name)) continue;
                var (x, y) = ReadItemPosition(hProc, hList, i);
                metaMap.TryGetValue(name, out var meta);
                result.Add(new DesktopItem
                {
                    Index = i,
                    Name = name,
                    X = x,
                    Y = y,
                    ZoneIndex = MatchZone(name, meta, zones)
                });
            }
        }
        finally
        {
            CloseHandle(hProc);
        }

        return result;
    }

    /// <summary>读取配置中的功能分区；为空时使用内置默认分区。</summary>
    private static List<DesktopZone> LoadZones()
    {
        try
        {
            var zones = ConfigService.Load().DesktopZones;
            if (zones is { Count: > 0 })
                return zones;
        }
        catch
        {
            // ignore
        }
        return DesktopZone.CreateDefaults();
    }

    /// <summary>返回首个匹配分区的索引；都不匹配返回 zones.Count（排在最后）。</summary>
    private static int MatchZone(string displayName, ItemMeta meta, List<DesktopZone> zones)
    {
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

        // 其次：名称关键词。软件分类应优先于通用 .lnk/.url 扩展名，
        // 因此即使“游戏”等分区位于列表末尾，也能正确匹配快捷方式名称。
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

        // 游戏协议必须先于通用 .url 扩展名，否则 Steam 游戏会先落入“常用程序与杂项”。
        if (meta.IsGameShortcut)
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
                    && string.Equals(meta.Extension, kw, StringComparison.OrdinalIgnoreCase))
                    return i;
                if (IsFolderKeyword(kw) && meta.IsDirectory)
                    return i;
            }
        }

        // 未命中任何明确规则的项统一进入“常用程序与杂项”，
        // 不再依赖 .exe/.lnk/.url 或逐个程序名称。
        for (var i = 0; i < zones.Count; i++)
        {
            if (string.Equals(zones[i].Name, "常用程序与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zones[i].Name, "常用与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zones[i].Name, "杂项", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return zones.Count;
    }

    /// <summary>名称匹配：容忍是否带扩展名（如 "report.docx" 与桌面显示名 "report" 视为相同）。</summary>
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

    /// <summary>读取真实桌面文件夹内容，建立“显示名（含/不含扩展名）→ 元数据”映射。</summary>
    private static Dictionary<string, ItemMeta> BuildDesktopMetaMap()
    {
        var map = new Dictionary<string, ItemMeta>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in EnumerateDesktopFolders())
        {
            DirectoryInfo dir;
            try { dir = new DirectoryInfo(folder); if (!dir.Exists) continue; }
            catch { continue; }

            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); }
            catch { continue; }

            foreach (var entry in entries)
            {
                ItemMeta meta;
                string fullName;
                try
                {
                    var isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                    var ext = isDir ? "" : (entry.Extension?.ToLowerInvariant() ?? "");
                    meta = new ItemMeta(ext, isDir, !isDir && IsGameShortcut(entry.FullName, ext));
                    fullName = entry.Name;
                }
                catch { continue; }

                AddCandidate(map, fullName, meta);
                var stem = Path.GetFileNameWithoutExtension(fullName);
                if (!string.IsNullOrEmpty(stem))
                    AddCandidate(map, stem, meta);
            }
        }

        return map;
    }

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
        catch
        {
            // 无法读取的快捷方式继续使用名称/扩展名规则。
        }
        return false;
    }

    private static void AddCandidate(Dictionary<string, ItemMeta> map, string key, ItemMeta meta)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        map.TryAdd(key, meta);
    }

    private static IEnumerable<string> EnumerateDesktopFolders()
    {
        var list = new List<string>();
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)); } catch { }
        try { list.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)); } catch { }
        return list.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region 跨进程读取列表项

    private static string ReadItemText(IntPtr hProc, IntPtr hList, int index)
    {
        const int maxChars = 260;
        var textBytes = maxChars * 2;
        var lvItemSize = Marshal.SizeOf<LVITEM>();
        var totalSize = lvItemSize + textBytes;

        var remote = VirtualAllocEx(hProc, IntPtr.Zero, (uint)totalSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero) return "";

        try
        {
            var remoteText = IntPtr.Add(remote, lvItemSize);
            var lvItem = new LVITEM
            {
                iSubItem = 0,
                pszText = remoteText,
                cchTextMax = maxChars
            };

            var local = Marshal.AllocHGlobal(lvItemSize);
            try
            {
                Marshal.StructureToPtr(lvItem, local, false);
                if (!WriteProcessMemory(hProc, remote, local, (uint)lvItemSize, out _))
                    return "";
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }

            SendMessage(hList, LVM_GETITEMTEXTW, (IntPtr)index, remote);

            var buffer = new byte[textBytes];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (!ReadProcessMemory(hProc, remoteText, handle.AddrOfPinnedObject(), (uint)textBytes, out _))
                    return "";
            }
            finally
            {
                handle.Free();
            }

            var text = Encoding.Unicode.GetString(buffer);
            var nul = text.IndexOf('\0');
            return nul >= 0 ? text.Substring(0, nul) : text;
        }
        finally
        {
            VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
        }
    }

    private static (int X, int Y) ReadItemPosition(IntPtr hProc, IntPtr hList, int index)
    {
        var remote = VirtualAllocEx(hProc, IntPtr.Zero, 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero) return (0, 0);

        try
        {
            SendMessage(hList, LVM_GETITEMPOSITION, (IntPtr)index, remote);

            var buffer = new byte[8];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                if (!ReadProcessMemory(hProc, remote, handle.AddrOfPinnedObject(), 8, out _))
                    return (0, 0);
            }
            finally
            {
                handle.Free();
            }

            var x = BitConverter.ToInt32(buffer, 0);
            var y = BitConverter.ToInt32(buffer, 4);
            return (x, y);
        }
        finally
        {
            VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
        }
    }

    private static void SetItemPosition(IntPtr hList, int index, int x, int y)
    {
        var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        SendMessage(hList, LVM_SETITEMPOSITION, (IntPtr)index, lParam);
    }

    /// <summary>
    /// 获取桌面图标网格的单元格尺寸（水平/垂直间距）。
    /// 水平间距使用桌面列表视图的实时网格值；纵向间距使用 Windows WindowMetrics。
    /// Explorer 返回的 ListView 纵向单元格包含额外文本区域，在部分系统上会明显大于
    /// 用户看到的系统默认排列步距，因此不能直接拿它作为图标纵向坐标间隔。
    /// </summary>
    private static (int cx, int cy) GetItemSpacing(IntPtr hList)
    {
        var result = SendMessage(hList, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero).ToInt64();
        var cx = (short)(result & 0xFFFF);
        var listViewCy = (short)((result >> 16) & 0xFFFF);

        if (cx <= 0) cx = 75;
        var cy = GetWindowsVerticalIconSpacing(hList, listViewCy);
        return (cx, cy);
    }

    private static int GetWindowsVerticalIconSpacing(IntPtr hList, int fallback)
    {
        try
        {
            // WindowMetrics 以 twip（1/1440 英寸）保存，Windows 默认值为 -1125，
            // 即 96 DPI 下 75 像素。缺少注册表项代表使用此系统默认值。
            const int defaultTwips = -1125;
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            var raw = key?.GetValue("IconVerticalSpacing")?.ToString();
            var twips = int.TryParse(raw, out var configured) ? configured : defaultTwips;
            var dpi = GetDpiForWindow(hList);
            if (dpi == 0) dpi = 96;
            var systemPixels = (int)Math.Round(Math.Abs(twips) * dpi / 1440.0);

            // LVM_GETITEMSPACING 包含 Explorer 为两行文字预留的完整高度，直接使用会偏松；
            // 仅使用 WindowMetrics 又会在手动定位时偏紧。取两者中值，既保留文字安全区，
            // 又保持接近 Windows 默认桌面的视觉密度。
            var explorerPixels = fallback > 0 ? fallback : systemPixels;
            var subtleExtra = Math.Max(2, (int)Math.Round(dpi / 24.0)); // 96 DPI 时增加 4 px
            var balancedPixels = (int)Math.Round((systemPixels + explorerPixels) / 2.0) + subtleExtra;
            return Math.Max(48, balancedPixels);
        }
        catch
        {
            return fallback > 0 ? fallback : 75;
        }
    }

    private static RECT GetPrimaryWorkAreaInClientCoordinates(IntPtr hList)
    {
        var primary = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (primary != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(primary, ref info))
            {
                var topLeft = new POINT { X = info.rcWork.Left, Y = info.rcWork.Top };
                var bottomRight = new POINT { X = info.rcWork.Right, Y = info.rcWork.Bottom };
                if (ScreenToClient(hList, ref topLeft) && ScreenToClient(hList, ref bottomRight))
                {
                    return new RECT
                    {
                        Left = topLeft.X,
                        Top = topLeft.Y,
                        Right = bottomRight.X,
                        Bottom = bottomRight.Y
                    };
                }
            }
        }

        GetClientRect(hList, out var fallback);
        return fallback;
    }

    private static void SetAutoArrange(IntPtr hList, bool enabled)
    {
        var style = GetWindowLongPtr(hList, GWL_STYLE).ToInt64();
        var newStyle = enabled ? (style | LVS_AUTOARRANGE) : (style & ~LVS_AUTOARRANGE);
        if (newStyle == style) return;

        SetWindowLongPtr(hList, GWL_STYLE, (IntPtr)newStyle);
        SetWindowPos(hList, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    #endregion

    #region 备份读写

    private sealed class LayoutBackup
    {
        /// <summary>旧版本字段（窗口样式自动排列），保留以兼容旧备份。</summary>
        [JsonPropertyName("autoArrange")]
        public bool AutoArrange { get; set; }

        /// <summary>整理前桌面文件夹的“自动排列 / 对齐网格”原始标志位。</summary>
        [JsonPropertyName("originalFolderFlags")]
        public uint OriginalFolderFlags { get; set; }

        [JsonPropertyName("items")]
        public List<BackupItem> Items { get; set; } = new();
    }

    private sealed class BackupItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }
    }

    private static void SaveBackup(LayoutBackup backup)
    {
        try
        {
            var dir = Path.GetDirectoryName(BackupPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(BackupPath, json);
        }
        catch
        {
            // ignore
        }
    }

    private static LayoutBackup? LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            var json = File.ReadAllText(BackupPath);
            return JsonSerializer.Deserialize<LayoutBackup>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteBackup()
    {
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); }
        catch { /* ignore */ }
    }

    #endregion

    #region 桌面列表窗口查找

    private static IntPtr GetDesktopListView()
    {
        // 常规情况：Progman -> SHELLDLL_DefView -> SysListView32
        var progman = FindWindow("Progman", null);
        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        // 启用壁纸幻灯片等情况下，DefView 挂在 WorkerW 下
        if (defView == IntPtr.Zero)
            defView = FindDefViewUnderWorkerW();

        if (defView == IntPtr.Zero) return IntPtr.Zero;
        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    private static IntPtr FindDefViewUnderWorkerW()
    {
        var found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(16);
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                var def = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (def != IntPtr.Zero)
                {
                    found = def;
                    return false; // 停止枚举
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    #endregion

    #region Win32

    private const int GWL_STYLE = -16;
    private const long LVS_AUTOARRANGE = 0x0100;

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const int LVM_SETITEMPOSITION = LVM_FIRST + 15;
    private const int LVM_GETITEMPOSITION = LVM_FIRST + 16;
    private const int LVM_GETITEMSPACING = LVM_FIRST + 51;
    private const int LVM_GETITEMSTATE = LVM_FIRST + 44;
    private const int LVIS_SELECTED = 0x0002;
    private const int LVM_GETITEMTEXTW = LVM_FIRST + 115;
    private const int WM_SETREDRAW = 0x000B;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;

    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct LVITEM
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, uint nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

    #endregion
}
