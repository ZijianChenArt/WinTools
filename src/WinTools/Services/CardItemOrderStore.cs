using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinTools.Services;

/// <summary>显示顺序独立保存，不改变文件名称或分区匹配规则。</summary>
internal static class CardItemOrderStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinTools", "card-item-order.json");
    private static Dictionary<string, List<string>>? _orders;

    private static Dictionary<string, List<string>> Orders
    {
        get
        {
            if (_orders != null) return _orders;
            try
            {
                var data = File.Exists(StorePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(StorePath))
                    : null;
                _orders = new(data ?? new(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("CardItemOrderStore.Load", ex);
                _orders = new(StringComparer.OrdinalIgnoreCase);
            }
            return _orders;
        }
    }

    public static IReadOnlyList<string> Apply(string zone, IReadOnlyList<string> paths)
    {
        Orders.TryGetValue(zone, out var order);
        order ??= new List<string>();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
            if (order[i] != null) ranks.TryAdd(order[i], i);

        // 一级：系统虚拟图标始终最前；二级：文件夹集中为一组；普通文件再按扩展名分组。
        // 同一格式内部才沿用用户拖动顺序，最后以名称兜底，确保每次刷新结果稳定。
        return paths
            .OrderBy(ItemKindRank)
            .ThenBy(FileFormatKey, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => ranks.GetValueOrDefault(Path.GetFileName(path), int.MaxValue))
            .ThenBy(DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static int ItemKindRank(string path)
    {
        if (DesktopShellItems.IsShellItem(path)) return 0;
        return Directory.Exists(path) ? 1 : 2;
    }

    private static string FileFormatKey(string path)
    {
        if (DesktopShellItems.IsShellItem(path) || Directory.Exists(path)) return string.Empty;
        var extension = Path.GetExtension(path);
        return string.IsNullOrEmpty(extension) ? "\uffff" : extension;
    }

    private static string DisplayName(string path) => DesktopShellItems.IsShellItem(path)
        ? DesktopShellItems.GetDisplayName(path) ?? path
        : Path.GetFileNameWithoutExtension(path);

    /// <summary>项目被重命名后，把顺序表里的旧文件名换成新的。</summary>
    /// <remarks>顺序表按**文件名**记录（不是完整路径）。不换的话重命名等于换了个 key，
    /// 排序时查不到名次，图标会直接掉到同类的末尾。</remarks>
    public static void Rename(string zone, string oldPath, string newPath)
    {
        try
        {
            if (!Orders.TryGetValue(zone, out var order) || order == null) return;
            var oldName = Path.GetFileName(oldPath);
            var newName = Path.GetFileName(newPath);
            var index = order.FindIndex(name => string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            order[index] = newName;
            Save(zone, order);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("CardItemOrderStore.Rename", ex);
        }
    }

    /// <summary>这个分区是否存过自定义顺序（右键菜单据此决定「恢复默认排序」是否可用）。</summary>
    public static bool HasCustomOrder(string zone) =>
        Orders.TryGetValue(zone, out var order) && order is { Count: > 0 };

    /// <summary>丢掉这个分区的自定义顺序，回到默认的分组 + 名称顺序。</summary>
    public static bool Reset(string zone)
    {
        if (!Orders.Remove(zone)) return true;
        return Persist();
    }

    public static bool Save(string zone, IEnumerable<string> paths)
    {
        Orders[zone] = paths.Select(path => Path.GetFileName(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Persist();
    }

    private static bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var temporary = StorePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Orders));
            File.Move(temporary, StorePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("CardItemOrderStore.Save", ex);
            return false;
        }
    }
}
