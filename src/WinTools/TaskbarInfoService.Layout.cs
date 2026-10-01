using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinTools.Services;

namespace WinTools;

// 布局：先弄清任务栏上应用图标占了哪些位置，再在空隙里选一档放得下的样式。
internal sealed partial class TaskbarInfoService
{
    /// <summary>
    /// 一档布局。Level 越大越省地方：
    /// 0 完整 → 1 额度文字缩成百分比 → 2 同上（音频入口始终只有耳机圆形图标，不再随级别变化）→ 3 去掉 Claude → 4 去掉全部额度 → 5 折叠成一个菜单按钮。
    /// </summary>
    private readonly record struct Layout(int Level, int IconCount, int Audio, bool Menu, bool Compact, int Codex, int Claude, int Width);

    private const int LastLevel = 5;
    private const int PreferredMaxLevel = 3; // 空隙够放额度气泡才算「合适」，否则继续找别的空隙

    private Layout BuildLayout(int level, float scale)
    {
        var button = (int)(36 * scale);
        var pad = (int)(8 * scale);
        var gap = (int)(8 * scale);
        var icons = _iconIds.Length;
        if (level >= LastLevel)
            return icons > 0 || _showAudio ? new Layout(level, 0, 0, true, true, 0, 0, pad + button) : default;

        var audio = _showAudio ? 1 : 0; // 始终是只有耳机图标的圆形按钮，设备名在弹窗里看
        var compact = level >= 1;
        var showCodex = level <= 3;
        // 没连接 Claude（读不到额度）时不占位。
        var showClaude = level <= 2 && !_claudeText.Contains("暂不可用");

        var width = 0;
        if (icons > 0 || audio > 0)
        {
            width = pad + icons * button;
            if (audio > 0) width += (icons > 0 ? (int)(4 * scale) : 0) + (audio == 2 ? (int)(150 * scale) : button);
        }
        var codex = showCodex ? MeasureInfoWidth(compact ? CompactQuota(_quotaText) : _quotaText, scale, compact) : 0;
        var claude = showClaude ? MeasureInfoWidth(compact ? CompactQuota(_claudeText) : _claudeText, scale, compact) : 0;
        if (codex > 0) width += (width > 0 ? gap : 0) + codex;
        if (claude > 0) width += (width > 0 ? gap : 0) + claude;
        return width > 0 ? new Layout(level, icons, audio, false, compact, codex, claude, width) : default;
    }

    /// <summary>在给定宽度内选最完整的一档；连折叠按钮也放不下时返回 default（Width == 0）。</summary>
    private Layout ChooseLayout(int available, float scale)
    {
        for (var level = 0; level <= LastLevel; level++)
        {
            var layout = BuildLayout(level, scale);
            if (layout.Width > 0 && layout.Width <= available) return layout;
        }
        return default;
    }

    private bool TryPlace(RECT bar, float scale, out Layout layout, out int x)
    {
        var margin = (int)(8 * scale);
        var barWidth = bar.Right - bar.Left;
        var clusters = CurrentClusters(bar);
        if (clusters == null)
        {
            // 读不到图标位置（UI Automation 不可用）：沿用「不超过任务栏一半」的保守估计。
            layout = ChooseLayout(Math.Max(1, barWidth / 2 - (int)(64 * scale)), scale);
            x = _centered ? bar.Left + (barWidth - layout.Width) / 2 : bar.Left + margin;
            return layout.Width > 0;
        }

        // 图标簇之间的空隙，从左到右。
        var gaps = new List<(int Left, int Right)>();
        var cursor = bar.Left;
        foreach (var cluster in clusters)
        {
            if (cluster.Left > cursor) gaps.Add((cursor, cluster.Left));
            cursor = Math.Max(cursor, cluster.Right);
        }
        if (bar.Right > cursor) gaps.Add((cursor, bar.Right));

        var found = false;
        layout = default;
        x = 0;
        foreach (var gap in gaps)
        {
            var available = gap.Right - gap.Left - 2 * margin;
            if (available <= 0) continue;
            var candidate = ChooseLayout(available, scale);
            if (candidate.Width <= 0) continue;
            var candidateX = _centered ? gap.Left + (gap.Right - gap.Left - candidate.Width) / 2 : gap.Left + margin;
            if (candidate.Level <= PreferredMaxLevel) { layout = candidate; x = candidateX; return true; }
            if (!found || candidate.Level < layout.Level) { layout = candidate; x = candidateX; found = true; }
        }
        return found;
    }

