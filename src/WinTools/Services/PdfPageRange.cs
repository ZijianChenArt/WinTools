using System;
using System.Collections.Generic;

namespace WinTools.Services;

/// <summary>A half-open index range of pages intersecting a closed vertical viewport.</summary>
internal readonly record struct PdfPageRange(int First, int EndExclusive)
{
    internal int Count => EndExclusive - First;
    internal bool IsEmpty => First == EndExclusive;

    /// <summary>
    /// Finds the intersecting range in O(log n), without scanning preceding pages.
    /// Page tops and bottoms must be increasing, as in the continuous PDF layout;
    /// heights must be nonnegative. A page touching either viewport edge is included.
    /// </summary>
    internal static PdfPageRange Find(IReadOnlyList<double> tops, IReadOnlyList<double> heights,
        double viewportTop, double viewportBottom)
    {
        if (tops.Count != heights.Count) throw new ArgumentException("Page positions and heights must have equal counts.");
        if (tops.Count == 0 || double.IsNaN(viewportTop) || double.IsNaN(viewportBottom)
            || viewportBottom < viewportTop) return default;

        var low = 0;
        var high = tops.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (tops[middle] + heights[middle] < viewportTop) low = middle + 1;
            else high = middle;
        }
        var first = low;

        high = tops.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (tops[middle] <= viewportBottom) low = middle + 1;
            else high = middle;
        }
        return new(first, low);
    }
}
