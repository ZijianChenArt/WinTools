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

    // WScript.Shell 必须运行在 STA。统一复用一个后台 STA 线程，既不阻塞 WinUI 首帧，
    // 也避免为几十个快捷方式各创建一条线程。
    private static readonly BlockingCollection<IconLoadRequest> IconLoadQueue = new();

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
        var thread = new Thread(ProcessIconLoadQueue)
        {
            IsBackground = true,
            Name = "WinTools shell icon loader",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private BitmapImage? _icon;

    public CardItem(string path)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(DisplayName))
            DisplayName = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string DisplayName { get; }

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
    private static byte[]? ReadExtractedIconPng(string path, int index)
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

        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return default;

            shell = Activator.CreateInstance(shellType);
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
            return default;
        }
        finally
        {
            // 某些 WinRT/COM 组合会在 FinalReleaseComObject 时报告 RCW 已分离；
            // 资源释放失败不能反过来让已经解析成功的图标变成空白。
            try
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                    Marshal.FinalReleaseComObject(shortcut);
            }
            catch { }

            try
            {
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
            catch { }
        }
    }
}

/// <summary>
/// 单个「桌面分区」卡片：常驻桌面、可拖动 / 缩放、只用 Mica 材质。
/// 内容是该分区托管文件夹（<see cref="DesktopCollectService.ZoneFolder"/>）里的真实文件，
/// 显示真实图标，单击启动。
/// </summary>
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;

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
    private FileSystemWatcher? _watcher;
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
    public int DipHeight { get; private set; } = HeaderHeight + TileHeight + GridPadding * 2;
    /// <summary>当前卡片在桌面上的位置（DIP）。PlaceAt 时更新，
    /// 供多屏拓扑记忆写入 cache 时记录。</summary>
    public int DipLeft { get; private set; }
    public int DipTop { get; private set; }

    public DesktopCardWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ItemsGrid.ItemsSource = _items;
        ShellRoot.Loaded += (_, _) => _layoutReady.TrySetResult();
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

    public bool HasItems => _items.Count > 0;

    /// <summary>等待窗口完成首次 XAML 加载，确保多显示器 DPI 缩放值可用。</summary>
    public async Task WaitForLayoutReadyAsync()
    {
        if (!ShellRoot.IsLoaded)
            await Task.WhenAny(_layoutReady.Task, Task.Delay(1500));
        await Task.Yield();
    }

    /// <summary>按当前显示器的真实缩放重新应用已计算好的 DIP 尺寸。</summary>
    public void ApplyScaleAwareSize()
    {
        try
        {
            var scale = ShellRoot.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32(
                (int)(DipWidth * scale),
                (int)(DipHeight * scale)));
        }
        catch { /* ignore */ }
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

            WindowHelper.ConfigurePopupTitleBar(this);
            WindowHelper.DisableWindowTransitions(this);
            // 去掉 DWM 的 1px 边框，否则会与卡片自身描边形成「双层框」。
            WindowHelper.RemoveSystemWindowBorder(this);
            AppWindow.IsShownInSwitchers = false;
            ApplyUiStyleSurfaces();

            // 卡片不真正关闭，只隐藏；由管理器在 Cleanup() 里解订阅本 handler
            // 并调用 Close()，让窗口走真正的销毁路径，避免反复开关分区时
            // 卡片窗口长期滞留在 WinUI 的窗口列表里。
            _closingHandler = (_, e) =>
            {
                e.Cancel = true;
                AppWindow.Hide();
            };
            AppWindow.Closing += _closingHandler;
        }
        catch { /* 配置失败不影响主流程 */ }
    }

    /// <summary>绑定到某个分区：读取其托管文件夹内容（尺寸随之自适应）。位置由管理器排布。</summary>
    public void Bind(string zoneName, System.Collections.Generic.IReadOnlyList<string>? initialItems = null)
    {
        _zoneName = zoneName;
        RefreshContent(initialItems);
        StartWatching();
    }

    /// <summary>本卡片所在显示器的工作区（换算为 DIP），供管理器排布。</summary>
    public RectInt32 GetWorkAreaDip()
    {
        try
        {
            var scale = ShellRoot.XamlRoot?.RasterizationScale ?? 1.0;
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
            var scale = ShellRoot.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Move(new PointInt32((int)(dipX * scale), (int)(dipY * scale)));
        }
        catch { /* ignore */ }
    }

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

        // 宽度 = GridPadding * 2 + cols * (TileWidth + ItemMarginDip * 2)
        // ItemMarginDip 同时给左右，所以乘 2。GridViewItem 容器本身已 Padding=0，不另算外边距。
        DipWidth = cols * TileWidth + cols * ItemMarginDip * 2 + GridPadding * 2;
        DipHeight = HeaderHeight + rows * TileHeight + GridPadding + 10;

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

    public async Task RaiseAnimatedAsync(int delayMs = 0)
    {
        await WaitUntilReadyAsync();
        if (delayMs > 0) await Task.Delay(delayMs);
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

    /// <summary>重新读取托管文件夹内容。</summary>
    public void RefreshContent(System.Collections.Generic.IReadOnlyList<string>? knownPaths = null)
    {
        try
        {
            var paths = knownPaths ?? DesktopCollectService.ListCardItems(_zoneName);

            // 路径没变的项目**直接复用旧对象**，连带保留已经加载好的图标。
            // 每次刷新都 new 一遍的话，托管目录里动一个文件就要把整张卡片的图标重新走一遍
            // Shell / WScript.Shell 解析（单个 .lnk 几十毫秒），拖放和收纳时尤其明显。
            var reusable = new Dictionary<string, CardItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items) reusable.TryAdd(item.Path, item);

            _items.Clear();
            // 图标任务留一份句柄：首次呈现前要等它们跑完，否则卡片会先露出一批空占位。
            _iconTasks.Clear();
            foreach (var path in paths)
            {
                if (reusable.TryGetValue(path, out var existing))
                {
                    _items.Add(existing);
                    continue;
                }

                var item = new CardItem(path);
                _items.Add(item);
                _iconTasks.Add(item.LoadIconAsync());
            }

            EmptyHint.Visibility = paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ItemsGrid.Visibility = paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ApplyAutoSize(paths.Count);
        }
        catch { /* ignore */ }
    }

    /// <summary>监视托管文件夹，内容变化时自动刷新卡片。</summary>
    private void StartWatching()
    {
        try
        {
            _watcher?.Dispose();
            var dir = DesktopCollectService.ZoneFolder(_zoneName);
            void OnChanged(object s, FileSystemEventArgs e) =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshContent();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                });

            if (Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += OnChanged;
                _watcher.Deleted += OnChanged;
                _watcher.Renamed += (s, e) => DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshContent();
                    ContentChanged?.Invoke(this, EventArgs.Empty);
                });
            }

        }
        catch { /* 监视失败只是不自动刷新 */ }
    }

    private void Items_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CardItem item) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true,
            });
        }
        catch { /* 启动失败静默 */ }
    }

    /// <summary>把被拖动的 CardItem 引用塞到 DataPackage，让目标 GridView 在 Drop 时能拿到。
    /// WinUI 3 的 GridView 拖放默认走 DataView，但跨 DataTemplate/跨窗口需要显式传递引用，
    /// 所以用 Properties 字典挂自定义键。</summary>
    private void ItemsGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is CardItem item)
        {
            e.Data.Properties["CardItem"] = item;
        }
    }

    /// <summary>拖动经过时决定是否接受 Drop：只有携带 CardItem 的拖动才接受为"移动"。</summary>
    private void ItemsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.TryGetValue("CardItem", out var obj) && obj is CardItem)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    /// <summary>在目标分区 GridView 上释放：把源文件物理移动到当前分区所在目录，
    /// 然后由双方 FileSystemWatcher 触发 ContentChanged → Sync → 卡片自动刷新。</summary>
    private void ItemsGrid_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue("CardItem", out var obj) || obj is not CardItem item)
            return;

        var result = DesktopCollectService.MoveItemToZone(item.Path, _zoneName);
        switch (result)
        {
            case DesktopCollectService.MoveZoneResult.Success:
                // 物理位置变了还不够：下一次「同步桌面」会按关键词重新归类，把它挪回去。
                // 所以要把这次手动放置记进分区的显式文件清单（匹配优先级最高的一档）。
                ItemMovedIn?.Invoke(this, new CardItemMovedEventArgs(item.DisplayName, _zoneName));
                // 双方 FileSystemWatcher 会自动更新 UI。
                break;
            case DesktopCollectService.MoveZoneResult.SameZone:
                // 拖到本分区不做事（视觉上是原位）。
                break;
            case DesktopCollectService.MoveZoneResult.SourceNotFound:
            case DesktopCollectService.MoveZoneResult.PhysicalFailed:
            case DesktopCollectService.MoveZoneResult.TargetInvalid:
                ShowTransientToast("移动失败：源文件不存在或目标分区无效。");
                break;
        }
    }

    /// <summary>拖动完成后统一让源 GridView 重新同步一遍，避免某些场景下
    /// FileSystemWatcher 没及时触发（比如跨卷移动某些边界情况）。</summary>
    private void ItemsGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult == DataPackageOperation.Move)
        {
            // 源分区会通过 FileSystemWatcher 收到 Deleted 事件 → ContentChanged → Sync。
            // 这里不再主动调用，避免重复 IO。
        }
    }

    /// <summary>右键菜单「打开」：走默认 Shell 启动（.exe 直接运行、.doc 走关联软件等）。</summary>
    private void ItemMenu_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardItem item }) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // 启动失败静默——和单击启动行为一致。
        }
    }

    /// <summary>右键菜单「删除到回收站」：通过 SHFileOperation + FOF_ALLOWUNDO 移到回收站。</summary>
    private void ItemMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardItem item }) return;
        var result = DesktopCollectService.DeleteToRecycleBin(item.Path);
        switch (result)
        {
            case DesktopCollectService.RecycleBinResult.Success:
                // FileSystemWatcher 会在 200ms 内触发 ContentChanged → Sync → 卡片自动少一个图标。
                // 这里什么都不用做。
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

    /// <summary>短暂的轻量提示。卡片是桌面浮层，没必要弹模态 ContentDialog。</summary>
    private void ShowTransientToast(string message)
    {
        try
        {
            var flyout = new Microsoft.UI.Xaml.Controls.InfoBar
            {
                Title = "桌面卡片",
                Message = message,
                IsOpen = true,
                IsClosable = true,
                Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
            };
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
        try { _watcher?.Dispose(); _watcher = null; } catch { /* ignore */ }
        try { _fadeTimer?.Stop(); _fadeTimer = null; } catch { /* ignore */ }
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