    /// <summary>把选定的布局换算成各元素的位置，供绘制与点击命中共用。</summary>
    private void Place(Layout layout, float scale)
    {
        _layout = layout;
        _buttonWidth = (int)(36 * scale);
        _buttonStart = (int)(8 * scale);
        var gap = 8 * scale;
        _shownIcons = layout.Menu ? new[] { MenuId } : _iconIds;

        float end = 0;
        if (layout.Menu) end = _buttonStart + _buttonWidth;
        else if (layout.IconCount > 0 || layout.Audio > 0) end = _buttonStart + layout.IconCount * _buttonWidth;

        _deviceWidth = layout.Audio == 2 ? (int)(150 * scale) : layout.Audio == 1 ? _buttonWidth : 0;
        if (_deviceWidth > 0)
        {
            _deviceStart = (int)(end + (layout.IconCount > 0 ? 4 * scale : 0));
            _deviceIconCenter = layout.Audio == 2 ? _deviceStart + 20 * scale : _deviceStart + _deviceWidth / 2f;
            end = _deviceStart + _deviceWidth;
        }
        _codexDrawWidth = layout.Codex;
        _claudeDrawWidth = layout.Claude;
        if (layout.Codex > 0)
        {
            _codexStart = end + (end > 0 ? gap : 0);
            end = _codexStart + layout.Codex;
        }
        if (layout.Claude > 0) _claudeStart = end + (end > 0 ? gap : 0);
    }

    // ---- 读取任务栏上图标的位置（UI Automation，后台线程，只读） ----

    private readonly object _scanLock = new();
    private (int Left, int Right)[]? _clusters;
    private RECT _clusterBar;
    private RECT _lastScanBar;
    private DateTime _nextScan = DateTime.MinValue;
    private volatile bool _scanning;
    private bool _scanFailureLogged;

