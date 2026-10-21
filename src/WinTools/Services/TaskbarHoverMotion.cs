using System;

namespace WinTools.Services;

internal static class TaskbarHoverMotion
{
    internal static float ScaleAt(float from, float to, long elapsedMs)
    {
        var progress = Math.Clamp(elapsedMs / 220f, 0, 1);
        return from + (to - from) * (1 - MathF.Pow(1 - progress, 3));
    }

    internal static int Left(int expandedLeft, int expandedWidth, int width, int minLeft, int maxLeft)
    {
        // 以完整尺寸的中点为固定中心，收缩与展开均不读取鼠标位置。
        return Math.Clamp(expandedLeft + (int)MathF.Round((expandedWidth - width) / 2f), minLeft, maxLeft);
    }
}
