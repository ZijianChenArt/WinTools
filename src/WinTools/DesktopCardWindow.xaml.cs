using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using WinRT;
using WinTools.Services;

namespace WinTools;

/// <summary>卡片里的一项：托管文件夹中的一个真实文件 / 文件夹，带真实 shell 图标。</summary>
/// <summary>拖放导致的分区归属变化：项目名 + 目标分区名。</summary>
public sealed class CardItemMovedEventArgs : EventArgs
{
    public CardItemMovedEventArgs(string itemName, string targetZone)
    {
        ItemName = itemName;
        TargetZone = targetZone;
    }

    public string ItemName { get; }
    public string TargetZone { get; }
}

public sealed class CardItem : INotifyPropertyChanged
{
    private readonly record struct IconLoadResult(string ResolvedPath, byte[]? Bytes);
    private readonly record struct ShortcutIconInfo(string TargetPath, string IconPath, int IconIndex);
    private sealed record IconLoadRequest(string Path, TaskCompletionSource<IconLoadResult> Completion);

    // WScript.Shell 必须运行在 STA，所以图标解析放在固定几条后台 STA 线程上，既不阻塞
    // WinUI 首帧，也不会为几十个快捷方式各创建一条线程。
    private static readonly BlockingCollection<IconLoadRequest> IconLoadQueue = new();

