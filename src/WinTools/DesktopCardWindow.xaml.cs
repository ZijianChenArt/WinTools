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
        _items.CollectionChanged += (_, _) => QueueItemOrderSave();
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
            var paths = CardItemOrderStore.Apply(_zoneName,
                knownPaths ?? DesktopCollectService.ListCardItems(_zoneName));

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
        finally { _refreshingItems = false; }
    }


    private void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CardItem item }) return;
        e.Handled = true;
        OpenItem(item);
    }

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
            e.Data.RequestedOperation = DataPackageOperation.Move;
        }
    }

    /// <summary>接受携带卡片路径的拖动，并提示分区内排序。</summary>
    private void ItemsGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.TryGetValue("CardItem", out var obj) && obj is string)
        {
            if (e.DataView.Properties.TryGetValue("SourceCard", out var source) && source is string zone && zone == _zoneName)
            {
                // 同卡片内不接管事件：交给 CanReorderItems 产生 Fluent 插入间隙和原生让位动画。
                return;
            }

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

        // 同卡片内不接管 Drop，由 CanReorderItems 完成最终插入。
        if (e.DataView.Properties.TryGetValue("SourceCard", out var source) && source is string zone && zone == _zoneName)
            return;

        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        var result = DesktopCollectService.CanAssignToZone(item.Path, _zoneName);
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
    }

    /// <summary>右键菜单「打开」：走默认 Shell 启动（.exe 直接运行、.doc 走关联软件等）。</summary>
    private void ItemMenu_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardItem item }) return;
        OpenItem(item);
    }

    /// <summary>右键菜单「删除到回收站」：通过 SHFileOperation + FOF_ALLOWUNDO 移到回收站。</summary>
    private void ItemMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardItem item }) return;
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

    /// <summary>显示该项目由 Windows Shell 和已安装扩展共同提供的完整原生右键菜单。</summary>
    private void ItemMenu_SystemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CardItem item }) return;
        // MenuFlyout 的 Click 回调执行时原菜单仍在关闭过程中；此处同步进入 TrackPopupMenuEx
        // 会形成两个嵌套菜单消息循环，表现为窗口卡死。排到下一轮，让 WinUI 先完整收起菜单。
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!ShellContextMenu.Show(item.Path, _hwnd))
                ShowTransientToast("无法打开系统右键菜单。");
        });
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
