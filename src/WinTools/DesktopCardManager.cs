using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly DispatcherQueueTimer _cardChangeTimer;
    private readonly DispatcherQueueTimer _foregroundWatchTimer;
    private FileSystemWatcher? _desktopWatcher;
    private bool _raisedByHotkey;
    private bool _hotkeyAnimating;
    private IntPtr _raisedFromForeground;
    private bool _animateAllCardsOnNextSync;
    private int _enabledTransitionVersion;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    public DesktopCardManager(Action<Window> registerWindow)
    {
        _registerWindow = registerWindow;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _desktopChangeTimer = _dispatcherQueue.CreateTimer();
        _desktopChangeTimer.Interval = TimeSpan.FromMilliseconds(650);
        _desktopChangeTimer.IsRepeating = false;
        // 桌面出现新文件必须走**完整同步**（物理搬入托管分区），只刷卡片内容是搬不动文件的。
        // 收纳有磁盘 I/O，放后台线程，别卡 UI。
        _desktopChangeTimer.Tick += (_, _) => _ = SyncAsync();
        // 托管目录自身的增删只需要刷新卡片内容；而且一次拖放会连着触发
        // Created / Renamed / Deleted 多个事件，必须防抖，否则每个事件都跑一遍全量同步。
        _cardChangeTimer = _dispatcherQueue.CreateTimer();
        _cardChangeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _cardChangeTimer.IsRepeating = false;
        _cardChangeTimer.Tick += (_, _) => SyncContent();
        _foregroundWatchTimer = _dispatcherQueue.CreateTimer();
        // 60ms：GetAsyncKeyState 的"自上次查询以来按下过"标志要靠轮询捕获，
        // 120ms 会漏掉快速点击。
        _foregroundWatchTimer.Interval = TimeSpan.FromMilliseconds(60);
        _foregroundWatchTimer.IsRepeating = true;
        _foregroundWatchTimer.Tick += ForegroundWatchTimer_Tick;
        StartDesktopWatcher();
    }

    public bool IsEnabled => _enabled;

    /// <summary>卡片侧改动了分区归属（目前只有拖放换区），设置页需要重新载入分区列表。</summary>
    public event EventHandler? ZonesChangedExternally;

    /// <summary>开启 / 关闭桌面卡片。开启时按当前分区建卡片，关闭时全部收起。</summary>
    /// <remarks>
    /// 桌面图标的显示 / 隐藏跟着这个开关走：开启分区 = 关掉系统的「显示桌面图标」，
    /// 关闭分区 = 还回去。文件本身自始至终不动（见 <see cref="DesktopCollectService"/>）。
    /// </remarks>
    public void SetEnabled(bool enabled)
    {
        var transitionVersion = ++_enabledTransitionVersion;
        _enabled = enabled;
        DesktopIconVisibilityService.SetHidden(enabled);
        if (enabled)
        {
            // 复用窗口和新建窗口统一加入 pendingReveal：先在透明/Cloak 状态完成同步，
            // 再由 FinalizeInitialLayoutAndRevealAsync 同时播放与快捷键呼出相同的渐显。
            _animateAllCardsOnNextSync = true;
            try { Sync(); }
            finally { _animateAllCardsOnNextSync = false; }
        }
        else
        {
            ClearRaisedState();
            _ = HideAllAnimatedAsync(transitionVersion);
        }
    }

    /// <summary>
    /// 启动专用初始化：旧版数据迁移（唯一还会动文件的地方）放到后台线程，
    /// 返回 UI 线程后才创建 WinUI 卡片，避免主窗口首帧被文件 I/O 阻塞。
    /// </summary>
    public async Task InitializeAsync()
    {
        _enabled = true;
        if (_syncing) return;
        _syncing = true;
        try
        {
            App.TraceStartup("Cards: InitializeAsync 进入");
            Dictionary<string, string>? migratedZones = null;
            var migrated = 0;
            var migrateFailed = 0;
            await Task.Run(() =>
            {
                if (!DesktopCollectService.HasLegacyManagedItems()) return;
                migrated = DesktopCollectService.MigrateLegacyManagedItems(out migrateFailed, out var pins);
                migratedZones = pins;
                App.TraceStartup($"Cards: 旧版数据迁移完成，搬回 {migrated} 项，失败 {migrateFailed} 项");
            });

            // 归属写回配置必须在 UI 线程：SettingsService 的防抖保存挂在 UI 线程的 DispatcherQueue 上。
            // 62 项就写 62 次盘太蠢，所以先全部改到内存里的 zones，最后统一保存一次。
            if (migratedZones is { Count: > 0 })
            {
                foreach (var pair in migratedZones)
                    PinItemToZone(pair.Key, pair.Value, raiseChanged: false, persist: false);
                PersistZones();
                ZonesChangedExternally?.Invoke(this, EventArgs.Empty);
            }

            DesktopIconVisibilityService.SetHidden(true);
            if (_enabled)
                SyncCards();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>重新扫描桌面并让卡片与分区列表一致。用户主动按「立即同步」或首次开启时调用。</summary>
    /// <remarks>
    /// 新机制下**不再有"搬入"这个动作**：桌面项目原地不动，卡片按规则实时分组，
    /// 所以完整同步和内容同步已经是同一件事。保留两个入口只为兼容调用方语义。
    /// </remarks>
    public void Sync()
    {
        if (!_enabled || _syncing) return;

        _syncing = true;
        try
        {
            DesktopIconVisibilityService.SetHidden(true);
            SyncCards();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>和 <see cref="Sync"/> 相同。桌面监视器触发的自动同步走这个版本。</summary>
    /// <remarks>
    /// 以前这里要把 CollectAll 的磁盘 I/O 甩到后台线程，现在只剩目录枚举，
    /// 直接在 UI 线程做即可；<see cref="SyncCards"/> 本来也必须在 UI 线程。
    /// </remarks>
    public Task SyncAsync()
    {
        Sync();
        return Task.CompletedTask;
    }

    /// <summary>只同步卡片集合与分区列表。桌面内容变化时由防抖定时器调用。</summary>
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
        // 桌面**只枚举一次**再分组：以前是每个分区各扫一次自己的托管目录，现在所有分区
        // 共用同一份桌面快照，同一轮扫描结果直接传给窗口，Bind/Refresh 不再各扫一次。
        var grouped = DesktopCollectService.GroupByZone(zones);
        var visibleZones = zones
            .Select(z => (Zone: z, Items: grouped.TryGetValue(z.Name, out var items) ? items : new List<string>()))
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
                {
                    if (_animateAllCardsOnNextSync)
                    {
                        existing.PresentCloaked();
                        pendingReveal.Add(existing);
                    }
                    else
                    {
                        existing.ShowCard();
                    }
                }
                continue;
            }

            var card = new DesktopCardWindow();
            App.TraceStartup($"Cards: 窗口构造完成 [{zone.Name}]");
            ThemeService.Register(card);
            // 只注册为 shell：卡片自己维护显式 MicaController，不能让 UiStyleService
            // 给它设置窗口背景（会抢走合成目标，Mica 直接没了）。
            UiStyleService.RegisterShell(card);
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

        var zonesForLayout = visibleZones.Select(entry => entry.Zone).ToList();
        // 有卡片要走 Finalize 时跳过这一次排版：那边等到 XAML Loaded、拿到真实
        // RasterizationScale 后还会 ApplyScaleAwareSize + Layout 一次，这里算出来的
        // 必然是错的（实测第一次 workDip=3840x2100，第二次才是正确的 3072x1680），
        // 而且卡片此时全被 Cloak，位置根本没人看得见。9 张卡片的 Resize + Move 约 70ms。
        if (pendingReveal.Count == 0)
            Layout(zonesForLayout);

        // 新建 WinUI 窗口刚 Show 时 XamlRoot/DPI 可能仍未就绪。此时用默认 1.0
        // 缩放排版，在高 DPI 多屏环境中会让卡片重叠；手动“同步桌面”之所以能
        // 修好，正是因为第二次布局时缩放值已经可用。首次显示前等待 Loaded，
        // 重新应用尺寸并排版一次，从根源上消除启动后必须手动同步的问题。
        _ = FinalizeInitialLayoutAndRevealAsync(pendingReveal, zonesForLayout);
    }

    private async Task FinalizeInitialLayoutAndRevealAsync(
        List<DesktopCardWindow> pendingReveal,
        List<DesktopZone> zones)
    {
        if (pendingReveal.Count == 0) return;
        App.TraceStartup("Cards: Finalize 进入，等 XAML Loaded");
        await Task.WhenAll(pendingReveal.Select(card => card.WaitForLayoutReadyAsync()));
        App.TraceStartup("Cards: XAML Loaded 就绪，等图标");
        await Task.WhenAll(pendingReveal.Select(card => card.WaitUntilReadyAsync()));
        App.TraceStartup("Cards: 图标就绪");
        foreach (var card in pendingReveal) card.ApplyScaleAwareSize();
        Layout(zones);
        App.TraceStartup("Cards: 二次布局完成，开始揭示");

        // 所有卡片同时揭示：Win11 开窗动画是一次性的，逐张错开会显得像旧版的"逐个加载"。
        await Task.WhenAll(pendingReveal.Select(card => card.RevealAsync()));
        App.TraceStartup("Cards: 全部揭示完成");
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
            _desktopWatcher.Deleted += changed;
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

    private void Card_ContentChanged(object? sender, EventArgs e)
    {
        _cardChangeTimer.Stop();
        _cardChangeTimer.Start();
    }

    /// <summary>拖放换区：把归属固化到配置的显式清单。**不移动任何文件**。</summary>
    private void Card_ItemMovedIn(object? sender, CardItemMovedEventArgs e)
        => PinItemToZone(e.ItemName, e.TargetZone, raiseChanged: true, persist: true);

    /// <summary>
    /// 把某个名字钉到指定分区：先从所有分区的显式清单里摘掉它，再加进目标分区。
    /// 这就是新机制下「分区归属」的唯一存储——文件在磁盘上的位置永远是桌面，不表达归属。
    /// </summary>
    /// <remarks>
    /// 不写显式清单的话，下一次 <see cref="DesktopZoneMatcher"/> 会按关键词把它算回原分区，
    /// 用户的拖放就白做了。
    /// </remarks>
    /// <param name="persist">false = 只改内存里的分区对象，由调用方批量落盘（迁移时 62 项就不用写 62 次）。</param>
    private void PinItemToZone(string itemName, string targetZone, bool raiseChanged, bool persist)
    {
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(targetZone)) return;

        var zones = SettingsService.Instance.Current.DesktopZones;
        if (zones == null) return;

        var changed = false;
        foreach (var zone in zones)
        {
            if (zone.Items == null) continue;
            var removed = zone.Items.RemoveAll(name =>
                string.Equals(name, itemName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) changed = true;
        }

        var target = zones.FirstOrDefault(z =>
            string.Equals(z.Name, targetZone, StringComparison.Ordinal));
        if (target != null)
        {
            target.Items ??= new List<string>();
            target.Items.Add(itemName);
            changed = true;
        }

        if (!changed) return;
        if (persist) PersistZones();
        // 设置页里的分区列表是配置的副本，不刷新的话用户下一次编辑分区会用旧副本覆盖这次拖放。
        if (raiseChanged) ZonesChangedExternally?.Invoke(this, EventArgs.Empty);
    }

    private static void PersistZones()
    {
        var zones = SettingsService.Instance.Current.DesktopZones;
        if (zones == null) return;
        ConfigService.Update(c => c.DesktopZones = zones);
    }

    /// <summary>
    /// 分辨率 / 缩放 / 工作区变化后重新排版：先按新的缩放重算每张卡片的物理尺寸，
    /// 再整体重排一次位置。
    /// </summary>
    /// <remarks>
    /// 单靠 <see cref="Layout"/> 不够。卡片的窗口尺寸是在 <c>ApplyAutoSize</c> 里
    /// 用当时的 <c>RasterizationScale</c> 把 DIP 乘成物理像素后 <c>Resize</c> 上去的，
    /// 缩放改了以后这个像素值就不再对应同样的 DIP——卡片会整体变大或变小，
    /// 而 <see cref="Layout"/> 仍按 <c>DipWidth/DipHeight</c> 算间距，于是出现
    /// 「间距没跟着调整」的观感。必须先 <c>ApplyScaleAwareSize()</c> 再排版。
    /// 调用方（<c>MainWindow.QueueDesktopCardRelayout</c>）已做 700ms 防抖，
    /// 保证这里读到的是系统落定后的缩放值。
    /// </remarks>
    public void RelayoutForDisplayChange()
    {
        if (!_enabled || _cards.Count == 0) return;

        App.TraceStartup($"DesktopCardManager.RelayoutForDisplayChange: cards={_cards.Count}");
        foreach (var card in _cards.Values)
            card.ApplyScaleAwareSize();

        Layout();
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

    /// <summary>
    /// 无论当前是否在桌面，第一次按下都呼到最前，第二次按下降回后台。
    /// </summary>
    public async void RaiseAll()
    {
        if (_hotkeyAnimating) return;
        _hotkeyAnimating = true;
        try
        {
            if (!_enabled)
            {
                SetEnabled(true);
                await AnimateRaiseAllAsync();
                MarkRaisedFromCurrentForeground();
                return;
            }

            if (_raisedByHotkey)
            {
                await AnimateLowerAllAsync();
                ClearRaisedState();
                return;
            }

            var foreground = GetForegroundWindow();
            await AnimateRaiseAllAsync();
            _raisedFromForeground = foreground;
            _raisedByHotkey = true;
            // 先把呼出之前遗留的点击标志读掉，否则第一个 tick 就会把刚呼出的卡片收回去。
            _ = ClickedOutsideCards();
            _foregroundWatchTimer.Start();
        }
        finally
        {
            _hotkeyAnimating = false;
        }
    }

    private void MarkRaisedFromCurrentForeground()
    {
        _raisedFromForeground = GetForegroundWindow();
        _raisedByHotkey = true;
        _ = ClickedOutsideCards();
        _foregroundWatchTimer.Start();
    }

    private async void ForegroundWatchTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_raisedByHotkey || _hotkeyAnimating)
        {
            // 动画进行中也要把点击标志读掉，否则这一次点击会残留到下一个 tick 立刻触发收起。
            _ = ClickedOutsideCards();
            return;
        }

        var clickedAway = ClickedOutsideCards();
        var current = GetForegroundWindow();
        // 只比较前台窗口是不够的：用户呼出卡片后，最常见的动作就是点回**呼出时那个窗口**，
        // 此时前台 HWND 没变，卡片会一直盖在上面收不起来。所以再补一条「点到卡片以外
        // 的任何地方就收起」，两个条件满足其一即收起。
        if (current != IntPtr.Zero && IsOwnCard(current))
        {
            // 用户在卡片上操作（点空白、拖图标）不算"点到别处"，也不能因为前台变成卡片
            // 自己就把整批卡片收回去。
            _raisedFromForeground = current;
            return;
        }

        if (!clickedAway && (current == IntPtr.Zero || current == _raisedFromForeground)) return;

        _hotkeyAnimating = true;
        try
        {
            await AnimateLowerAllAsync();
            ClearRaisedState();
        }
        finally
        {
            _hotkeyAnimating = false;
        }
    }

    private bool IsOwnCard(IntPtr hwnd)
    {
        foreach (var card in _cards.Values)
        {
            if (card.OwnsHandle(hwnd)) return true;
        }
        return false;
    }

    /// <summary>自上次轮询以来用户是否在卡片之外按下过鼠标。读一次即清标志。</summary>
    private bool ClickedOutsideCards()
    {
        try
        {
            var pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0
                || (GetAsyncKeyState(VK_RBUTTON) & 0x0001) != 0;
            if (!pressed) return false;
            if (!GetCursorPos(out var point)) return true;
            foreach (var card in _cards.Values)
            {
                if (card.ContainsScreenPoint(point.X, point.Y)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void ClearRaisedState()
    {
        _raisedByHotkey = false;
        _raisedFromForeground = IntPtr.Zero;
        _foregroundWatchTimer.Stop();
    }

    private async Task AnimateRaiseAllAsync()
    {
        var cards = _cards.Values.ToList();
        await Task.WhenAll(cards.Select(card => card.WaitUntilReadyAsync()));
        await Task.WhenAll(cards.Select(card => card.RaiseAnimatedAsync()));
    }

    private async Task AnimateLowerAllAsync()
    {
        var cards = _cards.Values.ToList();
        await Task.WhenAll(cards.Select(card => card.LowerAnimatedAsync()));
    }

    public void HideAll()
    {
        ++_enabledTransitionVersion;
        ClearRaisedState();
        foreach (var card in _cards.Values)
            card.HideCard();
    }

    private async Task HideAllAnimatedAsync(int transitionVersion)
    {
        var cards = _cards.Values.ToList();
        await Task.WhenAll(cards.Select(card => card.HideAnimatedAsync(
            () => transitionVersion == _enabledTransitionVersion && !_enabled)));
    }

    public void Dispose()
    {
        _desktopChangeTimer.Stop();
        _cardChangeTimer.Stop();
        _foregroundWatchTimer.Stop();
        try { _desktopWatcher?.Dispose(); _desktopWatcher = null; }
        catch (Exception ex) { ErrorReporter.Log("DesktopCardManager.Dispose.Watcher", ex); }
        foreach (var zone in _cards.Keys.ToList())
            DisposeCard(zone);
        _cards.Clear();
    }
}
