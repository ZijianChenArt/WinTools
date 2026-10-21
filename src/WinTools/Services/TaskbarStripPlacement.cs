using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTools.Services;

internal static class TaskbarStripPlacement
{
    internal static int QuotaPreferredLeft(int left, int right, int width, int margin,
        bool centered, bool buttonsFloating, int? buttonRight)
    {
        if (!buttonsFloating && buttonRight is int edge) return edge + margin;
        var quotaCentered = buttonsFloating ? !centered : centered;
        return quotaCentered ? left + (right - left - width) / 2 : left + margin;
    }

    // 将系统图标和任务栏内的按钮作为障碍，选择最接近首选位置的完整空隙。
    internal static bool TryFind(int left, int right, IEnumerable<(int Left, int Right)> occupied,
        int width, int preferredLeft, int margin, out int x)
    {
        x = 0;
        if (width <= 0) return false;
        var cursor = left;
        var best = int.MaxValue;
        var found = false;
        foreach (var block in occupied.OrderBy(b => b.Left).Append((Left: right, Right: right)))
        {
            var gapEnd = Math.Clamp(block.Left, left, right) - margin - width;
            var gapStart = cursor + margin;
            if (gapEnd >= gapStart)
            {
                var candidate = Math.Clamp(preferredLeft, gapStart, gapEnd);
                var distance = Math.Abs(candidate - preferredLeft);
                if (distance < best) { x = candidate; best = distance; found = true; }
            }
            cursor = Math.Max(cursor, Math.Clamp(block.Right, left, right));
        }
        return found;
    }
}
