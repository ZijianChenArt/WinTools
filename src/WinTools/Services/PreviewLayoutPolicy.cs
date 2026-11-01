using System;

namespace WinTools.Services;

/// <summary>All sizes are DIP, including chrome. Small screens always override preferred minimums.</summary>
internal static class PreviewLayoutPolicy
{
    internal static (double Width, double Height) Fit(double width, double height, double workWidth, double workHeight,
        double maxWidthRatio = .85, double maxHeightRatio = .82)
    {
        var maxWidth = Math.Max(1, Math.Min(workWidth - 32, workWidth * maxWidthRatio));
        var maxHeight = Math.Max(1, Math.Min(workHeight - 32, workHeight * maxHeightRatio));
        return (Math.Clamp(width, Math.Min(380, maxWidth), maxWidth),
            Math.Clamp(height, Math.Min(180, maxHeight), maxHeight));
    }

    internal static double ListHeight(int rows, double chrome = 44) => chrome + 40 + Math.Clamp(rows, 1, 12) * 32;
}