    private static bool SameBar(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private (int Left, int Right)[]? CurrentClusters(RECT bar)
    {
        lock (_scanLock) return _clusters != null && SameBar(_clusterBar, bar) ? _clusters : null;
    }

    /// <summary>任务栏尺寸变化（分辨率、缩放）立即重扫，否则每 3 秒一次，跟上新开或关闭的应用图标。</summary>
    private void ScheduleScan(IntPtr taskbar, RECT bar, float scale)
    {
        if (_scanning) return;
        var changed = !SameBar(_lastScanBar, bar);
        if (!changed && DateTime.UtcNow < _nextScan) return;
        _scanning = true;
        _lastScanBar = bar;
        _nextScan = DateTime.UtcNow.AddSeconds(3);
        _ = Task.Run(() =>
        {
            try
            {
                var result = ScanTaskbar(taskbar, bar, scale);
                lock (_scanLock) { _clusters = result; _clusterBar = bar; }
            }
            catch (Exception ex)
            {
                lock (_scanLock) _clusters = null;
                if (!_scanFailureLogged) { _scanFailureLogged = true; ErrorReporter.Log("TaskbarInfo.Scan", ex); }
            }
            finally { _scanning = false; }
        });
    }

    private static (int Left, int Right)[]? ScanTaskbar(IntPtr taskbar, RECT bar, float scale)
    {
        IUIAutomation? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? all = null;
        try
        {
            automation = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
            automation.ElementFromHandle(taskbar, out root);
            automation.CreateTrueCondition(out condition);
            root.FindAll(4, condition, out all); // TreeScope_Descendants
            all.get_Length(out var count);
            var barWidth = bar.Right - bar.Left;
            var barHeight = bar.Bottom - bar.Top;
            var minSize = (int)(10 * scale);
            var items = new List<(int Left, int Right)>();
            for (var index = 0; index < count; index++)
            {
                all.GetElement(index, out var element);
                try
                {
                    // 本程序自己的信息条（任务栏的被拥有窗口）也会出现在这棵树里，必须排除，
                    // 否则它会把自己当成「图标」，放到左边就让左边变占用、跳到右边，如此来回横跳。
                    element.GetCurrentPropertyValue(30002, out var pid); // UIA_ProcessIdPropertyId
                    if (pid is int ownerPid && ownerPid == Environment.ProcessId) continue;
                    element.GetCurrentPropertyValue(30001, out var value); // UIA_BoundingRectanglePropertyId
                    if (value is not double[] rect || rect.Length != 4) continue;
                    int left = (int)rect[0], top = (int)rect[1], width = (int)rect[2], height = (int)rect[3];
                    var centerY = top + height / 2;
                    // 只要任务栏这一行里的小元素：忽略整栏容器、屏幕外与不可见的项。
                    if (width < minSize || height < minSize || height > barHeight + 2 || width > barWidth * 0.4) continue;
                    if (centerY < bar.Top || centerY > bar.Bottom || left < bar.Left - 2 || left + width > bar.Right + 2) continue;
                    items.Add((left, left + width));
                }
                finally { Marshal.ReleaseComObject(element); }
            }
            if (items.Count == 0) return null;
            items.Sort((a, b) => a.Left.CompareTo(b.Left));
            var tolerance = (int)(12 * scale); // 相邻图标之间的缝隙不算空隙
            var merged = new List<(int Left, int Right)> { items[0] };
            for (var index = 1; index < items.Count; index++)
            {
                var last = merged[^1];
                if (items[index].Left <= last.Right + tolerance) merged[^1] = (last.Left, Math.Max(last.Right, items[index].Right));
                else merged.Add(items[index]);
            }
            return merged.ToArray();
        }
        finally
        {
            if (all != null) Marshal.ReleaseComObject(all);
            if (condition != null) Marshal.ReleaseComObject(condition);
            if (root != null) Marshal.ReleaseComObject(root);
            if (automation != null) Marshal.ReleaseComObject(automation);
        }
    }

    // 只声明用到的 IUIAutomation 成员；未使用的槽位用占位方法保持 vtable 顺序。
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();      // 3
        void CompareRuntimeIds();    // 4
        void GetRootElement();       // 5
        void ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element); // 6
        void ElementFromPoint();     // 7
        void GetFocusedElement();    // 8
        void GetRootElementBuildCache();       // 9
        void ElementFromHandleBuildCache();    // 10
        void ElementFromPointBuildCache();     // 11
        void GetFocusedElementBuildCache();    // 12
        void CreateTreeWalker();     // 13
        void get_ControlViewWalker();          // 14
        void get_ContentViewWalker();          // 15
        void get_RawViewWalker();    // 16
        void get_RawViewCondition(); // 17
        void get_ControlViewCondition();       // 18
        void get_ContentViewCondition();       // 19
        void CreateCacheRequest();   // 20
        void CreateTrueCondition(out IUIAutomationCondition condition); // 21
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();             // 3
        void GetRuntimeId();         // 4
        void FindFirst();            // 5
        void FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray elements); // 6
        void FindFirstBuildCache();  // 7
        void FindAllBuildCache();    // 8
        void BuildUpdatedCache();    // 9
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value); // 10
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        void get_Length(out int length);                                   // 3
        void GetElement(int index, out IUIAutomationElement element);      // 4
    }
}