    /// <summary>图标解析工作线程数。这条链路的成本几乎全在 <c>SHGetFileInfo</c>（等 Shell
    /// 回话，不吃 CPU），所以并行度按核心数定没有意义，4 条就够。</summary>
    /// <remarks>2026-09-06 实测 69 个托管项目：单线程 490ms（7.1ms/项），4 条 STA 线程 85ms。
    /// 图标转 PNG 只占 15ms，不是瓶颈，不用管。</remarks>
    private static readonly int IconWorkerCount = Math.Clamp(Environment.ProcessorCount, 1, 4);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string szFileName, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    static CardItem()
    {
        // 多个消费者共用同一个 BlockingCollection：GetConsumingEnumerable 本身支持并发消费，
        // 每个请求各自持有 TaskCompletionSource，完成顺序乱掉也不影响绑定。
        for (var i = 0; i < IconWorkerCount; i++)
        {
            var thread = new Thread(ProcessIconLoadQueue)
            {
                IsBackground = true,
                Name = $"WinTools shell icon loader {i + 1}",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    private BitmapImage? _icon;

    public CardItem(string path)
    {
        Path = path;
        // 系统虚拟图标（此电脑 / 回收站…）的 Path 是 Shell 解析名 ::{CLSID}，
        // 拆不出文件名，显示名要向 Shell 要。
        if (DesktopShellItems.IsShellItem(path))
        {
            IsShellItem = true;
            DisplayName = DesktopShellItems.GetDisplayName(path) ?? "系统项目";
            return;
        }

        DisplayName = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(DisplayName))
            DisplayName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string DisplayName { get; }

    /// <summary>是不是系统虚拟图标（此电脑 / 回收站 / 网络…）。这类项目不能删除，打开方式也不同。</summary>
    public bool IsShellItem { get; }

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>优先读取资源管理器使用的真实图标，失败时再取系统缩略图。</summary>
    public async System.Threading.Tasks.Task LoadIconAsync()
    {
        try
        {
            var extension = System.IO.Path.GetExtension(Path);

            var shellIcon = await LoadShellIconAsync(Path);
            var iconPath = shellIcon.ResolvedPath;
            var iconBytes = shellIcon.Bytes;
            if (iconBytes is { Length: > 0 })
            {
                // 关键：DataWriter 拥有 stream 的所有权，Dispose writer 时会连带 dispose
                // stream。必须在 writer.Dispose 之前调 DetachStream()，否则后续
                // stream.Seek(0) / SetSourceAsync 会抛 ObjectDisposedException
                // （2026-08-31 排查见 %TEMP%\WinTools-error-*.log）。
                var stream = new InMemoryRandomAccessStream();
                try
                {
                    var writer = new DataWriter(stream);
                    writer.WriteBytes(iconBytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                    writer.Dispose();

                    stream.Seek(0);
                    var shellBitmap = new BitmapImage();
                    await shellBitmap.SetSourceAsync(stream);
                    Icon = shellBitmap;
                }
                finally
                {
                    stream.Dispose();
                }
                return;
            }

            StorageItemThumbnail? thumb = null;
            if (Directory.Exists(Path))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(Path);
                thumb = await folder.GetThumbnailAsync(ThumbnailMode.SingleItem, 64);
            }
            else if (File.Exists(Path))
            {
                var file = await StorageFile.GetFileFromPathAsync(Path);
                var preferShellIcon = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".url", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);

                // 快捷方式必须优先取 Shell 列表图标，否则 SingleItem 常返回通用白纸图标。
                thumb = await file.GetThumbnailAsync(
                    preferShellIcon ? ThumbnailMode.ListView : ThumbnailMode.SingleItem, 64);
                if (thumb == null || thumb.Size == 0)
                    thumb = await file.GetThumbnailAsync(
                        preferShellIcon ? ThumbnailMode.SingleItem : ThumbnailMode.ListView, 64);
                if (thumb == null || thumb.Size == 0)
                    thumb = await file.GetThumbnailAsync(ThumbnailMode.DocumentsView, 64);
            }

            if (thumb != null && thumb.Size > 0)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                Icon = bmp;
            }
            else
            {
            }
        }
        catch (Exception ex)
        {
            // 图标加载失败是常见情况（无扩展名关联、Shell 图标读取超时等），
            // 之前 catch 直接吞掉让用户看不到任何线索；现在落盘便于排错。
            ErrorReporter.Log($"CardItem.LoadIconAsync({Path})", ex);
        }
    }

    private static Task<IconLoadResult> LoadShellIconAsync(string path)
    {
        var completion = new TaskCompletionSource<IconLoadResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try { IconLoadQueue.Add(new IconLoadRequest(path, completion)); }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    // 进程内图标缓存：键是路径，值带一份最后写入时间。同一个 .lnk 在一次会话里
    // 会被反复解析（切换分区开关、重建卡片、跨卡片拖放），而 WScript.Shell 解析 +
    // Shell 图标提取是这条链路上最慢的一步，缓存后重复请求直接返回。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedIcon> IconCache
        = new(StringComparer.OrdinalIgnoreCase);

    private const int IconCacheCapacity = 512;

    /// <summary>右键菜单「刷新」：丢掉缓存，下次 <see cref="LoadIconAsync"/> 重新向 Shell 取图标。</summary>
    /// <remarks>缓存按最后写入时间失效，但应用升级后常常只换了 exe 的图标、快捷方式本身没变。</remarks>
    internal static void ForgetCachedIcon(string path) => IconCache.TryRemove(path, out _);

    private readonly record struct CachedIcon(DateTime Stamp, IconLoadResult Result);

    private static DateTime IconStamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static void ProcessIconLoadQueue()
    {
        foreach (var request in IconLoadQueue.GetConsumingEnumerable())
        {
            try
            {
                var stamp = IconStamp(request.Path);
                if (IconCache.TryGetValue(request.Path, out var cached) && cached.Stamp == stamp)
                {
                    request.Completion.TrySetResult(cached.Result);
                    continue;
                }

                // 系统虚拟图标只能通过 PIDL 取图标，走不了下面基于路径的那套。
                if (DesktopShellItems.IsShellItem(request.Path))
                {
                    var handle = DesktopShellItems.GetIconHandle(request.Path);
                    var shellResult = new IconLoadResult(
                        request.Path, handle != IntPtr.Zero ? ConvertIconToPng(handle) : null);
                    IconCache[request.Path] = new CachedIcon(stamp, shellResult);
                    request.Completion.TrySetResult(shellResult);
                    continue;
                }

                var shortcut = ResolveShortcutIcon(request.Path);
                var resolvedPath = !string.IsNullOrWhiteSpace(shortcut.TargetPath)
                    ? shortcut.TargetPath
                    : request.Path;
                var bytes = !string.IsNullOrWhiteSpace(shortcut.IconPath)
                    ? ReadExtractedIconPng(shortcut.IconPath, shortcut.IconIndex)
                    : null;
                bytes ??= ReadShellIconPng(resolvedPath);
                if (bytes == null && !string.Equals(resolvedPath, request.Path, StringComparison.OrdinalIgnoreCase))
                    bytes = ReadShellIconPng(request.Path);
                var result = new IconLoadResult(resolvedPath, bytes);
                if (IconCache.Count >= IconCacheCapacity) IconCache.Clear();
                IconCache[request.Path] = new CachedIcon(stamp, result);
                request.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                request.Completion.TrySetException(ex);
            }
        }
    }

    private static byte[]? ReadShellIconPng(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            path,
            0,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(info.hIcon);
            using var icon = (System.Drawing.Icon)borrowed.Clone();
            using var bitmap = icon.ToBitmap();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>按快捷方式记录的图标索引直接提取资源，支持 exe/dll/ico 以及无扩展名的 Installer 图标。</summary>
    internal static byte[]? ReadExtractedIconPng(string path, int index)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var large = new IntPtr[1];
        var count = ExtractIconEx(path, index, large, null, 1);
        if (count == 0 || large[0] == IntPtr.Zero) return null;
        return ConvertIconToPng(large[0]);
    }

    private static byte[]? ConvertIconToPng(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            using var icon = (System.Drawing.Icon)borrowed.Clone();
            using var bitmap = icon.ToBitmap();
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// 快捷方式移动到托管目录后，SHGetFileInfo 有时只返回 .lnk 的通用白纸图标。
    /// 先解析其目标（或显式图标文件），再按资源管理器相同规则读取目标图标。
    /// </summary>
    private static ShortcutIconInfo ResolveShortcutIcon(string path)
    {
        if (!System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return default;

        object? shortcut = null;
        try
        {
            var shell = GetWScriptShell();
            if (shell == null)
                return default;

            dynamic shellObject = shell;
            shortcut = shellObject.CreateShortcut(path);
            dynamic shortcutObject = shortcut;

            string target = shortcutObject.TargetPath as string ?? string.Empty;
            target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

            string iconLocation = shortcutObject.IconLocation as string ?? string.Empty;
            var iconPath = iconLocation.Trim();
            var iconIndex = 0;
            var separator = iconPath.LastIndexOf(',');
            if (separator >= 0)
            {
                _ = int.TryParse(iconPath[(separator + 1)..].Trim(), out iconIndex);
                iconPath = iconPath[..separator];
            }
            iconPath = Environment.ExpandEnvironmentVariables(iconPath.Trim().Trim('"'));

            return new ShortcutIconInfo(
                File.Exists(target) ? target : string.Empty,
                File.Exists(iconPath) ? iconPath : string.Empty,
                iconIndex);
        }
        catch
        {
            // 缓存的实例可能已经失效（RCW 分离等），丢掉让下一次重建，不要一直用坏的。
            _wscriptShell = null;
            return default;
        }
        finally
        {
            // 某些 WinRT/COM 组合会在 FinalReleaseComObject 时报告 RCW 已分离；
            // 资源释放失败不能反过来让已经解析成功的图标变成空白。
            // 只释放这次创建的 shortcut，shell 实例留给本线程后续请求复用。
            try
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
            }
            catch { }
        }
    }

    /// <summary>本线程复用的 <c>WScript.Shell</c> 实例。</summary>
    /// <remarks>每解析一个快捷方式就新建一个实例，COM 激活的开销比解析本身还大：
    /// 2026-09-06 实测 51 个 .lnk，每项新建 233ms，复用一个实例 78ms。
    /// 实例只在自己的 STA 工作线程上使用（COM 单元规则），线程随进程结束，无需显式释放。</remarks>
    [ThreadStatic] private static object? _wscriptShell;

    private static object? GetWScriptShell()
    {
        if (_wscriptShell != null) return _wscriptShell;
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        _wscriptShell = Activator.CreateInstance(shellType);
        return _wscriptShell;
    }
}

/// <summary>
/// 单个「桌面分区」卡片：常驻桌面、可拖动 / 缩放、只用 Mica 材质。
/// 内容是**桌面上**匹配到本分区的真实文件（见 <see cref="DesktopCollectService.GroupByZone"/>），
/// 显示真实图标，单击启动。文件一直在桌面原路径，卡片只是换个地方显示它们。
/// </summary>
/// <remarks>
/// 卡片自己不监视文件系统：所有卡片共用 <c>DesktopCardManager</c> 的那一个桌面监视器，
/// 由它防抖后统一刷新。以前每张卡片各监视自己的托管目录，一次拖放会触发多轮重复刷新。
/// </remarks>
public sealed partial class DesktopCardWindow : Window, IUiStyleShell
{
    private const int DefaultWidthDip = 260;
    private const int DefaultHeightDip = 240;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    private const long WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    // 卡片格子尺寸（DIP），与 XAML 中 GridView 的 ItemTemplate 一致。
    // GridViewItem 默认容器左右还各有约 2px 外边距，算宽度时要带上，否则最后一列会被挤到下一行。
    private const int TileWidth = 76;
    /// <summary>GridViewItem 默认容器的额外外边距，算宽度时按列补上，否则末列会被挤到下一行。</summary>
    private const int TileSlack = 22;
    private const int TileHeight = 86;
    private const int HeaderHeight = 0;
    private const int GridPadding = 8;
    /// <summary>每行图标上限是 5（用户设置 1-5 都支持；超过 5 自动回退到 5）。</summary>
    private const int MaxColumnsCap = 5;
    // 卡片最小宽度 = 一个图标的自然占位 (TileWidth + 左右 item margin + 左右 grid padding)
    // = 76 + 8 + 16 = 100。单图标卡片不再被 180 强行撑宽。
    private const int MinCardWidthDip = 100;

    // ItemContainerStyle 在 XAML 里把默认 GridViewItem 的 Padding / Margin 全清零，
    // 间距由下面这个常量统一控制，XAML 计算时也用同一个值。
    private const int ItemMarginDip = 4;

    private readonly ObservableCollection<CardItem> _items = new();
    private readonly IntPtr _hwnd;
    private string _zoneName = "";
    private MicaController? _micaController;
    private SystemBackdropConfiguration? _backdropConfiguration;
    private ICompositionSupportsSystemBackdrop? _backdropTarget;
    private bool _micaAttached;
    private bool _hasPresentedFirstFrame;
    private readonly TaskCompletionSource _layoutReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _iconTasks = new();
    // 用字段持有 AppWindow.Closing 订阅，Cleanup 时才能精确解订阅再让 Window 真正关闭。
    private TypedEventHandler<AppWindow, AppWindowClosingEventArgs>? _closingHandler;

    /// <summary>托管目录内容发生变化时通知管理器重新决定卡片是否需要显示。</summary>
    public event EventHandler? ContentChanged;

    /// <summary>当前卡片的自适应尺寸（DIP），由内容项数算出，供管理器排布。</summary>
    public int DipWidth { get; private set; } = MinCardWidthDip;
    /// <summary>卡片实际占用的高度：自然高度，被限高时取限高（超出的图标在卡片内滚动）。</summary>
    public int DipHeight => Math.Min(NaturalDipHeight, _heightCap);
    /// <summary>内容完全展开所需的高度，排版时按它判断放不放得下。</summary>
    public int NaturalDipHeight { get; private set; } = HeaderHeight + TileHeight + GridPadding * 2;
    private int _heightCap = int.MaxValue;
    /// <summary>当前卡片在桌面上的位置（DIP）。PlaceAt 时更新，
    /// 供多屏拓扑记忆写入 cache 时记录。</summary>
    public int DipLeft { get; private set; }
    public int DipTop { get; private set; }

    /// <summary>当前网格的列数 / 行数，缩放变化时要拿它重算尺寸。</summary>
    private int _cols = 1;
    private int _rows = 1;
    /// <summary>已经量到过真实内容高度：之后不再用"每行最坏情况"的估算值覆盖它。</summary>
    private bool _hasMeasuredHeight;
    private bool _panelHooked;

    public DesktopCardWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => QueueItemOrderSave();
        ShellRoot.Loaded += (_, _) => _layoutReady.TrySetResult();
        // 用 PointerPressed 而不是 Tapped：落在 GridView 空白区域的点击会被它内部的
        // ScrollViewer 当成平移手势吃掉，Tapped 根本不会触发（2026-09-18 实测，卡片空白处
        // 点击时处理器一次都没进来）。PointerPressed 在手势识别之前就冒泡，且必须
        // handledEventsToo: true，否则同样收不到。
        ShellRoot.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(CardRoot_PointerPressed), true);
        // 拖放处理器也用 handledEventsToo: true 挂，防止 ListViewBase 哪天把同源拖动的事件
        // 标成已处理后我们收不到——卡片内重排完全依赖在这里接住 Drop（见 ReorderWithinCard）。
        ItemsGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(ItemsGrid_DragOver), true);
        ItemsGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(ItemsGrid_Drop), true);
        // 右键菜单和快捷键都挂在 GridView 上：键盘（菜单键 / Shift+F10）发起的请求从获得焦点的
        // GridViewItem 往上冒泡，挂在模板里的元素上收不到。见 DesktopCardWindow.ItemMenu.cs。
        ItemsGrid.ContextRequested += ItemsGrid_ContextRequested;
        ItemsGrid.PreviewKeyDown += ItemsGrid_PreviewKeyDown;
        ConfigureWindow();
        // 每张桌面分区都是独立 HWND。启动时批量创建窗口，如果未完成首帧就 Show，
        // DWM 会先画出多个黑色/纯色矩形。先 Cloak，布局、Mica 和内容仍可正常合成，
        // 等第二帧完成后再一次性呈现。
        WindowHelper.SetWindowCloak(this, true);
        AppWindow.SetIcon("Assets\\AppIcon.ico");
    }

    public string ZoneName => _zoneName;

    /// <summary>用户把某个项目拖进了本分区（已完成物理移动），由管理器负责写回分区配置。</summary>
    public event EventHandler<CardItemMovedEventArgs>? ItemMovedIn;

    /// <summary>卡片自身高度变了（文件名行数变化导致行高变化）。管理器收到后只需重排位置，
    /// 不要再走一遍内容同步——那会和这里的测量互相触发。</summary>
    public event EventHandler? SizeChangedByContent;

    public bool HasItems => _items.Count > 0;

    /// <summary>等待窗口完成首次 XAML 加载，确保多显示器 DPI 缩放值可用。</summary>
    public async Task WaitForLayoutReadyAsync()
    {
        if (!ShellRoot.IsLoaded)
            await Task.WhenAny(_layoutReady.Task, Task.Delay(1500));
        await Task.Yield();
    }

    /// <summary>按当前显示器的真实缩放重新应用已计算好的 DIP 尺寸。</summary>
    /// <summary>
    /// 卡片排版用的缩放倍数。**以主显示器的有效 DPI 为准**。
    /// </summary>
    /// <remarks>
    /// 三个缩放来源在分辨率刚变完的那一两秒里会互相矛盾，选错了就会把错误尺寸永久写死在
    /// 窗口上（表现：改完分辨率卡片变窄、每行少一个图标）：
    /// <list type="bullet">
    ///   <item><c>XamlRoot.RasterizationScale</c>：要等 WinUI 下一帧才更新，最慢。</item>
    ///   <item><c>GetDpiForWindow</c>：**每个窗口各自**处理完 WM_DPICHANGED 才更新。9 张卡片
    ///         是一张一张排的，中途还要让出消息泵，于是同一轮排版里前几张读到旧缩放、
    ///         后几张读到新缩放，卡片被分成两组按两种尺寸摆（2026-09-17 实测）。</item>
    ///   <item><c>GetDpiForMonitor</c>：系统一应用新设置就是新值，且对所有卡片一致。</item>
    /// </list>
    /// 卡片本来就固定归属主显示器（见 <see cref="GetWorkAreaDip"/>），所以直接取主显示器的
    /// 有效 DPI，既不会前后不一致，也不用等窗口把消息消化完。
    /// </remarks>
    internal double CurrentScale
    {
        get
        {
            try
            {
                var monitor = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
                if (monitor != IntPtr.Zero &&
                    GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                    return dpiX / 96.0;
            }
            catch { /* 退回窗口自己的 DPI */ }
            try
            {
                var dpi = GetDpiForWindow(_hwnd);
                if (dpi > 0) return dpi / 96.0;
            }
            catch { /* 退回 XAML 的缩放 */ }
            return ShellRoot.XamlRoot?.RasterizationScale ?? 1.0;
        }
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    public void ApplyScaleAwareSize()
    {
        try
        {
            var target = TargetWindowRect(DipLeft, DipTop, CurrentScale);
            var size = AppWindow.Size;
            // 尺寸没变就别调 Resize。显示变化期间每一次 SetWindowPos 都要同步走一遍
            // DWM 合成 + XAML 重排，9 张 Mica 卡片叠起来能把 UI 线程堵住好几秒。
            if (size.Width == target.Width && size.Height == target.Height) return;
            AppWindow.Resize(new SizeInt32(target.Width, target.Height));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 把「可见卡片应该占的 DIP 矩形」换算成要下发给窗口的物理像素矩形。
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.Move/Resize</c> 操作的是**窗口矩形**，而 Win11 的窗口矩形比肉眼看到的
    /// 客户区大一圈——那圈透明的拖拽边框实测在 125% 下是左右各 9px、底部 9px（窗口 335x452、
    /// 客户区 317x443）。直接把 DIP 尺寸当窗口尺寸下发，等于内容区被这圈边框吃掉：
    /// 卡片最后一行的选中框底边被裁掉、卡片之间的可见间距也比设置里的值大了一圈。
    /// 所以这里按实测的边框把矩形**外扩**，让客户区正好等于布局算出来的 DIP 矩形。
    /// 拿不到边框（窗口还没显示、GetClientRect 返回 0）时退回旧算法，不影响首帧。
    /// </remarks>
    private RectInt32 TargetWindowRect(int dipX, int dipY, double scale)
    {
        var x = (int)Math.Round(dipX * scale);
        var y = (int)Math.Round(dipY * scale);
        var width = (int)Math.Ceiling(DipWidth * scale);
        var height = (int)Math.Ceiling(DipHeight * scale);

        if (TryGetFrameInsets(out var left, out var top, out var right, out var bottom))
        {
            x -= left;
            y -= top;
            width += left + right;
            height += top + bottom;
        }
        return new RectInt32(x, y, width, height);
    }

    /// <summary>窗口矩形与可见客户区之间那圈不可见边框（物理像素）。</summary>
    private bool TryGetFrameInsets(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;
        try
        {
            if (!GetWindowRect(_hwnd, out var window)) return false;
            if (!GetClientRect(_hwnd, out var client)) return false;
            var clientWidth = client.Right - client.Left;
            var clientHeight = client.Bottom - client.Top;
            if (clientWidth <= 0 || clientHeight <= 0) return false;

            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(_hwnd, ref origin)) return false;

            left = origin.X - window.Left;
            top = origin.Y - window.Top;
            right = (window.Right - window.Left) - clientWidth - left;
            bottom = (window.Bottom - window.Top) - clientHeight - top;

            // 负数只可能是句柄或时序异常，按"没有边框"处理，别把窗口算成负尺寸。
            if (left < 0 || top < 0 || right < 0 || bottom < 0)
            {
                left = top = right = bottom = 0;
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                // 尺寸由内容自适应、位置由设置里的间距决定，均不允许手动改。
                p.IsResizable = false;
                p.IsMinimizable = false;
                p.IsMaximizable = false;
                p.IsAlwaysOnTop = false;
            }

            // 卡片没有标题栏元素，也自己维护显式 MicaController（见 7.4），
            // 所以走 Chromeless 这条最短路径，不要用 ConfigurePopupTitleBar。
            WindowHelper.ConfigureChromelessWindow(this);
            WindowHelper.DisableWindowTransitions(this);
            // 去掉 DWM 的 1px 边框，否则会与卡片自身描边形成「双层框」。
            WindowHelper.RemoveSystemWindowBorder(this);
            AppWindow.IsShownInSwitchers = false;
            // IsShownInSwitchers 只在 Shell 层面挡住 Alt-Tab / 任务栏，**不改扩展样式**
            // （实测卡片的 ex style 仍是 0x00000100，只有 WS_EX_WINDOWEDGE）。凡是按
            // EnumWindows 找「应用窗口」的系统组件照样把每张卡片当成独立应用，关机界面
            // 因此会列出 N 行同名的「桌面卡片」。降级为工具窗口并清空标题即可避开。
            MarkAsToolWindow();
            Title = string.Empty;
            ApplyUiStyleSurfaces();

            // 卡片不真正关闭，只隐藏；由管理器在 Cleanup() 里解订阅本 handler
            // 并调用 Close()，让窗口走真正的销毁路径，避免反复开关分区时
            // 卡片窗口长期滞留在 WinUI 的窗口列表里。
            _closingHandler = (_, e) =>
            {
                // 进程正在退出时必须放行：否则关机界面的「结束任务」和不带 /F 的
                // taskkill 都关不掉卡片，只有管理器 Cleanup() 那一条路能关。
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                AppWindow.Hide();
            };
            AppWindow.Closing += _closingHandler;
        }
        catch { /* 配置失败不影响主流程 */ }
    }

    /// <summary>绑定到某个分区：读取其托管文件夹内容（尺寸随之自适应）。位置由管理器排布。</summary>
    internal DesktopCardWindow? ItemSource { get; set; }
    internal List<string> SnapshotPaths() => _items.Select(item => item.Path).ToList();

    public void Bind(string zoneName, System.Collections.Generic.IReadOnlyList<string>? initialItems = null)
    {
        _zoneName = zoneName;
        RefreshContent(initialItems);
    }

    /// <summary>本卡片所在显示器的工作区（换算为 DIP），供管理器排布。</summary>
    public RectInt32 GetWorkAreaDip()
    {
        try
        {
            var scale = CurrentScale;
            // 分区卡片固定归属于 Windows 设置中的主显示器。新建 WinUI 窗口的
            // 初始位置并不稳定，多屏环境下可能先落到副屏；若按窗口位置取
            // DisplayArea，随后布局就会错误地把所有卡片留在副屏。
            var a = DisplayArea.Primary.WorkArea;
            var dip = new RectInt32(
                (int)(a.X / scale), (int)(a.Y / scale),
                (int)(a.Width / scale), (int)(a.Height / scale));
            return dip;
        }
        catch (Exception ex)
        {
            // 取不到主显示器工作区就退回一个安全默认值，但要留痕：这会让所有卡片挤到
            // 左上角 1280x720 的假想屏幕里，不记日志根本查不出来。
            Services.ErrorReporter.Log("DesktopCard.GetWorkAreaDip", ex);
            return new RectInt32(0, 0, 1280, 720);
        }
    }

    /// <summary>由管理器调用：把卡片放到算好的位置（DIP）。</summary>
    public void PlaceAt(int dipX, int dipY)
    {
        DipLeft = dipX;
        DipTop = dipY;
        try
        {
            var target = TargetWindowRect(dipX, dipY, CurrentScale);
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            // 完全没变就什么都不做：分辨率来回切时大部分卡片的目标位置是一样的，
            // 省下来的每一次 SetWindowPos 都是 UI 线程上实打实的同步合成开销。
            if (pos.X == target.X && pos.Y == target.Y &&
                size.Width == target.Width && size.Height == target.Height) return;
            // 位置和尺寸合成一次调用：9 张卡片原本要走 18 次 SetWindowPos，这里减半。
            AppWindow.MoveAndResize(target);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 按当前行列数重算卡片的 DIP 尺寸，使图标网格**四边留白相等**（都等于
    /// <see cref="GridPadding"/>）。
    /// </summary>
    /// <remarks>
    /// 两个曾经算错的地方：
    /// <list type="bullet">
    ///   <item>宽度上原来按每列多给 <c>ItemMarginDip * 2</c> 的容器外边距，可
    ///         <c>ItemContainerStyle</c> 已经把 GridViewItem 的 Margin/Padding 清零，
    ///         容器实测就是 76dip（2026-09-18 量到 panel 宽正好 = 列数 × 76）。多出来的
    ///         24dip 因为面板是左对齐的，全堆在右边——左留白 8、右留白 32，肉眼很明显。</item>
    ///   <item>高度要按**物理像素**算。WinUI 把每个容器的高度向上取整到整像素
    ///         （86dip@125% = 107.5px → 实占 108px = 86.4dip），按 86dip/行 算就会越算越少，
    ///         最后一行被裁掉。所以先把一行折成像素再取整回 DIP。</item>
    /// </list>
    /// 缩放变了行高像素也会变，所以显示变化时要重新调一次（见
    /// <see cref="RefreshDipSizeForScale"/>）。
    /// </remarks>
    private void RecomputeDipSize()
    {
        DipWidth = _cols * TileWidth + GridPadding * 2;

        // 高度以**实测**为准（见 ApplyMeasuredContentHeight）：每一行的高度取决于这一行里
        // 有没有两行的文件名，算不出来。测到之前先按"每行都是最坏情况"给个初值，
        // 免得卡片第一帧是个空壳再跳一下。
        if (_hasMeasuredHeight) return;

        var scale = CurrentScale;
        if (scale <= 0) scale = 1.0;
        var rowDip = Math.Ceiling(TileHeight * scale) / scale;
        NaturalDipHeight = HeaderHeight + (int)Math.Ceiling(_rows * rowDip) + GridPadding * 2;
    }

    /// <summary>
    /// 拿图标面板量出来的真实高度当卡片高度。
    /// </summary>
    /// <remarks>
    /// 行高是自适应的（<see cref="CardTilesPanel"/> 按每行最高的格子决定行高），所以卡片高度
    /// 只能等布局跑完再读。读到以后要通知管理器重排——这张卡片矮了/高了，同一列下面的卡片
    /// 都得跟着挪。
    /// </remarks>
    private void ApplyMeasuredContentHeight(double contentHeightDip)
    {
        if (contentHeightDip <= 0) return;
        var wanted = HeaderHeight + (int)Math.Ceiling(contentHeightDip) + GridPadding * 2;
        _hasMeasuredHeight = true;
        if (wanted == NaturalDipHeight) return;

        NaturalDipHeight = wanted;
        ApplyScaleAwareSize();
        SizeChangedByContent?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刷新内容后把图标面板的 SizeChanged 接上，只接一次。</summary>
    private void HookContentPanel()
    {
        if (_panelHooked) return;
        if (ItemsGrid.ItemsPanelRoot is not FrameworkElement panel) return;

        panel.SizeChanged += (_, args) => ApplyMeasuredContentHeight(args.NewSize.Height);
        _panelHooked = true;
        ApplyMeasuredContentHeight(panel.ActualHeight);
    }

    /// <summary>
    /// 排版算出的限高（屏幕放不下时把卡片压矮，超出的图标在卡片内滚动）；
    /// <see cref="int.MaxValue"/> 表示不限。只记数不动窗口，由随后的 <see cref="PlaceAt"/> 一并应用。
    /// </summary>
    public void SetHeightCap(int cap) => _heightCap = cap < 1 ? int.MaxValue : cap;

    /// <summary>缩放变化后重算 DIP 尺寸。管理器排版前调用——位置是按 DipWidth/DipHeight 算的，
    /// 顺序反了会用旧尺寸排出错位的一版。</summary>
    public void RefreshDipSizeForScale() => RecomputeDipSize();

    /// <summary>按当前项数重算卡片尺寸并应用到窗口。
    /// 卡片**宽度固定**为 <see cref="Config.DesktopCardMaxColumns"/> 列的占位（不按
    /// itemCount 收缩）——1 个图标的分区也占满整张卡片宽，单图标靠左。行数按 itemCount 算。
    /// 高度 = 内容所需（rows * TileHeight），不预留滚动空间——卡片完全自适应。</summary>
    private void ApplyAutoSize(int itemCount)
    {
        var wantedCols = SettingsService.Instance.Current.DesktopCardMaxColumns;
        if (wantedCols < 1) wantedCols = 1;
        if (wantedCols > MaxColumnsCap) wantedCols = MaxColumnsCap;
        // 固定宽度：cols = wantedCols，不随 itemCount 收缩
        var cols = wantedCols;
        var rows = itemCount == 0 ? 1 : (itemCount + cols - 1) / cols;

        _cols = cols;
        _rows = rows;
        RecomputeDipSize();
        ApplyScaleAwareSize();
    }

    public void ShowCard()
    {
        try
        {
            AppWindow.Show();
            if (_hasPresentedFirstFrame) return;

            _hasPresentedFirstFrame = true;
            WindowHelper.UncloakWhenRendered(this);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 让窗口进入"已显示但仍被 Cloak"的状态：布局、Mica、图标都在后台合成，屏幕上看不到任何东西。
    /// 启动时先对所有卡片做这一步，排好版再统一 RevealAsync，避免用户看到一批黑方块逐个变成真实卡片。
    /// </summary>
    public void PresentCloaked()
    {
        try
        {
            if (_hasPresentedFirstFrame)
            {
                // 已经创建过、随后因关闭分区功能而隐藏的卡片，也必须先变为全透明再 Show。
                // 否则重新开启时会先闪出完整卡片，之后才开始淡入。
                PrepareFadeIn();
                AppWindow.Show();
                return;
            }

            WindowHelper.SetWindowCloak(this, true);
            ShellRoot.Opacity = 0;
            AppWindow.Show();
        }
        catch { /* ignore */ }
    }

    /// <summary>后台等待图标资源完成，单个慢速 Shell 图标最多阻塞指定时间。</summary>
    public async Task WaitUntilReadyAsync(int timeoutMs = 1500)
    {
        try
        {
            var pending = _iconTasks.Where(t => !t.IsCompleted).ToArray();
            if (pending.Length > 0)
                await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeoutMs));
        }
        catch { /* 图标失败不影响呈现 */ }
    }

    /// <summary>等图标加载完再解除 Cloak，并以 Win11 风格出现。</summary>
    public async Task RevealAsync(int delayMs = 0, int timeoutMs = 1500)
    {
        if (_hasPresentedFirstFrame)
        {
            // PresentCloaked 已经让复用窗口以 alpha=0 在后台出现；这里与首次启动、
            // 快捷键呼出共用完全相同的整窗渐显动画。
            PlayRevealAnimation();
            return;
        }

        try
        {
            await WaitUntilReadyAsync(timeoutMs);
        }
        catch { /* 图标失败不影响呈现 */ }

        if (delayMs > 0)
        {
            try { await Task.Delay(delayMs); } catch { /* ignore */ }
        }

        try
        {
            _hasPresentedFirstFrame = true;
            PrepareFadeIn();
            AppWindow.Show();
            WindowHelper.UncloakWhenRendered(this, PlayRevealAnimation);
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Win11 节奏的出现动效：整窗淡入，减速曲线。
    /// **必须做窗口级透明度**（WS_EX_LAYERED + SetLayeredWindowAttributes），不能只动
    /// `ShellRoot.Opacity`：卡片屏幕上的可见外观主要由窗口的 Mica 背景画出来，它在 XAML
    /// 内容之下，XAML 透明度对它完全无效——把 ShellRoot 淡到 0 也只是内容消失、Mica 矩形
    /// 照常显示，肉眼看起来就是"动画没生效"。同理也不要用缩放：缩的只有 XAML 内容，
    /// Mica 外框尺寸不动，看上去是卡片在固定边框里往里缩。
    /// </summary>
    private void PlayRevealAnimation()
    {
        ShellRoot.Opacity = 1;
        ShellRoot.RenderTransform = null;
        FadeWindow(0, 255, OpenDurationMs, EaseOut, null);
    }

    // Win11 节奏：打开 300ms 减速曲线，关闭 200ms 加速曲线。
    private const int OpenDurationMs = 300;
    private const int CloseDurationMs = 200;

    /// <summary>decelerate：起步最快、末尾极缓，对应 cubic-bezier(0, 0, 0, 1) 的手感。</summary>
    private static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>accelerate：起步缓、越到后面越快，用于收起。</summary>
    private static double EaseIn(double t) => t * t * t;

    private DispatcherQueueTimer? _fadeTimer;

    /// <summary>
    /// 用分层窗口 alpha 做整窗淡入淡出。动画结束后立刻摘掉 WS_EX_LAYERED，
    /// 平时保持普通窗口，避免分层状态影响 Mica 合成与命中测试。
    /// </summary>
    private void FadeWindow(byte from, byte to, int durationMs, Func<double, double> ease, Action? completed)
    {
        try
        {
            _fadeTimer?.Stop();
            // 分层样式只在动画期间存在：WS_EX_LAYERED 与 Mica 不能共存，常驻分层会让
            // 卡片彻底失去材质（实测：整窗一直是纯色）。动画一结束立刻摘掉。
            SetLayered(true);
            SetWindowAlpha(from);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var timer = DispatcherQueue.CreateTimer();
            _fadeTimer = timer;
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += (s, _) =>
            {
                var progress = Math.Clamp(clock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
                SetWindowAlpha((byte)Math.Round(from + (to - from) * ease(progress)));
                if (progress < 1) return;

                s.Stop();
                if (ReferenceEquals(_fadeTimer, s)) _fadeTimer = null;
                // 终点不透明就立刻摘掉分层样式，把 Mica 还回来；终点全透明的收起流程
                // 由 LowerAnimatedAsync 在降 Z 序之后再摘，避免摘掉的瞬间闪一下实体卡片。
                if (to == 255) SetLayered(false);
                completed?.Invoke();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.FadeWindow", ex);
            SetWindowAlpha(255);
            SetLayered(false);
            completed?.Invoke();
        }
    }

    /// <summary>在窗口露面之前先把整窗透明度压到 0，避免解除 Cloak / 置顶时闪一帧实体窗口。</summary>
    private void PrepareFadeIn()
    {
        try
        {
            _fadeTimer?.Stop();
            _fadeTimer = null;
            SetLayered(true);
            SetWindowAlpha(0);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.PrepareFadeIn", ex);
        }
    }

    /// <summary>把卡片降级为工具窗口，让系统不再把它当作独立的应用窗口列举
    /// （关机界面、任务管理器的窗口分组都按这个标志判断）。</summary>
    /// <remarks>WS_EX_TOOLWINDOW 与 Mica **不**冲突——冲突的是 WS_EX_LAYERED（见 7.4）；
    /// 淡入淡出期间临时加 / 摘 LAYERED 的 <see cref="SetLayered"/> 只改那一位，不受影响。
    /// 必须在窗口首次 Show 之前设置，否则 Shell 可能残留任务栏状态。</remarks>
    private void MarkAsToolWindow()
    {
        try
        {
            var style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            if ((style & WS_EX_TOOLWINDOW) != 0) return;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.MarkAsToolWindow", ex);
        }
    }

    private void SetLayered(bool layered)
    {
        var style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        var updated = layered ? style | WS_EX_LAYERED : style & ~WS_EX_LAYERED;
        if (updated == style) return;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(updated));
    }

    private void SetWindowAlpha(byte alpha) => SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);

    public void HideCard()
    {
        LibrarySurfaceReady = false;
        try { AppWindow.Hide(); }
        catch { /* ignore */ }
    }

    /// <summary>关闭分区功能时先整窗渐隐，动画完成后才真正隐藏窗口。</summary>
    public async Task HideAnimatedAsync(Func<bool>? shouldHide = null)
    {
        try
        {
            var completion = new TaskCompletionSource();
            FadeWindow(255, 0, CloseDurationMs, EaseIn, () => completion.TrySetResult());
            await completion.Task;
            if (shouldHide?.Invoke() != false)
                AppWindow.Hide();
            SetWindowAlpha(255);
            SetLayered(false);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.HideAnimated", ex);
            SetWindowAlpha(255);
            SetLayered(false);
            HideCard();
        }
    }

    /// <summary>
    /// 把卡片可靠地抬到当前前台窗口之上（快捷键「呼出」），但不抢焦点。
    /// 保持 TOPMOST 直到用户再次按下快捷键；若同一次调用里立即取消置顶，Windows
    /// 可能马上让当前前台窗口重新盖住卡片，看起来就像快捷键完全没有响应。
    /// </summary>
    public void RaiseToFront()
    {
        try
        {
            ShowCard();
            var flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (IsTopmost()) return;

            // 长时间运行后，个别卡片窗口会进入"设不上 WS_EX_TOPMOST"的状态：
            // SetWindowPos 返回成功，扩展样式却纹丝不动，于是这张卡片永远压在别的
            // 窗口下面，用户看到的就是"有一个分区一直不出现"。先显式退回 NOTOPMOST
            // 再重设，必要时隐藏 / 重新显示一次窗口，能把这个状态复位。
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (IsTopmost()) return;

            AppWindow.Hide();
            AppWindow.Show();
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, flags);
            if (!IsTopmost())
            {
                Services.ErrorReporter.Log("DesktopCard.RaiseToFront", new InvalidOperationException(
                    $"分区「{_zoneName}」的窗口拒绝置顶，本次呼出会被其它窗口盖住。"));
            }
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.RaiseToFront", ex);
        }
    }

    /// <summary>这个 HWND 是不是本卡片自己的窗口。前台切到卡片自身时不能当成"用户点到别处"。</summary>
    public bool OwnsHandle(IntPtr hwnd) => hwnd == _hwnd;

    /// <summary>本卡片的顶层窗口句柄。管理器要拿它比对右键菜单等弹出窗口的属主。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>屏幕坐标（物理像素）是否落在这张卡片窗口内。用于「点到别处就收起」的判定。</summary>
    public bool ContainsScreenPoint(int x, int y)
    {
        try
        {
            if (!GetWindowRect(_hwnd, out var rect)) return false;
            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }
        catch { return false; }
    }

    private bool IsTopmost()
    {
        try { return (GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0; }
        catch { return true; }
    }

    public void RaiseImmediately()
    {
        _fadeTimer?.Stop();
        _fadeTimer = null;
        ShellRoot.Opacity = 1;
        ShellRoot.RenderTransform = null;
        SetWindowAlpha(255);
        SetLayered(false);
        RaiseToFront();
    }

    internal Task WaitForLibraryIconsAsync() => Task.WhenAll((ItemSource ?? this)._iconTasks.ToArray());
    internal bool LibrarySurfaceReady { get; private set; }
    internal void MarkLibrarySurfaceReady() => LibrarySurfaceReady = true;
    internal void ParkLibrary() => WindowHelper.SetWindowCloak(this, true);

    internal void PrepareGroupReveal()
    {
        if (LibrarySurfaceReady)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return;
        }
        WindowHelper.SetWindowCloak(this, true);
        WindowHelper.DisableWindowTransitions(this);
        // The manager releases the whole group together, never per-window callbacks.
        _hasPresentedFirstFrame = true;
        RaiseImmediately();
    }

    public async Task RaiseAnimatedAsync(int delayMs = 0, Func<bool>? shouldReveal = null)
    {
        await WaitUntilReadyAsync();
        if (delayMs > 0) await Task.Delay(delayMs);
        if (shouldReveal?.Invoke() == false) return;
        try
        {
            // 顺序不能反：先把整窗透明度压到 0，再抬到最前。反过来的话窗口会以完全
            // 不透明的状态露出一帧，用户看到的就是"按下快捷键先闪一下，然后才慢慢淡入"。
            PrepareFadeIn();
            RaiseToFront();
            PlayRevealAnimation();
        }
        catch { RaiseToFront(); }
    }

    /// <summary>把快捷键临时呼出的卡片降回其它应用之后，不关闭桌面卡片。</summary>
    public void LowerFromFront()
    {
        try
        {
            var flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW;
            SetWindowPos(_hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, flags);
        }
        catch { /* ignore */ }
    }

    public async Task LowerAnimatedAsync(int delayMs = 0)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        try
        {
            var completion = new TaskCompletionSource();
            FadeWindow(255, 0, CloseDurationMs, EaseIn, () => completion.TrySetResult());
            await completion.Task;
            // 先降 Z 序再恢复不透明：顺序反了会在动画末尾闪一下完整卡片。
            LowerFromFront();
            SetWindowAlpha(255);
            SetLayered(false);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.LowerAnimated", ex);
            SetWindowAlpha(255);
            SetLayered(false);
            LowerFromFront();
        }
    }

    private bool _draggingItems;
    private bool _refreshingItems;
    private bool _itemOrderSaveQueued;

    private void QueueItemOrderSave()
    {
        if (_refreshingItems || _draggingItems || _itemOrderSaveQueued) return;
        _itemOrderSaveQueued = true;
        // 将同一轮集合变更合并，下一轮保存最终顺序。
        DispatcherQueue.TryEnqueue(() =>
        {
            _itemOrderSaveQueued = false;
            if (!CardItemOrderStore.Save(_zoneName, _items.Select(item => item.Path)))
                ShowTransientToast("图标顺序保存失败，请重试。");
            else ContentChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>重新读取托管文件夹内容。</summary>
    public void RefreshContent(System.Collections.Generic.IReadOnlyList<string>? knownPaths = null)
    {
        // 拖动持有容器，期间不要清空集合；结束后再合并磁盘变化。
        if (_draggingItems || _itemOrderSaveQueued) return;
        _refreshingItems = true;
        try
        {
            var incoming = knownPaths ?? DesktopCollectService.ListCardItems(_zoneName);
            var sourcePaths = ItemSource?.SnapshotPaths();
            var paths = sourcePaths != null && sourcePaths.Count == incoming.Count
                && new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase).SetEquals(incoming)
                ? sourcePaths : CardItemOrderStore.Apply(_zoneName, incoming);
            // Opening an unchanged library must not recreate GridView containers or rerun layout.
            if (_items.Select(item => item.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            {
                if (_cols != Math.Clamp(SettingsService.Instance.Current.DesktopCardMaxColumns, 1, MaxColumnsCap))
                {
                    _hasMeasuredHeight = false;
                    ApplyAutoSize(paths.Count);
                }
                return;
            }

            // 路径没变的项目**直接复用旧对象**，连带保留已经加载好的图标。
            // 每次刷新都 new 一遍的话，托管目录里动一个文件就要把整张卡片的图标重新走一遍
            // Shell / WScript.Shell 解析（单个 .lnk 几十毫秒），拖放和收纳时尤其明显。
            var reusable = new Dictionary<string, CardItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items) reusable.TryAdd(item.Path, item);
            // Both windows live on the same UI thread. Share item data and decoded images,
            // while keeping each window's collection, selection and layout independent.
            if (ItemSource != null)
                foreach (var item in ItemSource._items) reusable.TryAdd(item.Path, item);

            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            LibrarySurfaceReady = false;
            for (var index = _items.Count - 1; index >= 0; index--)
                if (!wanted.Contains(_items[index].Path)) _items.RemoveAt(index);
            // 图标任务留一份句柄：首次呈现前要等它们跑完，否则卡片会先露出一批空占位。
            _iconTasks.RemoveAll(task => task.IsCompleted);
            for (var index = 0; index < paths.Count; index++)
            {
                var path = paths[index];
                if (index < _items.Count && string.Equals(_items[index].Path, path, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (reusable.TryGetValue(path, out var existing))
                {
                    var oldIndex = _items.IndexOf(existing);
                    if (oldIndex >= 0) _items.Move(oldIndex, index);
                    else _items.Insert(index, existing);
                    continue;
                }

                var item = new CardItem(path);
                _items.Insert(index, item);
                _iconTasks.Add(item.LoadIconAsync());
            }

            EmptyHint.Visibility = paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ItemsGrid.Visibility = paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ApplyAutoSize(paths.Count);
            // ItemsPanelRoot 要等容器生成后才有，排到下一轮再挂。
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, HookContentPanel);
        }
        catch { /* ignore */ }
        finally { _refreshingItems = false; }
    }


    /// <summary>本卡片里有图标被选中了。管理器用它把其它卡片的选中清掉——
    /// 九张卡片是九个独立窗口，各自的 GridView 互不知情，不清就会同时亮好几个。</summary>
    public event EventHandler? ItemSelected;

    /// <summary>清掉本卡片的选中态。</summary>
    public void ClearSelection()
    {
        try { if (ItemsGrid.SelectedIndex >= 0) ItemsGrid.SelectedIndex = -1; }
        catch { /* ignore */ }
    }

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid.SelectedIndex >= 0) ItemSelected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>点在图标以外的地方（卡片空白处、边距）就取消选中，和 Windows 桌面一致。</summary>
    /// <summary>上一次按下的图标与时间、位置，用来判断双击。</summary>
    private CardItem? _lastPressItem;
    private long _lastPressTick;
    private POINT _lastPressPoint;

    /// <summary>
    /// 点击处理：点在图标上交给 GridView 选中，并判断是不是双击；点在空白处清掉选中。
    /// 双击打开不用 DoubleTapped：那是手势识别出来的，鼠标稍一动就会被拖动或平移取消，而且只挂在图标内部很小的区域上。
    /// 这里按系统的双击时间与双击距离自己判断，整个单元格都能双击，也不会被拖动打断。
    /// </summary>
    private void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        CardItem? pressed = null;
        var node = e.OriginalSource as DependencyObject;
        while (node != null)
        {
            if (node is GridViewItem gridItem)
            {
                pressed = gridItem.DataContext as CardItem;
                break;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }

        if (pressed == null)
        {
            _lastPressItem = null;
            ClearSelection();
            return;
        }

        // 只有鼠标左键参与双击判断；右键菜单、中键不计入。
        if (!e.GetCurrentPoint(ShellRoot).Properties.IsLeftButtonPressed) return;

        GetCursorPos(out var cursor);
        var now = Environment.TickCount64;
        var sameItem = ReferenceEquals(pressed, _lastPressItem);
        var inTime = now - _lastPressTick <= GetDoubleClickTime();
        var inDistance = Math.Abs(cursor.X - _lastPressPoint.X) <= GetSystemMetrics(SM_CXDOUBLECLK) / 2
                         && Math.Abs(cursor.Y - _lastPressPoint.Y) <= GetSystemMetrics(SM_CYDOUBLECLK) / 2;

        if (sameItem && inTime && inDistance)
        {
            // 打开后清掉记录：三击不会再触发第二次打开。
            _lastPressItem = null;
            OpenItem(pressed);
            return;
        }

        _lastPressItem = pressed;
        _lastPressTick = now;
        _lastPressPoint = cursor;
    }

    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private void OpenItem(CardItem item)
    {
        try
        {
            // 系统虚拟图标没有文件路径，交给 explorer 解析（见 DesktopShellItems.Open）。
            if (item.IsShellItem)
            {
                DesktopShellItems.Open(item.Path);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true,
            });
        }
        catch { /* 启动失败静默 */ }
    }

    /// <summary>通过可传递的字符串携带路径与源分区，避免跨窗口传递托管 UI 对象。</summary>
    private void ItemsGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is CardItem item)
        {
            _draggingItems = true;
            e.Data.Properties["CardItem"] = item.Path;
            e.Data.Properties["SourceCard"] = _zoneName;
            // Properties 只是元数据；至少提供一个真实数据格式，系统才会启动 OLE 拖放。
            e.Data.SetData("WinTools.DesktopCardItem", item.Path);
            // 同时放行「复制」：暂存窗口、资源管理器等目标只接受复制；只声明 Move 的话它们一律显示禁止光标。
            // 卡片之间的换区 / 排序仍按 Move 处理（目标窗口 DragOver 里返回 Move）。
            e.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
            if (item.IsShellItem) return; // 「此电脑」这类系统图标没有文件可交给别的程序

            // 再带上真实的文件 / 文件夹项，拖到暂存窗口或其他程序里才能落下；目标真正要数据时才异步取。
            var path = item.Path;
            e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    IStorageItem? storageItem = null;
                    if (File.Exists(path)) storageItem = await StorageFile.GetFileFromPathAsync(path);
                    else if (Directory.Exists(path)) storageItem = await StorageFolder.GetFolderFromPathAsync(path);
                    if (storageItem != null) request.SetData(new[] { storageItem });
                }
                catch (Exception ex) { ErrorReporter.Log("DesktopCard.DragStorageItem", ex); }
                finally { deferral.Complete(); }
            });
        }
    }

    /// <summary>接受携带卡片路径的拖动，并提示分区内排序。</summary>
    private void ItemsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.TryGetValue("CardItem", out var obj) && obj is string)
        {
            // 同卡片内拖动 = 调整顺序；跨卡片 = 换区。两种都要在这里接住，
            // 否则系统显示"禁止"光标、松手也不会触发 Drop。
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
        }
    }

    /// <summary>在目标分区 GridView 上释放：把源文件物理移动到当前分区所在目录，
    /// 然后由双方 FileSystemWatcher 触发 ContentChanged → Sync → 卡片自动刷新。</summary>
    private void ItemsGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue("CardItem", out var obj) || obj is not string path)
            return;
        var item = _items.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? new CardItem(path);

        if (e.DataView.Properties.TryGetValue("SourceCard", out var source) && source is string zone && zone == _zoneName)
        {
            ReorderWithinCard(item, e);
            return;
        }

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        // 系统图标（此电脑等）没有文件，按显示名归属即可，和右键菜单「移动到分区」一致。
        var result = item.IsShellItem
            ? DesktopCollectService.MoveZoneResult.Success
            : DesktopCollectService.CanAssignToZone(item.Path, _zoneName);
        switch (result)
        {
            case DesktopCollectService.MoveZoneResult.Success:
                e.AcceptedOperation = DataPackageOperation.Move;
                // **文件不动**，换区就是把名字记进目标分区的显式清单（匹配优先级最高的一档）。
                // 不记的话，下一次匹配会按关键词把它算回原来的分区。
                ItemMovedIn?.Invoke(this, new CardItemMovedEventArgs(item.DisplayName, _zoneName));
                // 归属变了，两张卡片都要重画：桌面监视器不会因为"没有文件变化"而触发。
                ContentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case DesktopCollectService.MoveZoneResult.SameZone:
                // 已经属于本分区，不用改判。
                break;
            case DesktopCollectService.MoveZoneResult.SourceNotFound:
            case DesktopCollectService.MoveZoneResult.PhysicalFailed:
            case DesktopCollectService.MoveZoneResult.TargetInvalid:
                ShowTransientToast("换区失败：项目不存在或目标分区无效。");
                break;
        }
    }

    /// <summary>
    /// 卡片内拖动排序：按松手位置算出插入点，直接在集合里挪。
    /// </summary>
    /// <remarks>
    /// 以前这件事交给 GridView 的 <c>CanReorderItems</c>，换成 <see cref="CardTilesPanel"/>
    /// 之后那套内置重排不再生效（拖得动、松手不插入）。自己做反而更可控：插入点就是面板按
    /// 自适应行高算出来的那一格，不会和实际摆放对不上。
    /// <c>CanReorderItems</c> 本身仍要保持 True：关掉它，拖动会话就不再把 DragOver/Drop
    /// 发回源列表，这里根本不会被调用（2026-09-18 埋点：只有 Starting → Completed(None)）。
    /// 挪完之后 <c>DragItemsCompleted</c> 看到 DropResult=Move 会把顺序落盘。
    /// </remarks>
    private void ReorderWithinCard(CardItem item, DragEventArgs e)
    {
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (ItemsGrid.ItemsPanelRoot is not CardTilesPanel panel) return;

        var oldIndex = _items.IndexOf(item);
        if (oldIndex < 0) return;

        var insertion = panel.GetInsertionIndex(e.GetPosition(panel));
        // 先把自己拿掉，插入点在它后面的都要往前挪一位。
        var newIndex = insertion > oldIndex ? insertion - 1 : insertion;
        newIndex = Math.Clamp(newIndex, 0, _items.Count - 1);

        e.AcceptedOperation = DataPackageOperation.Move;
        if (newIndex != oldIndex) _items.Move(oldIndex, newIndex);
    }

    /// <summary>拖放结束后保存顺序，再合并拖动期间的目录变化。</summary>
    private void ItemsGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        _draggingItems = false;
        if (e.DropResult == DataPackageOperation.Move &&
            !CardItemOrderStore.Save(_zoneName, _items.Select(item => item.Path)))
        {
            ShowTransientToast("图标顺序保存失败，请重试。");
        }
        RefreshContent();
        if (e.DropResult == DataPackageOperation.Move)
            ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「重命名」（右键菜单 / F2）：在图标上弹出输入框，回车确认、Esc 取消。</summary>
    /// <remarks>
    /// 不做资源管理器那种"就地编辑文件名"：卡片格子只有 76dip 宽，塞一个输入框既看不全名字，
    /// 按下去还会先被 GridView 当成拖动起手。弹出层能给够宽度，也有地方显示报错原因。
    /// </remarks>
    private void BeginRename(CardItem item)
    {
        if (item.IsShellItem)
        {
            ShowTransientToast("系统图标不能重命名。");
            return;
        }
        // 右键菜单还在关闭过程中，这一轮直接弹第二个 Flyout 会被吞掉（和「显示系统右键菜单」
        // 同款处理），排到下一轮再弹。
        DispatcherQueue.TryEnqueue(() => ShowRenameFlyout(item));
    }

    private void ShowRenameFlyout(CardItem item)
    {
        try
        {
            if (ItemsGrid.ContainerFromItem(item) is not FrameworkElement anchor) return;

            var box = new TextBox
            {
                Text = item.DisplayName,
                Width = 240,
                SelectionStart = 0,
                SelectionLength = item.DisplayName.Length,
            };
            var error = new TextBlock
            {
                Visibility = Visibility.Collapsed,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            };
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(box);
            panel.Children.Add(error);

            var flyout = new Flyout
            {
                Content = panel,
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            };

            void Commit()
            {
                var message = TryRename(item, box.Text);
                if (message == null)
                {
                    flyout.Hide();
                    return;
                }
                error.Text = message;
                error.Visibility = Visibility.Visible;
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            }

            box.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Enter)
                {
                    args.Handled = true;
                    Commit();
                }
                else if (args.Key == Windows.System.VirtualKey.Escape)
                {
                    args.Handled = true;
                    flyout.Hide();
                }
            };
            flyout.Opened += (_, _) =>
            {
                box.Focus(FocusState.Programmatic);
                box.SelectAll();
            };

            flyout.ShowAt(anchor);
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Log("DesktopCard.ShowRenameFlyout", ex);
            ShowTransientToast("打不开重命名输入框。");
        }
    }

    /// <summary>执行重命名。成功返回 null，失败返回给用户看的原因。</summary>
    private string? TryRename(CardItem item, string newDisplayName)
    {
        var trimmed = (newDisplayName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "名字不能为空。";
        if (string.Equals(trimmed, item.DisplayName, StringComparison.Ordinal)) return null;  // 没改，当成取消

        // 卡片上显示的是不含扩展名的名字（和资源管理器隐藏扩展名时一致），
        // 所以拼回原扩展名，用户不会因为改个名字把 .lnk 弄丢。
        var extension = System.IO.Path.GetExtension(item.Path);
        var result = DesktopCollectService.RenameItem(item.Path, trimmed + extension, out var newPath);
        switch (result)
        {
            case DesktopCollectService.RenameResult.Success:
                // 顺序表按文件名记录，不同步改的话图标会掉到同类的末尾。
                CardItemOrderStore.Rename(_zoneName, item.Path, newPath);
                // 改完名字可能已经不属于本分区了（分区是按关键字实时匹配的），
                // 所以除了刷新自己，还要通知管理器做一次同步。
                RefreshContent();
                ContentChanged?.Invoke(this, EventArgs.Empty);
                return null;
            case DesktopCollectService.RenameResult.NotFound:
                return "原文件已经不在了，可能刚被移走或删除。";
            case DesktopCollectService.RenameResult.InvalidName:
                return "名字里含有 Windows 不允许的字符（冒号、斜杠、星号、问号、竖线、尖括号），或者以空格 / 点结尾。";
            case DesktopCollectService.RenameResult.NameExists:
                return "这个名字已经被同目录下的其它项目占用了。";
            default:
                return "重命名失败，可能没有权限（公共桌面上的项目需要管理员）。";
        }
    }

    /// <summary>「删除」（右键菜单 / Delete 键）：通过 SHFileOperation + FOF_ALLOWUNDO 移到回收站。</summary>
    private void DeleteItem(CardItem item)
    {
        // 系统虚拟图标（此电脑 / 回收站…）没有文件可删，SHFileOperation 对它无意义。
        // 想让它从桌面消失，得去「个性化 → 主题 → 桌面图标设置」里关。
        if (item.IsShellItem)
        {
            ShowTransientToast("系统图标不能删除。请在「个性化 → 主题 → 桌面图标设置」里关闭。");
            return;
        }
        var result = DesktopCollectService.DeleteToRecycleBin(item.Path);
        switch (result)
        {
            case DesktopCollectService.RecycleBinResult.Success:
                // 先立即更新当前卡片，避免用户看到已不存在的入口；再让管理器做一次全量同步，
                // 同时覆盖公共桌面、Shell 延迟通知或 FileSystemWatcher 偶发漏事件的情况。
                _items.Remove(item);
                ContentChanged?.Invoke(this, EventArgs.Empty);
                break;
            case DesktopCollectService.RecycleBinResult.NotFound:
            case DesktopCollectService.RecycleBinResult.Failed:
                ShowTransientToast("删除失败：文件可能已被移走或回收站被禁用。");
                break;
            case DesktopCollectService.RecycleBinResult.Aborted:
                // 用户取消或 Shell 拒绝——不打扰。
                break;
        }
    }

    /// <summary>「显示更多选项」（右键菜单 / Shift+F10）：Windows Shell 和已安装扩展提供的完整原生菜单。</summary>
    /// <param name="screenPoint">键盘呼出时弹在图标旁边；鼠标呼出时为空，弹在光标处。</param>
    private void ShowSystemMenu(CardItem item, (int X, int Y)? screenPoint = null)
    {
        // 右键菜单的点击回调执行时原菜单仍在关闭过程中；此处同步进入 TrackPopupMenuEx
        // 会形成两个嵌套菜单消息循环，表现为窗口卡死。排到下一轮，让 WinUI 先完整收起菜单。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ShellContextMenu.Show(item.Path, _hwnd, screenPoint))
                ShowTransientToast("无法打开系统右键菜单。");
        });
    }

    private Microsoft.UI.Xaml.Controls.InfoBar? _toast;

    /// <summary>短暂的轻量提示。卡片是桌面浮层，没必要弹模态 ContentDialog。</summary>
    private void ShowTransientToast(string message,
        Microsoft.UI.Xaml.Controls.InfoBarSeverity severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning)
    {
        try
        {
            // 新提示顶掉旧的：「正在压缩」后紧跟「已压缩」这类连续提示不能叠在同一个位置。
            if (_toast != null && ShellRoot.Children.Contains(_toast)) ShellRoot.Children.Remove(_toast);
            var flyout = new Microsoft.UI.Xaml.Controls.InfoBar
            {
                Title = "桌面卡片",
                Message = message,
                IsOpen = true,
                IsClosable = true,
                Severity = severity,
            };
            _toast = flyout;
            // 简单做法：把它放在 ShellRoot 里覆盖卡片底部，3 秒后自动关闭。
            flyout.Closed += (_, _) => { if (ShellRoot.Children.Contains(flyout)) ShellRoot.Children.Remove(flyout); };
            ShellRoot.Children.Add(flyout);
            var timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => { flyout.IsOpen = false; timer.Stop(); };
            timer.Start();
        }
        catch
        {
            // 提示也失败就吞掉。
        }
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        var cardBrush = WindowHelper.GetDesktopCardBrush(theme);
        var surfaceColor = cardBrush.Color;
        CardSurface.Background = cardBrush;
        CardSurface.BorderBrush = WindowHelper.GetCodexBorderBrush(theme);
        // DWMWA_COLOR_NONE 在部分多屏/DPI 组合下会在圆角两端留下亮点。
        // DWM 仍独占外圆角，但边缘颜色与卡片底色一致，视觉上不再出现白点。
        WindowHelper.MatchSystemWindowBorder(this, Windows.UI.Color.FromArgb(
            255, surfaceColor.R, surfaceColor.G, surfaceColor.B));
        ApplyPersistentMica(theme);
    }

    /// <summary>
    /// 使用显式 MicaController，并始终保持输入激活状态。系统默认 MicaBackdrop 会在窗口
    /// 失焦后切换为不透明灰色；桌面卡片需要像参考软件一样持续保留材质感。
    /// </summary>
    private void ApplyPersistentMica(ElementTheme theme)
    {
        try
        {
            if (!MicaController.IsSupported())
            {
                WindowHelper.ApplyWindowBackdrop(this);
                return;
            }

            if (_micaController == null)
            {
                // 只在首次接管合成目标时清掉普通 SystemBackdrop。已连接后再次清空会让
                // MicaController 的目标失效，主题切换后透明卡片便会露出黑色底层。
                SystemBackdrop = null;
                _backdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
                _backdropConfiguration ??= new SystemBackdropConfiguration();
                _micaController = new MicaController { Kind = MicaKind.Base };
                _micaAttached = _micaController.AddSystemBackdropTarget(_backdropTarget);
                if (_micaAttached)
                    _micaController.SetSystemBackdropConfiguration(_backdropConfiguration);
            }

            if (!_micaAttached || _backdropConfiguration == null || _micaController == null)
                return;

            var isDark = theme == ElementTheme.Dark;
            _backdropConfiguration.IsInputActive = true;
            _backdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
            _micaController.TintColor = isDark
                ? Windows.UI.Color.FromArgb(255, 30, 31, 34)
                : Windows.UI.Color.FromArgb(255, 248, 249, 252);
            _micaController.FallbackColor = isDark
                ? Windows.UI.Color.FromArgb(255, 34, 35, 38)
                : Windows.UI.Color.FromArgb(255, 246, 247, 250);
            _micaController.TintOpacity = isDark ? 0.24f : 0.12f;
            _micaController.LuminosityOpacity = isDark ? 0.74f : 0.88f;
        }
        catch
        {
            WindowHelper.ApplyWindowBackdrop(this);
        }
    }

    public void Cleanup()
    {
        try { _fadeTimer?.Stop(); _fadeTimer = null; } catch { /* ignore */ }
        ReleaseShareHandler();
        try
        {
            if (_micaController != null)
            {
                _micaController.RemoveAllSystemBackdropTargets();
                _micaController.Dispose();
            }
        }
        catch { /* ignore */ }
        _micaController = null;
        _micaAttached = false;
        _backdropConfiguration = null;
        _backdropTarget = null;
        ThemeService.Unregister(this);
        UiStyleService.UnregisterShell(this);

        // 先解订阅再关闭，否则 Closing 仍会被 e.Cancel = true 拦下，
        // Window 永远停在隐藏态、HWND 也不会被 WinUI 真正回收。
        if (_closingHandler != null)
        {
            try { AppWindow.Closing -= _closingHandler; } catch { /* ignore */ }
            _closingHandler = null;
        }
        try { Close(); } catch { /* 已关闭或非主线程重复关闭会抛 */ }
    }
}
