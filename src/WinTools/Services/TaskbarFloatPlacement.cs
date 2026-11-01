using System;

namespace WinTools.Services;

/// <summary>Physical screen coordinates; the expanded bar's center and bottom remain stable during hover.</summary>
internal static class TaskbarFloatPlacement
{
    internal static (int CenterX, int BottomY) ClampAnchor(int centerX, int bottomY, int width, int height,
        int left, int top, int right, int bottom)
    {
        var x = (int)Math.Clamp((long)centerX - width / 2, left, Math.Max(left, right - width));
        var y = (int)Math.Clamp((long)bottomY - height, top, Math.Max(top, bottom - height));
        return (x + width / 2, y + height);
    }

    internal static (int X, int Y) Position(int centerX, int bottomY, int width, int height) =>
        (centerX - width / 2, bottomY - height);
}
