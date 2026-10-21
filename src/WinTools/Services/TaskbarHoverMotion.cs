using System;

namespace WinTools.Services;

internal static class TaskbarHoverMotion
{
    internal static float ScaleAt(float from, float to, long elapsedMs)
    {
        var progress = Math.Clamp(elapsedMs / 220f, 0, 1);
        return from + (to - from) * (1 - MathF.Pow(1 - progress, 3));
    }

    internal static int Left(int alignedLeft, int screenAnchor, float localAnchor, float anchorWeight, int minLeft, int maxLeft)
    {
        var anchoredLeft = screenAnchor - (int)MathF.Round(localAnchor);
        return Math.Clamp((int)MathF.Round(alignedLeft + (anchoredLeft - alignedLeft) * anchorWeight), minLeft, maxLeft);
    }
}
