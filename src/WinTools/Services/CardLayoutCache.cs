using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;

namespace WinTools.Services;

/// <summary>
/// 一张卡片在屏幕上的绝对位置 + 尺寸（DIP 坐标系）。
/// </summary>
internal sealed class CardPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// 桌面分区卡片的多屏拓扑记忆：
/// <list type="bullet">
///   <item>每个 topology（屏幕组合 + 各自 OuterBounds + IsPrimary + Orientation）
///         对应一份"分区名 → 卡片位置/尺寸"的快照</item>
///   <item>启动时若当前 topology 在缓存里命中，按缓存恢复卡片位置；
///         不命中则按当前 Layout 的结果写入新条目</item>
///   <item>WM_DISPLAYCHANGE 触发后等系统稳定再读最新 topology，更新缓存条目</item>
/// </list>
/// 借鉴 DeskBox `MultiMonitorLayout` 的"为每种屏幕组合分别保存布局"。
/// </summary>
internal static class TopologyKey
{
    /// <summary>从 Windows 主显示器的 DisplayArea 拼出稳定字符串。
    /// 不调 <c>DisplayArea.FindAll()</c>——部分 CsWinRT 版本的 <c>IReadOnlyList&lt;DisplayArea&gt;</c>
    /// projection 在 LINQ <c>ToList()</c> 时会抛 InvalidCastException（与 dispatcher 上下文相关）。
    /// 主显示器 + OuterBounds 已能区分主屏分辨率 / DPI 的常见变化。
    /// 拔插 / DPI 变化后 OuterBounds 必变，自然落到不同条目。</summary>
    public static string Get()
    {
        try
        {
            var area = DisplayArea.Primary;
            var b = area.OuterBounds;
            var sb = new StringBuilder();
            sb.Append("P=").Append(area.IsPrimary ? '1' : '0')
              .Append('|').Append(b.X).Append(',').Append(b.Y)
              .Append(',').Append(b.Width).Append(',').Append(b.Height)
              .Append(';');
            return sb.ToString();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("TopologyKey.Get", ex);
            return "fallback";
        }
    }
}

/// <summary>
/// 卡片布局缓存：&lt;topologyKey, { zoneName: CardPlacement }&gt;。
/// 存到 %LOCALAPPDATA%\WinTools\card-layout-cache.json，进程内互斥。
/// </summary>
internal static class CardLayoutCache
{
    private static readonly object _lock = new();
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinTools", "card-layout-cache.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static Dictionary<string, Dictionary<string, CardPlacement>> LoadAll()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var bytes = File.ReadAllBytes(_path);
            if (bytes.Length == 0) return new();
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CardPlacement>>>(
                bytes, _json);
            return data ?? new();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("CardLayoutCache.LoadAll", ex);
            return new();
        }
    }

    private static void SaveAll(Dictionary<string, Dictionary<string, CardPlacement>> data)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(data, _json);
            // 写到 .tmp 再 atomic rename，避免崩溃时文件半截。
            // 关键：必须用无 BOM UTF-8——System.Text.Json 解析时遇到 BOM (0xEF)
            // 会抛 "'0xEF' is an invalid start of a value"，导致下次启动读不到 cache。
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("CardLayoutCache.SaveAll", ex);
        }
    }

    /// <summary>取出当前 topology 对应的快照；没有就返回空字典（表示无历史）。</summary>
    public static Dictionary<string, CardPlacement> GetForCurrentTopology()
    {
        var key = TopologyKey.Get();
        lock (_lock)
        {
            var all = LoadAll();
            return all.TryGetValue(key, out var p) ? new Dictionary<string, CardPlacement>(p) : new();
        }
    }

    /// <summary>把当前所有活动卡片的位置写入当前 topology 条目。
    /// 调用方传入"分区名 → 卡片位置/尺寸"的快照。</summary>
    public static void SaveForCurrentTopology(Dictionary<string, CardPlacement> placements)
    {
        if (placements == null) return;
        var key = TopologyKey.Get();
        lock (_lock)
        {
            var all = LoadAll();
            all[key] = new Dictionary<string, CardPlacement>(placements);
            SaveAll(all);
        }
    }
}
