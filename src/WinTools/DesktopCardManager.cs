using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 管理桌面分区卡片：每个分区一张常驻卡片。
/// 开启后为每个分区建一张卡片；分区增删时同步；快捷键「呼出」时把所有卡片整体抬到前面。
/// </summary>
public sealed class DesktopCardManager : IDisposable
{
    private readonly Dictionary<string, DesktopCardWindow> _cards = new(StringComparer.Ordinal);
    private readonly Action<Window> _registerWindow;
    private bool _enabled;
    private bool _syncing;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _desktopChangeTimer;
    private FileSystemWatcher? _desktopWatcher;

    public DesktopCardManager(Action<Window> registerWindow)
    {
        _registerWindow = registerWindow;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _desktopChangeTimer = _dispatcherQueue.CreateTimer();
        _desktopChangeTimer.Interval = TimeSpan.FromMilliseconds(650);
        _desktopChangeTimer.IsRepeating = false;
        _desktopChangeTimer.Tick += (_, _) => SyncContent();
        StartDesktopWatcher();
    }

    public bool IsEnabled => _enabled;

    /// <summary>卡片侧改动了分区归属（目前只有拖放换区），设置页需要重新载入分区列表。</summary>
    public event EventHandler? ZonesChangedExternally;

    /// <summary>开启 / 关闭桌面卡片。开启时按当前分区建卡片，关闭时全部收起。</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
            Sync();
        else
            HideAll();
    }

    /// <summary>
    /// 启动专用初始化：旧 DeskBox 导入、桌面扫描和物理移动放到后台线程，
    /// 返回 UI 线程后才创建 WinUI 卡片，避免主窗口首帧被文件 I/O 阻塞。
    /// </summary>
    public async Task InitializeAsync()
    {
        _enabled = true;
        if (_syncing) return;
        _syncing = true;
        try
        {
            await Task.Run(() =>
            {
                DesktopCollectService.ImportLegacyDeskBoxItems(out _);
                DesktopCollectService.ReclassifyManagedItems(out _);
                DesktopCollectService.CollectAll(out _);
            });
            if (_enabled)
                SyncCards();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>完全同步：先物理搬入桌面文件，再让卡片集合与分区列表一致。
    /// 适用于用户主动按"立即同步"或首次开启。</summary>
    public void Sync()
    {
        if (!_enabled || _syncing) return;

        _syncing = true;
        try
        {
            // 开启 / 立即同步：成功项目从桌面移入托管分区，失败项目仍留在桌面。
            DesktopCollectService.ImportLegacyDeskBoxItems(out _);
            DesktopCollectService.ReclassifyManagedItems(out _);
            DesktopCollectService.CollectAll(out _);
            SyncCards();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>只同步卡片集合与分区列表，不重新物理搬入。
    /// 适用于 FileSystemWatcher / 卡片内 FileSystemWatcher 触发的"内容变化"事件，
    /// 避免每次桌面文件抖动都跑一次全量 CollectAll。</summary>
    public void SyncContent()
    {
        if (!_enabled || _syncing) return;

        _syncing = true;
        try
        {
            SyncCards();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCards()
    {
        var pendingReveal = new List<DesktopCardWindow>();
        // SettingsService 在 App 启动时只加载一次配置。这里复用内存快照，避免每次
        // FileSystemWatcher 刷新、每创建一张卡片都重新读取并反序列化 config.json。
        var zones = SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>();
        // 空分区不创建窗口，避免默认七张空卡片占满桌面并浪费 WinUI 窗口资源。
        // 同一轮扫描结果直接传给窗口，避免“判断是否为空”和 Bind/Refresh 各扫一次目录。
        var visibleZones = zones
            .Select(z => (Zone: z, Items: DesktopCollectService.ListCardItems(z.Name)))
            .Where(entry => entry.Items.Count > 0)
            .ToList();
        var wanted = new HashSet<string>(visibleZones.Select(entry => entry.Zone.Name), StringComparer.Ordinal);

        foreach (var stale in _cards.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            DisposeCard(stale);
        }

        foreach (var entry in visibleZones)
        {
            var zone = entry.Zone;
            if (_cards.TryGetValue(zone.Name, out var existing))
            {
                existing.RefreshContent(entry.Items);
                if (existing.HasItems)
                    existing.ShowCard();
                continue;
            }

            var card = new DesktopCardWindow();
            ThemeService.Register(card);
            card.ApplyUiStyleSurfaces();
            _registerWindow(card);
            card.ContentChanged += Card_ContentChanged;
            card.ItemMovedIn += Card_ItemMovedIn;
            card.Bind(zone.Name, entry.Items);
            // 先只在后台呈现（仍被 Cloak）。等下面 Layout 排完版、图标加载完再统一淡入。
            if (card.HasItems)
            {
                card.PresentCloaked();
                pendingReveal.Add(card);
            }
            _cards[zone.Name] = card;
        }

        Layout(visibleZones.Select(entry => entry.Zone).ToList());

        // 卡片按顺序错开 40ms 淡入，形成一次整体出现，而不是逐个跳出来。
        for (var i = 0; i < pendingReveal.Count; i++)
        {
            _ = pendingReveal[i].RevealAsync(i * 40);
        }
    }

    /// <summary>彻底释放单张卡片：解订阅 → 资源回收 → 真正关闭窗口。</summary>
    private void DisposeCard(string zoneName)
    {
        if (!_cards.TryGetValue(zoneName, out var card)) return;
        card.ContentChanged -= Card_ContentChanged;
        card.ItemMovedIn -= Card_ItemMovedIn;
        card.Cleanup();
        card.HideCard();
        _cards.Remove(zoneName);
    }

    private void StartDesktopWatcher()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) return;

            _desktopWatcher = new FileSystemWatcher(desktop)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => QueueDesktopSync();
            RenamedEventHandler renamed = (_, _) => QueueDesktopSync();
            _desktopWatcher.Created += changed;
            _desktopWatcher.Changed += changed;
            _desktopWatcher.Renamed += renamed;
        }
        catch (Exception ex)
        {
            // 监视失败时仍可手动同步，但桌面文件新增/删除可能漏更新，记日志便于排错
            ErrorReporter.Log("DesktopCardManager.StartDesktopWatcher", ex);
        }
    }

    private void QueueDesktopSync()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            _desktopChangeTimer.Stop();
            _desktopChangeTimer.Start();
        });
    }

    private void Card_ContentChanged(object? sender, EventArgs e) => Sync();

    /// <summary>
    /// 拖放换区后把归属固化到配置：从所有分区的显式清单里摘掉这个名字，再加进目标分区。
    /// 不这么做的话，下一次 Sync 里的 ReclassifyManagedItems 会按关键词把它挪回原分区。
    /// </summary>
    private void Card_ItemMovedIn(object? sender, CardItemMovedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.ItemName) || string.IsNullOrWhiteSpace(e.TargetZone)) return;

        var zones = SettingsService.Instance.Current.DesktopZones;
        if (zones == null) return;

        var changed = false;
        foreach (var zone in zones)
        {
            if (zone.Items == null) continue;
            var removed = zone.Items.RemoveAll(name =>
                string.Equals(name, e.ItemName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) changed = true;
        }

        var target = zones.FirstOrDefault(z =>
            string.Equals(z.Name, e.TargetZone, StringComparison.Ordinal));
        if (target != null)
        {
            target.Items ??= new List<string>();
            target.Items.Add(e.ItemName);
            changed = true;
        }

        if (!changed) return;
        ConfigService.Update(c => c.DesktopZones = zones);
        // 设置页里的分区列表是配置的副本，不刷新的话用户下一次编辑分区会用旧副本覆盖这次拖放。
        ZonesChangedExternally?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按设置里的水平 / 垂直间距把卡片排成网格：从右上角起自上而下，
    /// 一列排不下就往左新开一列。卡片本身不可拖动，位置完全由这里决定。
    /// 多显示器时按每张卡所在显示器分别排版，避免跨屏把卡片算到错误工作区。
    /// </summary>
    public void Layout(List<DesktopZone>? zones = null)
    {
        if (!_enabled || _cards.Count == 0) return;

        var config = SettingsService.Instance.Current;
        zones ??= config.DesktopZones ?? new List<DesktopZone>();

        var gap = Math.Max(0, config.DesktopCardGap);
        var margin = Math.Max(0, config.DesktopCardMargin);
        App.TraceStartup($"DesktopCardManager.Layout: gap={gap} margin={margin} maxCols={config.DesktopCardMaxColumns}");

        // 取出本轮需要排版的卡片，按其所在显示器分组。多屏拓扑变化时每张卡的
        // GetWorkAreaDip() 都会返回新值，GroupBy 自然按新屏幕重新分桶。
        var zonesToLayout = zones
            .Where(z => _cards.ContainsKey(z.Name))
            .GroupBy(z => GetMonitorKey(z.Name), StringComparer.Ordinal)
            .ToList();

        // 多屏拓扑记忆：cache 只用作"此 topology 是否曾见过"的检测 + SaveCurrentLayout
        // 写入。X/Y 不再信任 cache 里的旧值——margin / maxCols 等
        // 设置用户随时会改，存 X/Y 容易贴不齐右边缘；让默认算法永远按当前 workDip +
        // 当前 margin 重算，**所有 zone 都 PlaceAt 一次**保证贴右。
        CardLayoutCache.GetForCurrentTopology(); // 触发读取（无副作用；保留 API 入口）

        // 默认算法跑所有 zone，贴右排版。
        foreach (var group in zonesToLayout)
            LayoutMonitorGroup(group, gap, margin);

        SaveCurrentLayout();
    }

    private void LayoutMonitorGroup(
        IGrouping<string, DesktopZone> group,
        int gap, int margin)
    {
        if (!_cards.TryGetValue(group.First().Name, out var anchor)) return;
        var workDip = anchor.GetWorkAreaDip();
        var availableHeight = workDip.Height - margin;

        // 列右边缘（DIP，距屏幕左侧），从右往左推进。
        // margin 是左 / 右 / 顶的统一值，三方向共用。
        var columnRight = workDip.X + workDip.Width - margin;
        var leftLimit = workDip.X + margin;
        var y = workDip.Y + margin;
        var columnWidth = 0;
        App.TraceStartup($"  LayoutMonitorGroup: workDip={workDip.X},{workDip.Y},{workDip.Width},{workDip.Height} gap={gap} margin={margin} -> columnRight={columnRight} leftLimit={leftLimit}");

        foreach (var zone in group)
        {
            if (!_cards.TryGetValue(zone.Name, out var card)) continue;

            // 当前列放不下这张卡片 → 换到左边新一列。
            if (y > workDip.Y + margin && y + card.DipHeight > workDip.Y + availableHeight)
            {
                columnRight -= columnWidth + gap;
                y = workDip.Y + margin;
                columnWidth = 0;
            }

            // 左侧留白是布局边界；空间不足时保持最后一列完整可见。
            if (columnRight - card.DipWidth < leftLimit)
                columnRight = leftLimit + card.DipWidth;

            card.PlaceAt(columnRight - card.DipWidth, y);
            columnWidth = Math.Max(columnWidth, card.DipWidth);
            y += card.DipHeight + gap;
        }
    }

    /// <summary>把当前所有活动卡片的位置 / 尺寸写到当前 topology 的缓存条目。
    /// 失败时静默——缓存只是优化，丢失下次重排即可。</summary>
    private void SaveCurrentLayout()
    {
        try
        {
            var snapshot = new Dictionary<string, CardPlacement>(StringComparer.Ordinal);
            foreach (var (zoneName, card) in _cards)
            {
                // 实际窗口位置 = XAML 给的 DIP * 缩放；这里我们存 DIP，让 cache 与
                // DisplayArea.FindAll() 共享同一坐标系；PlaceAt 也是 DIP 入口。
                snapshot[zoneName] = new CardPlacement
                {
                    X = card.DipLeft,
                    Y = card.DipTop,
                    Width = card.DipWidth,
                    Height = card.DipHeight,
                };
            }
            CardLayoutCache.SaveForCurrentTopology(snapshot);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopCardManager.SaveCurrentLayout", ex);
        }
    }

    /// <summary>用工作区坐标作为显示器的稳定 key。同屏卡片共享同一字符串。</summary>
    private string GetMonitorKey(string zoneName)
    {
        if (!_cards.TryGetValue(zoneName, out var card)) return string.Empty;
        var work = card.GetWorkAreaDip();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{work.X},{work.Y},{work.Width},{work.Height}");
    }

    /// <summary>快捷键「呼出」：把所有卡片抬到普通窗口之上（不抢焦点）。若尚未开启则先开启。</summary>
    public void RaiseAll()
    {
        if (!_enabled)
        {
            SetEnabled(true);
            return;
        }

        foreach (var card in _cards.Values)
            card.RaiseToFront();
    }

    public void HideAll()
    {
        foreach (var card in _cards.Values)
            card.HideCard();
    }

    public void Dispose()
    {
        _desktopChangeTimer.Stop();
        try { _desktopWatcher?.Dispose(); _desktopWatcher = null; }
        catch (Exception ex) { ErrorReporter.Log("DesktopCardManager.Dispose.Watcher", ex); }
        foreach (var zone in _cards.Keys.ToList())
            DisposeCard(zone);
        _cards.Clear();
    }
}
