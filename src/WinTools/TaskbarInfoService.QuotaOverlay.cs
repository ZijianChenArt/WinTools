using System.Collections.Generic;
using WinTools.Services;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    // 两个独立窗口共用一份额度和图标扫描；额度窗口不启动额外的读取 / 刷新定时器。
    private readonly TaskbarInfoService? _quotaSource;
    private TaskbarInfoService? _quotaOverlay;

    private bool TryPlaceQuota(RECT bar, float scale, out Layout layout, out int x)
    {
        layout = default;
        x = 0;
        var source = _quotaSource!;
        var clusters = source.CurrentClusters(bar);
        // 没有可信图标位置时不盲放额度，以免盖住开始按钮、应用图标或系统托盘。
        if (clusters == null) return false;
        var occupied = new List<(int Left, int Right)>(clusters);
        if (source._visible && !source._floating && source._bounds is { } buttons)
            occupied.Add((buttons.X, buttons.X + buttons.Width));
        for (var level = 0; level <= 1; level++)
        {
            var candidate = BuildLayout(level, scale);
            var preferred = TaskbarStripPlacement.QuotaPreferredLeft(bar.Left, bar.Right, candidate.Width,
                (int)(8 * scale), source._centered, source._visible && source._floating,
                source._visible && source._bounds is { } bounds ? bounds.X + bounds.Width : null);
            if (!TaskbarStripPlacement.TryFind(bar.Left, bar.Right, occupied, candidate.Width,
                    preferred, (int)(8 * scale), out x)) continue;
            layout = candidate;
            return true;
        }
        return false;
    }

    private void CopyQuotaState()
    {
        var source = _quotaSource!;
        _quotaText = source._quotaText;
        _claudeText = source._claudeText;
        _reading = source._reading;
        _readingClaude = source._readingClaude;
    }
}
