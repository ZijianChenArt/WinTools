using System;
using System.Collections.Generic;

namespace WinTools.Services;

/// <summary>Pure placement calculation. A library plan cannot mutate the desktop plan.</summary>
internal static class DesktopCardLayout
{
    internal static List<(int X, int Y)> Calculate(int workX, int workY, int workWidth, int workHeight,
        int margin, int gap, IReadOnlyList<(int Width, int Height)> sizes, bool fromLeft)
    {
        var result = new List<(int X, int Y)>(sizes.Count);
        var columnRight = workX + workWidth - margin;
        var y = workY + margin;
        var columnWidth = 0;
        foreach (var size in sizes)
        {
            if (y > workY + margin && y + size.Height > workY + workHeight - margin)
            {
                columnRight -= columnWidth + gap;
                y = workY + margin;
                columnWidth = 0;
            }
            columnRight = Math.Max(columnRight, workX + margin + size.Width);
            var x = fromLeft ? workX + workWidth - (columnRight - workX) : columnRight - size.Width;
            result.Add((x, y));
            columnWidth = Math.Max(columnWidth, size.Width);
            y += size.Height + gap;
        }
        return result;
    }
}
