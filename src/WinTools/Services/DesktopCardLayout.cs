using System;
using System.Collections.Generic;

namespace WinTools.Services;

/// <summary>Pure placement calculation. A library plan cannot mutate the desktop plan.</summary>
internal static class DesktopCardLayout
{
    /// <summary>一张卡片的落位：左上角（DIP）与最终高度。Height 小于卡片自然高度时，卡片内部滚动。</summary>
    internal readonly record struct Placement(int X, int Y, int Height);

    /// <summary>限高兜底时卡片最矮能压到多少（约三行图标），再矮就没法用了。</summary>
    internal const int MinCardHeight = 160;

    private const int CapStep = 40;

    /// <summary>严格按顺序排：一列排不下就往屏幕内侧新开一列。</summary>
    internal static List<(int X, int Y)> Calculate(int workX, int workY, int workWidth, int workHeight,
        int margin, int gap, IReadOnlyList<(int Width, int Height)> sizes, bool fromLeft)
    {
        var packed = Pack(workX, workY, workWidth, workHeight, margin, gap, sizes, fromLeft, firstFit: false);
        var result = new List<(int X, int Y)>(sizes.Count);
        foreach (var p in packed.Positions) result.Add(p);
        return result;
    }

    /// <summary>
    /// 放不下就逐级收紧，直到放得下为止（每一级只在上一级放不下时才启用）：
    /// 1. 严格按顺序排（原行为，放得下就一点不变）；
    /// 2. 见缝插针：后面的卡片优先塞进前面某一列底部的空位；
    /// 3. 间距与边距收到 12、再收到 8（不会比设置值更大）；
    /// 4. 卡片限高：把最高的卡片压矮（内部滚动），直到整体放得进屏幕。
    /// 任何一级下，高于一整列的卡片都会被压到一列高。设置值本身不会被改写，空间够了自动恢复。
    /// </summary>
    internal static List<Placement> Fit(int workX, int workY, int workWidth, int workHeight,
        int margin, int gap, IReadOnlyList<(int Width, int Height)> sizes, bool fromLeft)
    {
        var tiers = new List<(int Gap, int Margin)> { (gap, margin) };
        foreach (var limit in new[] { 12, 8 })
        {
            var tier = (Math.Min(gap, limit), Math.Min(margin, limit));
            if (!tiers.Contains(tier)) tiers.Add(tier);
        }

        foreach (var (g, m) in tiers)
        {
            var capped = Cap(sizes, ColumnHeight(workHeight, m), int.MaxValue);
            foreach (var firstFit in new[] { false, true })
            {
                var packed = Pack(workX, workY, workWidth, workHeight, m, g, capped, fromLeft, firstFit);
                if (packed.TotalWidth <= workWidth) return ToPlacements(packed, capped);
            }
        }

        // 间距已经收到最紧还是放不下：给最高的卡片限高，从一整列高度往下一档档试。
        var (tightGap, tightMargin) = tiers[^1];
        var columnHeight = ColumnHeight(workHeight, tightMargin);
        var best = Cap(sizes, columnHeight, columnHeight);
        var bestPacked = Pack(workX, workY, workWidth, workHeight, tightMargin, tightGap, best, fromLeft, firstFit: true);
        for (var cap = columnHeight; cap >= MinCardHeight; cap -= CapStep)
        {
            var candidate = Cap(sizes, columnHeight, cap);
            var packed = Pack(workX, workY, workWidth, workHeight, tightMargin, tightGap, candidate, fromLeft, firstFit: true);
            best = candidate;
            bestPacked = packed;
            if (packed.TotalWidth <= workWidth) break;
        }
        return ToPlacements(bestPacked, best);
    }

    private static int ColumnHeight(int workHeight, int margin) => Math.Max(MinCardHeight, workHeight - margin * 2);

    private static List<(int Width, int Height)> Cap(
        IReadOnlyList<(int Width, int Height)> sizes, int columnHeight, int cap)
    {
        var limit = Math.Min(columnHeight, cap);
        var result = new List<(int Width, int Height)>(sizes.Count);
        foreach (var size in sizes) result.Add((size.Width, Math.Min(size.Height, limit)));
        return result;
    }

    private static List<Placement> ToPlacements(
        (List<(int X, int Y)> Positions, int TotalWidth) packed, IReadOnlyList<(int Width, int Height)> sizes)
    {
        var result = new List<Placement>(sizes.Count);
        for (var i = 0; i < sizes.Count; i++)
            result.Add(new Placement(packed.Positions[i].X, packed.Positions[i].Y, sizes[i].Height));
        return result;
    }

    /// <summary>
    /// 分列：<paramref name="firstFit"/> 为 false 时只看最后一列（原规则）；
    /// 为 true 时从第一列起找第一个还放得下的列。桌面从右往左排列，库从左往右。
    /// 总宽度放不下时位置仍夹在屏幕内（卡片会重叠，但不会跑出屏幕）——调用方靠 TotalWidth 判断。
    /// </summary>
    private static (List<(int X, int Y)> Positions, int TotalWidth) Pack(
        int workX, int workY, int workWidth, int workHeight, int margin, int gap,
        IReadOnlyList<(int Width, int Height)> sizes, bool fromLeft, bool firstFit)
    {
        var limit = workHeight - margin * 2;
        var columnWidth = new List<int>();
        var columnNext = new List<int>();
        var columnOf = new int[sizes.Count];
        var yOf = new int[sizes.Count];

        for (var i = 0; i < sizes.Count; i++)
        {
            var height = sizes[i].Height;
            var target = -1;
            var first = firstFit ? 0 : Math.Max(0, columnWidth.Count - 1);
            for (var c = first; c < columnWidth.Count; c++)
            {
                if (columnNext[c] == 0 || columnNext[c] + height <= limit)
                {
                    target = c;
                    break;
                }
            }
            if (target < 0)
            {
                columnWidth.Add(0);
                columnNext.Add(0);
                target = columnWidth.Count - 1;
            }
            columnOf[i] = target;
            yOf[i] = columnNext[target];
            columnNext[target] += height + gap;
            columnWidth[target] = Math.Max(columnWidth[target], sizes[i].Width);
        }

        var columnStart = new int[columnWidth.Count];
        var offset = 0;
        for (var c = 0; c < columnWidth.Count; c++)
        {
            columnStart[c] = offset;
            offset += columnWidth[c] + gap;
        }
        var totalWidth = columnWidth.Count == 0 ? 0 : offset - gap + margin * 2;

        var positions = new List<(int X, int Y)>(sizes.Count);
        var right = workX + workWidth - margin;
        for (var i = 0; i < sizes.Count; i++)
        {
            var c = columnOf[i];
            var x = fromLeft
                ? workX + margin + columnStart[c]
                : right - columnStart[c] - sizes[i].Width;
            x = fromLeft
                ? Math.Min(x, Math.Max(workX + margin, right - sizes[i].Width))
                : Math.Max(x, workX + margin);
            positions.Add((x, workY + margin + yOf[i]));
        }
        return (positions, totalWidth);
    }
}
