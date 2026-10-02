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
    // The library owns different HWNDs, but borrows the desktop manager's data and watcher.
    private readonly DesktopCardManager? _sourceManager;
    private DesktopCardManager? _libraryManager;
    private Task _revealTask = Task.CompletedTask;
    private Task _libraryPreparation = Task.CompletedTask;
    private bool _preparingHidden;
    private int _contentVersion, _preparedVersion = -1;
    private bool _disposed;
    private bool _enabled;
    private bool _syncing;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly DispatcherQueueTimer _desktopChangeTimer;
    private readonly DispatcherQueueTimer _cardChangeTimer;
    private readonly DispatcherQueueTimer _foregroundWatchTimer;
    private readonly DispatcherQueueTimer _layoutTimer;
    private FileSystemWatcher? _desktopWatcher;
    private bool _raisedByHotkey;
    /// <summary>这次是靠左时当分区库抬起来的：收起也要和分区库一样直接放回，不播动画。</summary>
    private bool _raisedAsLibrary;
    private bool _hotkeyAnimating;
    private IntPtr _raisedFromForeground;
    private bool _animateAllCardsOnNextSync;
    private int _enabledTransitionVersion;
    /// <summary>显示变化重排是否正在进行（重排是分帧的，期间会回到消息泵）。</summary>
    private bool _relayoutRunning;
    /// <summary>重排途中又来了新的显示变化，跑完这遍要再补一遍。</summary>
    private bool _relayoutPendingAgain;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    public DesktopCardManager(Action<Window> registerWindow) : this(registerWindow, null) { }

    private DesktopCardManager(Action<Window> registerWindow, DesktopCardManager? sourceManager)
    {
        _registerWindow = registerWindow;
        _sourceManager = sourceManager;
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
        // 卡片高度自适应：9 张卡片会连着报好几次高度变化，防抖后只排一次。
        _layoutTimer = _dispatcherQueue.CreateTimer();
        _layoutTimer.Interval = TimeSpan.FromMilliseconds(150);
        _layoutTimer.IsRepeating = false;
        _layoutTimer.Tick += (_, _) =>
        {
            try { Layout(); }
            catch (Exception ex) { ErrorReporter.Log("DesktopCardManager.LayoutAfterSizeChange", ex); }
        };
        _foregroundWatchTimer = _dispatcherQueue.CreateTimer();
        // 60ms：GetAsyncKeyState 的"自上次查询以来按下过"标志要靠轮询捕获，
        // 120ms 会漏掉快速点击。
        _foregroundWatchTimer.Interval = TimeSpan.FromMilliseconds(60);
        _foregroundWatchTimer.IsRepeating = true;
        _foregroundWatchTimer.Tick += ForegroundWatchTimer_Tick;
        if (_sourceManager == null) StartDesktopWatcher();
    }

    public bool IsEnabled => _enabled;
    internal bool HasVisibleCards => _enabled || _libraryManager?._enabled == true;

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
        if (_sourceManager == null) DesktopIconVisibilityService.SetHidden(enabled);
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
        if (_disposed || _syncing) return;
        if (!_enabled) { _libraryManager?.Sync(); return; }

        _syncing = true;
        try
        {
            if (_sourceManager == null) DesktopIconVisibilityService.SetHidden(true);
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
        if (_disposed || _syncing) return;
        if (!_enabled) { _libraryManager?.SyncContent(); return; }

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

    private void SyncCards(Dictionary<string, List<string>>? sharedItems = null, int newWindowBudget = int.MaxValue)
    {
        var pendingReveal = new List<DesktopCardWindow>();
        // SettingsService 在 App 启动时只加载一次配置。这里复用内存快照，避免每次
        // FileSystemWatcher 刷新、每创建一张卡片都重新读取并反序列化 config.json。
        var zones = SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>();
        // 空分区不创建窗口，避免默认七张空卡片占满桌面并浪费 WinUI 窗口资源。
        // 桌面**只枚举一次**再分组：以前是每个分区各扫一次自己的托管目录，现在所有分区
        // 共用同一份桌面快照，同一轮扫描结果直接传给窗口，Bind/Refresh 不再各扫一次。
        var grouped = sharedItems ?? DesktopCollectService.GroupByZone(zones);
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
                existing.ItemSource = _sourceManager != null && _sourceManager._cards.TryGetValue(zone.Name, out var sourceCard)
                    ? sourceCard : null;
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
                        if (!_preparingHidden) existing.ShowCard();
                    }
                }
                continue;
            }

            if (newWindowBudget-- <= 0) continue;
            var card = new DesktopCardWindow();
            card.ItemSource = _sourceManager != null && _sourceManager._cards.TryGetValue(zone.Name, out var source)
                ? source : null;
            App.TraceStartup($"Cards: 窗口构造完成 [{zone.Name}]");
            ThemeService.Register(card);
            // 只注册为 shell：卡片自己维护显式 MicaController，不能让 UiStyleService
            // 给它设置窗口背景（会抢走合成目标，Mica 直接没了）。
            UiStyleService.RegisterShell(card);
            card.ApplyUiStyleSurfaces();
            _registerWindow(card);
            card.ContentChanged += Card_ContentChanged;
            card.ItemMovedIn += Card_ItemMovedIn;
            card.ItemSelected += Card_ItemSelected;
            card.SizeChangedByContent += Card_SizeChangedByContent;
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
        _revealTask = FinalizeInitialLayoutAndRevealAsync(pendingReveal, zonesForLayout, _enabledTransitionVersion);
        if (_libraryManager?._enabled == true)
            _libraryManager.SyncCards(grouped);
        if (_sourceManager == null)
        {
            ++_contentVersion;
            if (_libraryPreparation.IsCompleted) _libraryPreparation = PrepareLibraryAsync();
        }
    }

    private async Task FinalizeInitialLayoutAndRevealAsync(
        List<DesktopCardWindow> pendingReveal,
        List<DesktopZone> zones, int transitionVersion)
    {
        if (pendingReveal.Count == 0) return;
        App.TraceStartup("Cards: Finalize 进入，等 XAML Loaded");
        await Task.WhenAll(pendingReveal.Select(card => card.WaitForLayoutReadyAsync()));
        App.TraceStartup("Cards: XAML Loaded 就绪，等图标");
        if (_sourceManager == null)
            await Task.WhenAll(pendingReveal.Select(card => card.WaitUntilReadyAsync()));
        // Closing a library while its icons are loading must not resurrect hidden windows.
        if (_disposed || (!_enabled && !_preparingHidden) || transitionVersion != _enabledTransitionVersion) return;
        pendingReveal = pendingReveal.Where(card =>
            _cards.TryGetValue(card.ZoneName, out var current) && ReferenceEquals(card, current)).ToList();
        if (pendingReveal.Count == 0) return;
        App.TraceStartup("Cards: 图标就绪");
        foreach (var card in pendingReveal) card.ApplyScaleAwareSize();
        Layout(zones);
        App.TraceStartup("Cards: 二次布局完成，开始揭示");

        // 所有卡片同时揭示：Win11 开窗动画是一次性的，逐张错开会显得像旧版的"逐个加载"。
        if (_preparingHidden)
        {
            await Task.WhenAll(pendingReveal.Select(card => card.WaitForLibraryIconsAsync()));
            if (_disposed || transitionVersion != _enabledTransitionVersion) return;
            foreach (var card in pendingReveal) card.PrepareGroupReveal();
            var prepared = new TaskCompletionSource();
            WindowHelper.WhenRendered(pendingReveal[0], () =>
            {
                if (!_disposed && transitionVersion == _enabledTransitionVersion)
                    foreach (var card in pendingReveal) card.MarkLibrarySurfaceReady();
                prepared.TrySetResult();
            });
            await prepared.Task;
        }
        else if (_sourceManager != null)
            await RevealLibraryTogetherAsync(pendingReveal, transitionVersion);
        else
            await Task.WhenAll(pendingReveal.Select(card => card.RevealAsync()));
        App.TraceStartup("Cards: 全部揭示完成");
    }

    /// <summary>彻底释放单张卡片：解订阅 → 资源回收 → 真正关闭窗口。</summary>
    private void DisposeCard(string zoneName)
    {
        if (!_cards.TryGetValue(zoneName, out var card)) return;
        card.ContentChanged -= Card_ContentChanged;
        card.ItemMovedIn -= Card_ItemMovedIn;
        card.ItemSelected -= Card_ItemSelected;
        card.SizeChangedByContent -= Card_SizeChangedByContent;
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

            // 只听增删和改名：卡片只按名字和属性分组，已加载的图标也按路径复用，
            // 文件内容 / 大小变了对卡片没有任何影响。以前带着 Size，桌面上的文件一写盘
            // （下载、导出、Office 自动保存）就每块数据都往 UI 线程投一次事件。
            _desktopWatcher = new FileSystemWatcher(desktop)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler changed = (_, _) => QueueDesktopSync();
            RenamedEventHandler renamed = (_, _) => QueueDesktopSync();
            _desktopWatcher.Created += changed;
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
            if (_disposed) return;
            _desktopChangeTimer.Stop();
            _desktopChangeTimer.Start();
        });
    }

    private void Card_ContentChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_sourceManager != null) { _sourceManager.Card_ContentChanged(sender, e); return; }
        _cardChangeTimer.Stop();
        _cardChangeTimer.Start();
    }

    /// <summary>某张卡片因为行高自适应变高/变矮了：只重排位置。</summary>
    /// <remarks>不能走 <see cref="Card_ContentChanged"/>：那条路会重新刷内容 → 重新测量 →
    /// 又触发一次高度变化，两边互相喂事件就停不下来了。</remarks>
    private void Card_SizeChangedByContent(object? sender, EventArgs e)
    {
        if (_disposed || !_enabled) return;
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    /// <summary>拖放换区：把归属固化到配置的显式清单。**不移动任何文件**。</summary>
    private void Card_ItemMovedIn(object? sender, CardItemMovedEventArgs e)
        => (_sourceManager ?? this).PinItemToZone(e.ItemName, e.TargetZone, raiseChanged: true, persist: true);

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
    public async Task RelayoutForDisplayChangeAsync()
    {
        if (_disposed) return;
        if (_libraryManager?._enabled == true)
            await _libraryManager.RelayoutForDisplayChangeAsync();
        if ((!_enabled && !_preparingHidden) || _cards.Count == 0) return;

        if (_relayoutRunning)
        {
            // 正在重排时又来一条显示变化：不再叠一遍，等这遍跑完补上即可。
            _relayoutPendingAgain = true;
            return;
        }

        _relayoutRunning = true;
        try
        {
            // 系统改分辨率往往分几步落定（先换模式、再换缩放、最后改工作区），而且每张
            // 卡片窗口还要各自消化 WM_DPICHANGED。策略：显示参数一直在变就继续排；
            // 稳定之后**再补一遍收尾**（第二遍几乎全是空操作，见 PlaceAt 的跳过逻辑），
            // 确保不会把过渡态的尺寸永久留在窗口上。最多四遍，防止死循环。
            var settledPasses = 0;
            for (var pass = 0; pass < 4 && settledPasses < 2; pass++)
            {
                _relayoutPendingAgain = false;
                var signature = DisplaySignature();
                App.TraceStartup($"DesktopCardManager.RelayoutForDisplayChange: pass={pass} cards={_cards.Count} sig={signature}");

                // 位置是按 DipWidth/DipHeight 算的，缩放变了得先让每张卡片按新缩放
                // 重算尺寸，否则这一版会按旧尺寸排出来。只算数不碰窗口，很便宜。
                foreach (var card in _cards.Values) card.RefreshDipSizeForScale();

                foreach (var (card, x, y) in BuildLayoutPlan(null))
                {
                    try { card.PlaceAt(x, y); }
                    catch (Exception ex) { ErrorReporter.Log("DesktopCardManager.Relayout.PlaceAt", ex); }

                    // **每张卡片之后必须让出 UI 线程。** 分辨率刚变完的那几秒里，一次
                    // MoveAndResize 要同步走完 DWM 合成 + XAML 重排，实测 9 张 Mica 卡片
                    // 连着做能占住 UI 线程十几秒；消息泵一停，Windows 就判定本进程「未响应」
                    // 并结束它——用户看到的就是改分辨率后程序闪退。
                    await Task.Yield();
                }

                SaveCurrentLayout();

                var settled = !_relayoutPendingAgain && DisplaySignature() == signature;
                settledPasses = settled ? settledPasses + 1 : 0;
                // 稳定后等一下再收尾：让各卡片窗口把 WM_DPICHANGED 处理完。
                if (settled && settledPasses < 2) await Task.Delay(700);
            }
        }
        finally
        {
            _relayoutRunning = false;
        }
    }

    /// <summary>主显示器工作区 + 当前缩放：用来判断显示参数是否已经落定。</summary>
    private string DisplaySignature()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            var scale = _cards.Values.FirstOrDefault()?.CurrentScale ?? 1.0;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{area.X},{area.Y},{area.Width},{area.Height}@{scale:0.###}");
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// 按设置里的水平 / 垂直间距把卡片排成网格：从右上角（或设置为靠左时的左上角）起自上而下，
    /// 一列排不下就往屏幕内侧新开一列。卡片本身不可拖动，位置完全由这里决定。
    /// 多显示器时按每张卡所在显示器分别排版，避免跨屏把卡片算到错误工作区。
    /// </summary>
    public void Layout(List<DesktopZone>? zones = null)
    {
        if (_libraryManager?._enabled == true) _libraryManager.Layout(zones);
        if (!_enabled || _cards.Count == 0) return;

        // 取出本轮需要排版的卡片，按其所在显示器分组。多屏拓扑变化时每张卡的
        // GetWorkAreaDip() 都会返回新值，GroupBy 自然按新屏幕重新分桶。
        foreach (var (card, x, y) in BuildLayoutPlan(zones))
            card.PlaceAt(x, y);

        SaveCurrentLayout();
    }

    /// <summary>算出每张卡片该去哪儿，但**不动窗口**。
    /// 拆出来是为了让显示变化那条路径可以一张一张地应用、中间回到消息泵。</summary>
    private List<(DesktopCardWindow Card, int X, int Y)> BuildLayoutPlan(List<DesktopZone>? zones)
    {
        var plan = new List<(DesktopCardWindow, int, int)>();
        if ((!_enabled && !_preparingHidden) || _cards.Count == 0) return plan;

        var config = SettingsService.Instance.Current;
        zones ??= config.DesktopZones ?? new List<DesktopZone>();
        var gap = Math.Max(0, config.DesktopCardGap);
        var margin = Math.Max(0, config.DesktopCardMargin);

        App.TraceStartup($"DesktopCardManager.BuildLayoutPlan: gap={gap} margin={margin} maxCols={config.DesktopCardMaxColumns}");

        var zonesToLayout = zones
            .Where(z => _cards.ContainsKey(z.Name))
            .GroupBy(z => GetMonitorKey(z.Name), StringComparer.Ordinal)
            .ToList();

        // 多屏拓扑记忆：cache 只用作"此 topology 是否曾见过"的检测 + SaveCurrentLayout
        // 写入。X/Y 不再信任 cache 里的旧值——margin / maxCols 等
        // 设置用户随时会改，按当前 workDip 和 margin 重新计算坐标。

        // 桌面卡片按设置靠左或靠右；独立的分区库始终在左侧。
        var fromLeft = _sourceManager != null || config.DesktopCardAlignment == "left";
        foreach (var group in zonesToLayout)
            LayoutMonitorGroup(group, gap, margin, fromLeft, plan);

        return plan;
    }

    private void LayoutMonitorGroup(
        IGrouping<string, DesktopZone> group,
        int gap, int margin, bool fromLeft,
        List<(DesktopCardWindow Card, int X, int Y)> plan)
    {
        if (!_cards.TryGetValue(group.First().Name, out var anchor)) return;
        var workDip = anchor.GetWorkAreaDip();
        var cards = group.Where(zone => _cards.ContainsKey(zone.Name)).Select(zone => _cards[zone.Name]).ToList();
        // 按自然高度排；放不下时 Fit 会收紧间距、见缝插针，实在不行再给卡片限高（卡片内滚动）。
        var placements = DesktopCardLayout.Fit(workDip.X, workDip.Y, workDip.Width, workDip.Height,
            margin, gap, cards.Select(card => (card.DipWidth, card.NaturalDipHeight)).ToList(), fromLeft);
        for (var index = 0; index < cards.Count; index++)
        {
            var placement = placements[index];
            // 限高只在 PlaceAt 时随位置一起落到窗口上；空间够了 Height 等于自然高度，限高自动撤掉。
            cards[index].SetHeightCap(placement.Height < cards[index].NaturalDipHeight ? placement.Height : int.MaxValue);
            plan.Add((cards[index], placement.X, placement.Y));
        }
    }

    /// <summary>把当前所有活动卡片的位置 / 尺寸写到当前 topology 的缓存条目。
    /// 失败时静默——缓存只是优化，丢失下次重排即可。</summary>
    private void SaveCurrentLayout()
    {
        // A transient library must never overwrite the persistent desktop layout cache.
        if (_sourceManager != null) return;
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

    /// <summary>
    /// 只呼出、不收起：托盘菜单「显示桌面分区」用。与 <see cref="RaiseAll"/> 的区别是
    /// 卡片已经在最前时什么都不做，而不是像快捷键那样第二次按下就降回后台。
    /// </summary>
    public void ShowAll(bool fromLeft = false)
    {
        if (fromLeft && !LibraryReusesDesktopCards) { _ = ShowLibraryAsync(); return; }
        if (_hotkeyAnimating) return;
        if (_enabled && _raisedByHotkey) return;
        if (fromLeft) { RaiseDesktopCardsAsLibrary(); return; }
        RaiseAll();
    }

    internal void ToggleLibrary()
    {
        if (_disposed) return;
        var library = _libraryManager;
        if (library?._enabled == true)
        {
            library._enabled = false;
            ++library._enabledTransitionVersion;
            library._layoutTimer.Stop();
            library.ClearRaisedState();
            library.ClearAllSelections();
            foreach (var card in library._cards.Values) card.ParkLibrary();
            return;
        }
        if (LibraryReusesDesktopCards) { ToggleDesktopCardsAsLibrary(); return; }
        if (library?._hotkeyAnimating == true) return;
        _ = ShowLibraryAsync();
    }

    /// <summary>
    /// 桌面卡片靠左时，分区库那组窗口的位置和桌面卡片完全重合，另建一组纯属重复。
    /// 2026-09-24 交替 A/B 实测（9 个分区、71 项）：省下 9 个 WinUI 窗口、约 22MB 私有内存、
    /// 约 480 个句柄，启动 CPU 少 1.4 秒。这时分区库直接把桌面卡片抬到最前面，
    /// 靠右时才另备一组左侧窗口（换来点击即开，约 0.1 秒，冷建要 2 秒多）。
    /// </summary>
    private bool LibraryReusesDesktopCards => _sourceManager == null && _enabled
        && SettingsService.Instance.Current.DesktopCardAlignment == "left";

    /// <summary>靠左时的分区库入口：和原来的左侧浮层一样直接显示、直接收起，不播放过渡动画。</summary>
    private void ToggleDesktopCardsAsLibrary()
    {
        if (_hotkeyAnimating) return;
        if (_raisedByHotkey) LowerDesktopCardsNow();
        else RaiseDesktopCardsAsLibrary();
    }

    private void RaiseDesktopCardsAsLibrary()
    {
        foreach (var card in _cards.Values) card.RaiseImmediately();
        _raisedAsLibrary = true;
        MarkRaisedFromCurrentForeground();
    }

    private void LowerDesktopCardsNow()
    {
        ClearAllSelections();
        foreach (var card in _cards.Values) card.LowerFromFront();
        ClearRaisedState();
    }

    /// <summary>设置页改了靠左 / 靠右：重排卡片，并按新位置决定分区库要不要另备一组窗口。</summary>
    public void ApplyAlignment()
    {
        Layout();
        if (_disposed || _sourceManager != null || !_enabled) return;
        if (_libraryPreparation.IsCompleted) _libraryPreparation = PrepareLibraryAsync();
    }

    /// <summary>释放预备好但没在显示的分区库窗口。</summary>
    private void ReleaseIdleLibrary()
    {
        var library = _libraryManager;
        if (library == null || library._enabled || library._hotkeyAnimating) return;
        _libraryManager = null;
        library.Dispose();
    }

    private async Task ShowLibraryAsync()
    {
        if (_disposed) return;
        await _libraryPreparation;
        if (_disposed) return;
        var library = _libraryManager ??= new DesktopCardManager(_registerWindow, this);
        if (library._enabled || library._hotkeyAnimating) return;
        var openTimer = System.Diagnostics.Stopwatch.StartNew();
        library._enabled = true;
        ++library._enabledTransitionVersion;
        library._hotkeyAnimating = true;
        library._animateAllCardsOnNextSync = true;
        try
        {
            if (_enabled && library._preparedVersion == _contentVersion && library._cards.Count > 0)
            {
                library.Layout();
                await library.RevealLibraryTogetherAsync(library._cards.Values.ToList(), library._enabledTransitionVersion);
                if (library._disposed || !library._enabled) return;
                library.MarkRaisedFromCurrentForeground();
                ErrorReporter.Log("DesktopLibrary.Open", $"预备窗口直接显示 {openTimer.ElapsedMilliseconds}ms，{library._cards.Count} 张卡片");
                return;
            }
            // The visible desktop already holds the current grouping and decoded icons.
            // Reuse that snapshot instead of enumerating the desktop again on click.
            var snapshot = _enabled && _cards.Count > 0
                ? _cards.ToDictionary(pair => pair.Key, pair => pair.Value.SnapshotPaths(), StringComparer.Ordinal)
                : null;
            library.SyncCards(snapshot);
            var prepareMs = openTimer.ElapsedMilliseconds;
            library._animateAllCardsOnNextSync = false;
            await library._revealTask;
            library._preparedVersion = _contentVersion;
            ErrorReporter.Log("DesktopLibrary.Open", $"准备窗口 {prepareMs}ms，总计 {openTimer.ElapsedMilliseconds}ms，{library._cards.Count} 张卡片，复用桌面数据={snapshot != null}");
            if (!library._disposed && library._enabled && library._cards.Count > 0)
                library.MarkRaisedFromCurrentForeground();
            else if (!library._disposed) library.HideAll();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopLibrary.Show", ex);
            library.HideAll();
        }
        finally
        {
            library._animateAllCardsOnNextSync = false;
            library._hotkeyAnimating = false;
        }
    }

    private async Task PrepareLibraryAsync()
    {
        // Let the visible desktop finish first; build one hidden window per idle slice.
        await Task.Delay(300);
        try
        {
            await _revealTask;
            if (_disposed || !_enabled) return;
            if (LibraryReusesDesktopCards) { ReleaseIdleLibrary(); return; }
            var library = _libraryManager ??= new DesktopCardManager(_registerWindow, this);
            while (!_disposed && _enabled && !LibraryReusesDesktopCards && !library._disposed
                && !library._enabled && library._preparedVersion != _contentVersion)
            {
                var version = _contentVersion;
                var snapshot = _cards.ToDictionary(pair => pair.Key, pair => pair.Value.SnapshotPaths(), StringComparer.Ordinal);
                library._preparingHidden = true;
                try
                {
                    for (var index = 0; index < Math.Max(1, snapshot.Count); index++)
                    {
                        if (_disposed || !_enabled || LibraryReusesDesktopCards || library._disposed
                            || version != _contentVersion) break;
                        library.SyncCards(snapshot, newWindowBudget: 1);
                        await library._revealTask;
                        await Task.Delay(25);
                    }
                    if (!_disposed && _enabled && !LibraryReusesDesktopCards && !library._disposed
                        && version == _contentVersion) library._preparedVersion = version;
                }
                finally { library._preparingHidden = false; }
            }
            // 准备途中改成了靠左：已经建出来的那几个隐藏窗口也用不上了。
            if (LibraryReusesDesktopCards) ReleaseIdleLibrary();
        }
        catch (Exception ex) { ErrorReporter.Log("DesktopLibrary.Prepare", ex); }
    }

    private async Task RevealLibraryTogetherAsync(List<DesktopCardWindow> cards, int version)
    {
        if (cards.Count == 0) return;
        await Task.WhenAll(cards.Select(card => card.WaitForLibraryIconsAsync()));
        if (_disposed || !_enabled || version != _enabledTransitionVersion) return;
        cards = cards.Where(card => _cards.TryGetValue(card.ZoneName, out var current)
            && ReferenceEquals(card, current)).ToList();
        if (cards.Count == 0) return;
        var alreadyRendered = cards.All(card => card.LibrarySurfaceReady);
        foreach (var card in cards) card.PrepareGroupReveal();
        if (alreadyRendered)
        {
            foreach (var card in cards) WindowHelper.SetWindowCloak(card, false);
            return;
        }
        var ready = new TaskCompletionSource();
        WindowHelper.WhenRendered(cards[0], () =>
        {
            try
            {
                if (_disposed || !_enabled || version != _enabledTransitionVersion) return;
                foreach (var card in cards)
                    if (_cards.TryGetValue(card.ZoneName, out var current) && ReferenceEquals(card, current))
                    {
                        card.MarkLibrarySurfaceReady();
                        WindowHelper.SetWindowCloak(card, false);
                    }
            }
            finally { ready.TrySetResult(); }
        });
        await ready.Task;
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
        // Only one group may consume GetAsyncKeyState's click flags at a time.
        if (_libraryManager?._enabled == true) return;
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
        if (current != IntPtr.Zero && IsOwnCardOrPopup(current))
        {
            // 用户在卡片上操作（点空白、拖图标）不算"点到别处"，也不能因为前台变成卡片
            // 自己就把整批卡片收回去。
            _raisedFromForeground = current;
            return;
        }

        if (!clickedAway && (current == IntPtr.Zero || current == _raisedFromForeground)) return;

        if (_raisedAsLibrary) { LowerDesktopCardsNow(); return; }

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

    /// <summary>某张卡片选中了图标：把其余卡片的选中清掉，整批卡片同一时刻只亮一个。</summary>
    private void Card_ItemSelected(object? sender, EventArgs e)
    {
        foreach (var card in _cards.Values)
        {
            if (!ReferenceEquals(card, sender)) card.ClearSelection();
        }
    }

    /// <summary>清掉所有卡片的选中态（收起时调用）。</summary>
    private void ClearAllSelections()
    {
        foreach (var card in _cards.Values) card.ClearSelection();
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
    /// <summary>
    /// 用户这一下是不是"点到卡片以外的地方"了（点到了就收起整批卡片）。
    /// </summary>
    /// <remarks>
    /// 只按"光标是否落在某张卡片的矩形里"判定是不够的，会把好几种明明还在操作卡片的动作
    /// 误判成"点走了"，表现就是卡片刚呼出、手一点就整批消失：
    /// <list type="bullet">
    ///   <item>右键菜单、工具提示都是**独立的顶层窗口**，位置基本都超出卡片矩形。
    ///         按矩形判定的话，一右键、或者点菜单里的任何一项，卡片就收起来了。</item>
    ///   <item>九张卡片之间有 gap（默认 16dip），点在缝里同样落在所有矩形之外。</item>
    /// </list>
    /// 所以改成三层判定：卡片矩形 → 光标下那个窗口是否属于卡片（含它的弹出窗口）→
    /// 是否落在整批卡片的包围盒内。
    /// </remarks>
    private bool ClickedOutsideCards()
    {
        try
        {
            var pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0
                || (GetAsyncKeyState(VK_RBUTTON) & 0x0001) != 0;
            if (!pressed) return false;
            if (!GetCursorPos(out var point)) return true;
            // The library button owns this click; let its release toggle the group once.
            if ((_sourceManager != null || LibraryReusesDesktopCards)
                && (Application.Current as App)?.TaskbarInfo?.IsLibraryButtonAt(point.X, point.Y) == true)
                return false;

            foreach (var card in _cards.Values)
            {
                if (card.ContainsScreenPoint(point.X, point.Y)) return false;
            }

            // 光标正下方那个窗口是卡片自己的弹出窗口（右键菜单 / 提示）→ 不算点走。
            var under = WindowFromPoint(point);
            if (under != IntPtr.Zero && IsOwnCardOrPopup(GetAncestor(under, GA_ROOT))) return false;

            // 落在整批卡片的包围盒里（含卡片之间的缝隙）→ 不算点走。
            if (InCardGroupBounds(point)) return false;

            return true;
        }
        catch { return false; }
    }

    /// <summary>这个窗口是卡片本身，或者是某张卡片弹出来的窗口（右键菜单 / 工具提示）。</summary>
    private bool IsOwnCardOrPopup(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsOwnCard(hwnd)) return true;

        // 弹出窗口的属主链指回卡片；层数给够但要有上限，免得句柄异常时死循环。
        var owner = hwnd;
        for (var depth = 0; depth < 4; depth++)
        {
            owner = GetWindow(owner, GW_OWNER);
            if (owner == IntPtr.Zero) break;
            if (IsOwnCard(owner)) return true;
        }
        return false;
    }

    /// <summary>光标是否落在"所有卡片的最小包围盒"内。卡片之间的缝隙算在里面。</summary>
    private bool InCardGroupBounds(POINT point)
    {
        var has = false;
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (var card in _cards.Values)
        {
            if (!GetWindowRect(card.Handle, out var rect)) continue;
            if (!has)
            {
                left = rect.Left; top = rect.Top; right = rect.Right; bottom = rect.Bottom;
                has = true;
                continue;
            }
            if (rect.Left < left) left = rect.Left;
            if (rect.Top < top) top = rect.Top;
            if (rect.Right > right) right = rect.Right;
            if (rect.Bottom > bottom) bottom = rect.Bottom;
        }
        if (!has) return false;
        return point.X >= left && point.X < right && point.Y >= top && point.Y < bottom;
    }

    private void ClearRaisedState()
    {
        _raisedByHotkey = false;
        _raisedAsLibrary = false;
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
        // 收起时一并清掉选中：下次呼出应该是干净状态，而不是还亮着上次点的那个图标。
        ClearAllSelections();
        var cards = _cards.Values.ToList();
        if (_sourceManager != null)
        {
            _enabled = false;
            ++_enabledTransitionVersion;
            _layoutTimer.Stop();
            foreach (var card in cards) card.ParkLibrary();
        }
        else await Task.WhenAll(cards.Select(card => card.LowerAnimatedAsync()));
    }

    public void HideAll()
    {
        if (_sourceManager != null) _enabled = false;
        _libraryManager?.HideAll();
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
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        ++_enabledTransitionVersion;
        _libraryManager?.Dispose();
        _layoutTimer.Stop();
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
