using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 文件预览（仿 QuickLook）：桌面卡片里选中项目按空格呼出，Esc / 空格关闭，左右键切换同一卡片里的文件。
/// 只做常用格式：图片、PDF（逐页）、文本与代码、音视频；其余类型显示系统图标。
/// </summary>
/// <remarks>
/// 窗口只建一次并反复复用。隐藏时立即释放位图、PDF 页与媒体源，所以空闲时不占解码器内存——
/// 这是相对常驻进程的主要优势。创建与展示由 <see cref="FilePreviewService"/> 统一管理。
/// </remarks>
public sealed partial class FilePreviewWindow : Window, IUiStyleShell
{
    /// <summary>文本预览最多读取的字节数。再大的文件只看前半部分，避免一次把几百 MB 的日志塞进 TextBlock。</summary>
    private const int MaxTextBytes = 256 * 1024;

    // 图片、视频、PDF按比例；文本、列表等由Presentation分部按内容测量。所有尺寸均为DIP。
    private const double MaxWidthRatio = 0.85;
    private const double MaxHeightRatio = 0.82;
    private const double MinWidthDip = 380;
    private const double MinHeightDip = 180;
    private const double DefaultWidthDip = 640;
    private const double DefaultHeightDip = 440;

    /// <summary>内容区之外的占位：左右各12；所有信息集中在44 DIP单行标题栏。</summary>
    private const double ChromeWidthDip = 24;
    private const double ChromeHeightDip = 44;
    private const double MediaBarHeightDip = 48;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".svg",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm",
        ".mp3", ".m4a", ".wav", ".flac", ".aac", ".wma",
    };

    /// <summary>
    /// 已知没有可用预览器的类型（设计稿、可执行文件、压缩包、Office 等）。按扩展名直接判定，
    /// 不读取文件内容：AI、PSD 这类文件读了也只是二进制，读的时间全是白花的。
    /// </summary>
    private static readonly HashSet<string> UnsupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ai", ".psd", ".psb", ".eps", ".indd", ".idml", ".ae", ".aep", ".prproj", ".blend", ".max", ".3ds",
        ".fbx", ".dwg", ".dxf", ".skp", ".sketch", ".fig", ".xd",
        ".exe", ".dll", ".msi", ".sys", ".bin", ".dat", ".db", ".sqlite", ".pyc", ".class", ".o", ".so",
        ".rar", ".7z", ".tar", ".gz", ".xz", ".cab", ".iso", ".dmg", ".apk",
        // .docx / .xlsx / .pptx 不在这里：它们是 zip 包里的 XML，可以直接提取文字。
        ".doc", ".xls", ".ppt", ".wps", ".et", ".dps",
        ".lnk", ".url",
    };

    private static readonly HashSet<string> HtmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm",
    };

    private static readonly HashSet<string> StructuredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".json", ".xml",
    };

    private enum Kind { Image, Pdf, Text, StructuredText, Media, Html, OfficeText, Archive, Folder, Unsupported }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private readonly UIElement[] _views;

    // PDF 连续滚动：每页一个 Image（先占位、按视口懒渲染）。_pdfTops / _pdfHeights 是各页在 PdfPages 里的纵向位置（DIP）。
    private readonly List<Image> _pdfImages = new();
    private readonly List<double> _pdfTops = new();
    private readonly List<double> _pdfHeights = new();
    private readonly List<double> _pdfAspects = new();
    private double _pdfLayoutWidth;
    private bool _pdfRendering;

    /// <summary>当前内容的像素尺寸（图片、视频），加载完成后才有；用于标题栏显示和窗口自适应。</summary>
    private (int Width, int Height)? _contentSize;

    private IReadOnlyList<string> _paths = Array.Empty<string>();
    private int _index;
    private bool _isOpen;

    /// <summary>每次切换文件或翻页都递增；异步加载完成时若版本已过期就丢弃结果。</summary>
    private int _loadToken;

    private PdfDocument? _pdf;
    private string? _pdfPath;
    private int _pdfPage;

    public FilePreviewWindow()
    {
        InitializeComponent();
        _views = new UIElement[] { ImageSurface, TextView, ReadingView, ListSurface, TableSurface, PdfView, HtmlView, MediaSurface, FallbackPanel };
        _previewAnimator = new PopupAnimator(this, 120, 80, 0, independentOpacity: true);
        _previewAnimator.Revealed += () =>
        {
            if (_pendingBounds is { } bounds && _isOpen)
            {
                _pendingBounds = null;
                SetPreviewBounds(bounds.Width, bounds.Height);
            }
            TryStartMediaPlayback();
        };
        InitializeMediaControls();
        HeaderBar.SizeChanged += (_, _) => UpdateCompactHeader();
        PreviewToolbar.SizeChanged += (_, _) => UpdateCompactHeader();
        ViewStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateCompactHeader());
        FooterText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateCompactHeader());

        // PDF 页面宽度跟随内容区变化（窗口尺寸调整完成后，页面要跟着重新排版）。
        PdfView.SizeChanged += (_, _) => RelayoutPdf();
        ConfigureWindow();
        AppWindow.SetIcon("Assets\\AppIcon.ico");
        ThemeService.Register(this);
        UiStyleService.Register(this);
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidSizeChange && _isOpen && _revealed && !_previewAnimator.IsBusy
                && (AppWindow.Size.Width != _automaticWidth || AppWindow.Size.Height != _automaticHeight)) _userSized = true;
        };

    }

    #region 窗口配置

    internal string CurrentPath => _index >= 0 && _index < _paths.Count ? _paths[_index] : "";

    internal bool IsOpen => _isOpen;

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.IsAlwaysOnTop = true;

            // 与悬浮弹窗一样：只要标题栏拖动区，不要系统按钮；保留可拖动边框以便调整大小。
            WindowHelper.ConfigurePopupTitleBar(this, HeaderDragRegion);
            WindowHelper.DisableWindowTransitions(this);
            ApplyUiStyleSurfaces();

            AppWindow.IsShownInSwitchers = false;

            // 点到别的窗口就收起，与悬浮搜索一致。
            Activated += OnActivated;

            // 关闭按钮（若有）只隐藏；进程退出时放行。
            AppWindow.Closing += (_, e) =>
            {
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                HideWindow();
            };
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.ConfigureWindow", ex);
        }
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica, elevated: true);
        WindowHelper.ApplyWindowBackdrop(this);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
#if DEBUG
        if (_capturingPreviewLayouts) return;
#endif
        if (args.WindowActivationState == WindowActivationState.Deactivated && _isOpen)
            HideWindow();
    }

    #endregion

    #region 显示 / 切换 / 隐藏

    /// <summary>展示 <paramref name="paths"/> 中的第 <paramref name="index"/> 项；可以是切换到另一个文件。</summary>
    internal void ShowAt(IReadOnlyList<string> paths, int index)
    {
        _paths = paths;
        _index = index;
        Present();
        _ = LoadCurrentAsync();
    }

    /// <summary>
    /// 预备复用窗口；读取选中项后由ShowAt填内容。慢读取在120ms后揭示加载壳，快读取直接显示正文。
    /// </summary>
    internal void ShowPending()
    {
        _loadToken++;
        ReleaseContent();
        _paths = Array.Empty<string>();
        _index = 0;
        _contentSize = null;
        Present();
        ShowLoading();
        TitleText.Text = "正在读取…";
        MetaText.Text = "";
        FooterText.Text = "";
    }

    /// <summary>读取选中项失败或没有可预览的项目时，收起还停留在加载状态的窗口；已经换成真实内容的窗口不受影响。</summary>
    internal void HideIfPending()
    {
        if (_isOpen && _paths.Count == 0) HideWindow();
    }

    /// <summary>
    /// 把窗口放到屏幕上并获得焦点，只做窗口层面的事，不碰内容。
    /// 首次显示先隐藏（cloak）再展示，等第一帧渲染完再取消隐藏，避免白闪。
    /// </summary>
    private void Present()
    {
        try
        {
            var wasOpen = _isOpen;
            if (!wasOpen)
            {
                WindowHelper.SetWindowCloak(this, true);
                PositionWindow();
            }

            _isOpen = true;
            ActivateForeground();

            if (!wasOpen)
            {
                _previewAnimator.Reset();
                _userSized = false;
                _revealed = false;
                _revealQueued = false;
                _animationVersion++;
            }
            KeyCatcher.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.Present", ex);
        }
    }

    /// <summary>
    /// 把窗口激活到前台并获得焦点。资源管理器按下空格时，前台是 Explorer，WinTools 是后台进程，
    /// 直接 SetForegroundWindow 会被系统拒绝：窗口只会显示出来，保持未激活（灰色），也就收不到"失去焦点即关闭"的事件。
    /// 做法是临时把本线程的输入队列挂到前台窗口所在线程上（AttachThreadInput），系统就允许切换前台，完成后立即分离。
    /// </summary>
    private void ActivateForeground()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var currentThread = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
                       && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            Activate();
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.ActivateForeground", ex);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// 启动时预先创建并渲染一次（放在屏幕外，随后隐藏）。WinUI 窗口第一次创建和布局很慢，
    /// 不预热的话第一次按空格就会明显卡住。与悬浮搜索的 EnsureInitialized 是同一做法。
    /// </summary>
    internal void Warmup()
    {
        try
        {
            AppWindow.Move(new PointInt32(-20000, -20000));
            WindowHelper.SetWindowCloak(this, true);
            Activate();
            _previewAnimator.PrepareReveal(AppWindow.Position.Y, CurrentScale());
            var version = _animationVersion;
            WindowHelper.WhenRendered(this, () =>
            {
                if (_isOpen || version != _animationVersion) return;
                UpdateFrameInsets();
                _previewAnimator.Reset();
                AppWindow.Hide();
            }, framesToWait: 4);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.Warmup", ex);
        }
    }

    /// <summary>隐藏窗口，并释放所有解码相关的资源。下次呼出时重新加载。</summary>
    internal void HideWindow(bool immediate = false)
    {
        if (!_isOpen) return;
        _isOpen = false;
        _loadToken++;
        _contentCancellation.Cancel();
        _mediaTimer?.Stop();
        _previewPlayer?.Pause();
        var version = ++_animationVersion;
        _revealQueued = false;
        void Finish()
        {
            if (_isOpen || version != _animationVersion) return;
            try { AppWindow.Hide(); }
            catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.HideWindow", ex); }
            ReleaseContent();
            _revealed = false;
        }
        if (immediate || !_revealed || !AnimationsEnabled) { _previewAnimator.Stop(); Finish(); _previewAnimator.Reset(); }
        else _previewAnimator.PlayDismiss(CurrentScale(), Finish);
    }

    private void ReleaseContent()
    {
        _contentCancellation.Cancel();
        _contentCancellation.Dispose();
        _contentCancellation = new CancellationTokenSource();
        ResetDocumentState();
        try
        {
            ReleaseMedia();
        }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.ReleaseMedia", ex); }

        ImageView.Source = null;
        FallbackIcon.Source = null;
        TextBody.Text = "";

        // PDF：清掉所有页的占位与位图。
        PdfPages.Children.Clear();
        _pdfImages.Clear();
        _pdfTops.Clear();
        _pdfHeights.Clear();
        _pdfAspects.Clear();
        _pdfLayoutWidth = 0;
        _pdf = null;
        _pdfPath = null;
        _pdfPage = 0;

        // Collapse the web surface before navigating away; otherwise about:blank can flash white.
        HtmlView.Visibility = Visibility.Collapsed;
        // HTML：回到空白页，释放网页内容（WebView2 进程本身保留，下次打开不用重新初始化）。
        try
        {
            if (HtmlView.CoreWebView2 != null)
            {
                if (_htmlNavigationCompleted != null) HtmlView.CoreWebView2.NavigationCompleted -= _htmlNavigationCompleted;
                if (_htmlNavigationStarting != null) HtmlView.CoreWebView2.NavigationStarting -= _htmlNavigationStarting;
            }
            _htmlNavigationCompleted = null;
            _htmlNavigationStarting = null;
            if (HtmlView.CoreWebView2 != null) HtmlView.Source = new Uri("about:blank");
        }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.ReleaseHtml", ex); }

    }

    /// <summary>首次呼出：先按默认尺寸居中于光标所在的显示器。内容加载后再由 <see cref="ApplyWindowSize"/> 调整。</summary>
    private void PositionWindow()
    {
        try
        {
            var scale = CurrentScale();
            var work = GetTargetDisplayArea().WorkArea;
            var width = (int)Math.Round(DefaultWidthDip * scale);
            var height = (int)Math.Round(DefaultHeightDip * scale);
            var x = work.X + (work.Width - width) / 2;
            var y = work.Y + (work.Height - height) / 2;
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.PositionWindow", ex);
        }
    }

    private double CurrentScale()
    {
        try
        {
            var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>
    /// 按内容调整窗口大小，窗口中心保持不动（与 QuickLook 一样，小图片就是小窗口）。
    /// 传入内容尺寸按DIP计算；整体等比缩小，计入标题/工具条/播放条，并受当前屏幕工作区限制。
    /// </summary>
    private void ApplyWindowSize(double? contentWidth, double? contentHeight, bool large = false)
    {
        try
        {
            var scale = CurrentScale();
            // 用窗口自己所在的显示器，而不是光标所在的：切换文件时窗口不能被挪到别的屏幕上。
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            if (_userSized) return;
            var maxWidth = Math.Min(1100, work.Width * MaxWidthRatio / scale);
            var maxHeight = work.Height * MaxHeightRatio / scale;
            var chromeHeight = ChromeHeightDip + ToolbarHeight + _frameHeightDip + (Classify(CurrentPath) == Kind.Media ? MediaBarHeightDip : 0);

            double width, height;
            if (contentWidth is > 0 && contentHeight is > 0)
            {
                var fit = Math.Min(1.0, Math.Min(
                    (maxWidth - ChromeWidthDip - _frameWidthDip) / contentWidth.Value,
                    Math.Max(1, maxHeight - chromeHeight) / contentHeight.Value));
                width = contentWidth.Value * fit + ChromeWidthDip + _frameWidthDip;
                height = contentHeight.Value * fit + chromeHeight;
            }
            else
            {
                // large：直接占满上限（网页这类需要大版面的内容）；否则用固定的默认尺寸。
                width = large ? maxWidth : DefaultWidthDip;
                height = large ? maxHeight : DefaultHeightDip;
            }
            // 小屏幕/高缩放时上限可能小于默认最小尺寸，不能让 Clamp 抛异常。
            width = Math.Clamp(width, Math.Min(MinWidthDip, maxWidth), maxWidth);
            height = Math.Clamp(height, Math.Min(MinHeightDip, maxHeight), maxHeight);

            SetPreviewBounds(width, height);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.ApplyWindowSize", ex);
        }
    }

    private static DisplayArea GetTargetDisplayArea()
    {
        try
        {
            if (GetCursorPos(out var point))
            {
                var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
                if (area != null) return area;
            }
        }
        catch { /* ignore */ }
        return DisplayArea.Primary;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    #endregion

    #region 键盘

    private void ShellRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape && new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.Shift, VirtualKey.LeftWindows, VirtualKey.RightWindows }
            .Any(key => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))) return;
        // 按钮与滑条保留自己的键盘交互；Esc 在任何焦点下都可以关闭预览。
        if (e.Key != VirtualKey.Escape && IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (HandleKey(e.Key)) e.Handled = true;
    }

    private bool IsInteractiveSource(DependencyObject? node)
    {
        while (node != null)
        {
            if (node is Slider or TextBox or ComboBox || node is Button button && button != KeyCatcher) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenWithDefaultApp();

    /// <summary>
    /// 窗口级的按键：空格 / Esc 关闭，← → 切换文件，Enter 用默认程序打开。
    /// ↑ ↓ 与 PageUp / PageDown 滚动当前内容，避免同一按键随文件类型变成切换文件。
    /// 返回是否已处理。WebView2 里的按键也走这里（它是独立窗口，按键不经过 PreviewKeyDown）。
    /// </summary>
    private bool HandleKey(VirtualKey key)
    {
        if (!_isOpen) return false;

        var isPdf = _pdfPath != null && string.Equals(_pdfPath, CurrentPath, StringComparison.OrdinalIgnoreCase);
        switch (key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Space:
                HideWindow();
                return true;
            case VirtualKey.Left:
                _ = StepFileAsync(-1);
                return true;
            case VirtualKey.Right:
                _ = StepFileAsync(1);
                return true;
            case VirtualKey.Enter:
                OpenWithDefaultApp();
                return true;
            case VirtualKey.Up or VirtualKey.PageUp when isPdf:
                ScrollPdf(key == VirtualKey.PageUp ? -PdfView.ViewportHeight * 0.9 : -60);
                return true;
            case VirtualKey.Down or VirtualKey.PageDown when isPdf:
                ScrollPdf(key == VirtualKey.PageDown ? PdfView.ViewportHeight * 0.9 : 60);
                return true;
            case VirtualKey.Up or VirtualKey.PageUp when TextView.Visibility == Visibility.Visible:
                TextView.ChangeView(null, Math.Max(0, TextView.VerticalOffset - (key == VirtualKey.PageUp ? TextView.ViewportHeight * 0.9 : 60)), null);
                return true;
            case VirtualKey.Down or VirtualKey.PageDown when TextView.Visibility == Visibility.Visible:
                TextView.ChangeView(null, TextView.VerticalOffset + (key == VirtualKey.PageDown ? TextView.ViewportHeight * 0.9 : 60), null);
                return true;
            case VirtualKey.Up or VirtualKey.PageUp:
            case VirtualKey.Down or VirtualKey.PageDown:
                return ScrollActiveContent(key);
            default:
                return false;
        }
    }

    private void ScrollPdf(double delta)
    {
        PdfView.ChangeView(null, Math.Max(0, PdfView.VerticalOffset + delta), null);
    }

    private Task StepFileAsync(int delta)
    {
        if (_paths.Count <= 1) return Task.CompletedTask;
        _index = (_index + delta + _paths.Count) % _paths.Count;
        return LoadCurrentAsync();
    }

    private void OpenWithDefaultApp()
    {
        var path = CurrentPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            HideWindow();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.OpenWithDefaultApp", ex);
        }
    }

    #endregion

    #region 加载内容

    private static Kind Classify(string path)
    {
        if (Directory.Exists(path)) return Kind.Folder;

        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return Kind.Image;
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)) return Kind.Pdf;
        if (HtmlExtensions.Contains(extension)) return Kind.Html;
        if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase)) return Kind.OfficeText;
        if (StructuredExtensions.Contains(extension)) return Kind.StructuredText;
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)) return Kind.Archive;
        if (MediaExtensions.Contains(extension)) return Kind.Media;
        if (UnsupportedExtensions.Contains(extension)) return Kind.Unsupported;
        // 其余（含没有扩展名）才需要看内容：ReadTextAsync 先只读一小段判断是不是文本。
        return Kind.Text;
    }

    private async Task LoadCurrentAsync()
    {
        var token = ++_loadToken;
        var path = CurrentPath;
        if (string.IsNullOrEmpty(path)) return;

        ReleaseContent();
        _contentSize = null;
        ShowLoading();
        UpdateHeader(path);
        UpdateFooter(path);
        ConfigurePresentation(path);

        try
        {
            switch (Classify(path))
            {
                case Kind.Folder:
                    ShowFolder(path, token);
                    break;

                case Kind.Image:
                    if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                    {
                        await LoadSvgAsync(path, token);
                        break;
                    }
                    // 同一路径可能已被覆盖；绕过 WinUI 的 URI 图片缓存。
                    var bitmap = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache };
                    bitmap.ImageOpened += (_, _) => OnContentSized(token, path, bitmap.PixelWidth, bitmap.PixelHeight);
                    bitmap.ImageFailed += (_, e) => { if (token == _loadToken) ImageView_ImageFailed(ImageView, e); };
                    ImageView.Source = bitmap;
                    ShowOnly(ImageSurface);
                    bitmap.UriSource = ToFileUri(path);
                    break;

                case Kind.Pdf:
                    await LoadPdfAsync(path, token);
                    break;

                case Kind.Html:
                    await LoadHtmlAsync(path, token);
                    break;

                case Kind.OfficeText:
                    if (path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        var cancellation = _contentCancellation.Token;
                        var workbook = await Task.Run(() => OfficeTextExtractor.ReadWorkbook(path, cancellation));
                        if (token != _loadToken) return;
                        ShowWorkbook(workbook);
                        break;
                    }
                    var officeText = await Task.Run(() => OfficeTextExtractor.Extract(path));
                    if (token != _loadToken) return;
                    if (officeText == null)
                    {
                        ShowUnsupported(path);
                        return;
                    }
                    SetTextPresentation(wrap: true, "Segoe UI");
                    TextBody.Text = officeText;
                    ShowOnly(TextView);
                    ViewStatus.Text = path.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase) ? "逐页文字大纲" : "文字预览";
                    FitTextWindow(760);
                    break;

                case Kind.StructuredText:
                    if (Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".tsv")
                    {
                        try
                        {
                            var table = await Task.Run(() => PreviewDataReader.ReadDelimited(path));
                            if (token != _loadToken) return;
                            ShowWorkbook(new[] { table });
                        }
                        catch (FormatException ex)
                        {
                            if (token != _loadToken) return;
                            await ShowRawTextAsync(path, token, ex.Message);
                        }
                        break;
                    }
                    var structuredText = await Task.Run(() => StructuredPreviewExtractor.Extract(path));
                    if (token != _loadToken) return;
                    if (structuredText == null)
                    {
                        await ShowRawTextAsync(path, token, "原文预览：文件较大或格式解析失败");
                        return;
                    }
                    SetTextPresentation(wrap: false, "Cascadia Mono");
                    if (structuredText.Length > MaxTextBytes)
                        structuredText = structuredText[..MaxTextBytes] + "\n\n… 格式化内容较长，仅显示前一部分。";
                    TextBody.Text = structuredText;
                    _rawText = structuredText;
                    ShowOnly(TextView);
                    FitTextWindow(860);
                    break;

                case Kind.Archive:
                    await LoadEntriesAsync(path, token, archive: true);
                    break;

                case Kind.Media:
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    if (token != _loadToken) return;
                    if (IsAudio(path))
                    {
                        AudioInfo.Visibility = Visibility.Visible;
                        AudioTitle.Text = Path.GetFileNameWithoutExtension(path);
                        MediaView.Visibility = Visibility.Collapsed;
                        SetPreviewBounds(540, 280);
                    }
                    else
                    {
                        AudioInfo.Visibility = Visibility.Collapsed;
                        MediaView.Visibility = Visibility.Visible;
                        try
                        {
                            var properties = await file.Properties.GetVideoPropertiesAsync();
                            if (token != _loadToken) return;
                            if (properties.Width > 0 && properties.Height > 0) ApplyWindowSize(properties.Width, properties.Height);
                        }
                        catch { /* Decoder reports natural dimensions after opening. */ }
                    }
                    ShowOnly(MediaSurface);
                    OpenMedia(file, token, path);
                    break;

                case Kind.Text:
                    var text = await ReadTextAsync(path);
                    if (token != _loadToken) return;
                    if (text == null)
                    {
                        // 快捷方式的 .lnk 是二进制，单独说明，免得被误以为是损坏的文件。
                        var isShortcut = string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);
                        ShowFallback(path, isShortcut ? "快捷方式" : "二进制文件", "没有可用的预览器。按 Enter 用默认程序打开。");
                        return;
                    }
                    _rawText = text;
                    if (IsMarkdown(path))
                    {
                        var blocks = await Task.Run(() => MarkdownPreviewParser.Parse(text));
                        if (token != _loadToken) return;
                        RenderMarkdown(blocks);
                        break;
                    }
                    var readable = IsReadableText(path);
                    SetTextPresentation(readable, readable ? "Segoe UI" : "Consolas");
                    TextBody.Text = text;
                    ShowOnly(TextView);
                    FitTextWindow(readable ? 720 : 860);
                    break;

                default:
                    ShowUnsupported(path);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            if (token == _loadToken) ShowFallback(path, "没有读取权限", "无法读取此文件。可用默认程序打开。");
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"FilePreviewWindow.Load({path})", ex);
            if (token == _loadToken)
                ShowFallback(path, "无法预览", "文件可能损坏或有密码保护。按 Enter 用默认程序打开。");
        }
    }

    private void SetTextPresentation(bool wrap, string font)
    {
        // 仅设置 TextWrapping 无效：横向滚动启用时，ScrollViewer 会以无限宽度测量正文。
        TextView.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        TextView.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        TextBody.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextBody.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(font);
    }

    /// <summary>媒体解码就绪后确定尺寸；淡入结束再开始播放，避免播放启动与揭示争抢绘制。</summary>
    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentMedia(sender)) return;
            var session = sender.PlaybackSession;
            _mediaReady = true;
            var width = session.NaturalVideoWidth;
            var height = session.NaturalVideoHeight;
            UpdateMediaProgress();
            UpdateHeader(_mediaPath);
            if (width > 0 && height > 0)
                OnContentSized(_mediaLoadToken, _mediaPath, (int)width, (int)height);
            else
                SetPreviewBounds(540, 220);
            ShowOnly(MediaSurface);
            RevealWhenReady();
            if (_revealed && !_previewAnimator.IsBusy) TryStartMediaPlayback();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        ErrorReporter.Log("FilePreviewWindow.MediaFailed", args.ErrorMessage ?? args.Error.ToString());
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsCurrentMedia(sender))
                ShowFallback(CurrentPath, "无法播放此文件", "缺少系统解码器。按 Enter 用默认程序打开。");
        });
    }

    /// <summary>
    /// 打开 PDF：先为每一页建一个按页面比例占好位置的 Image（不渲染），再让视口附近的页按需渲染。
    /// 窗口按第一页的大小定尺寸。
    /// </summary>
    private async Task LoadPdfAsync(string path, int token)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (token != _loadToken) return;

        _pdf = document;
        _pdfPath = path;
        _pdfPage = 0;

        PdfPages.Children.Clear();
        _pdfImages.Clear();
        _pdfTops.Clear();
        _pdfHeights.Clear();
        _pdfAspects.Clear();
        _pdfLayoutWidth = 0;

        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var size = page.Size;
            _pdfAspects.Add(size.Width > 0 ? size.Height / size.Width : 1.414);
            _pdfImages.Add(new Image { Stretch = Stretch.Uniform, Tag = (int)i });
            PdfPages.Children.Add(_pdfImages[^1]);
            _pdfTops.Add(0);
            _pdfHeights.Add(0);

            // 第一页决定窗口尺寸；窗口调整后 PdfView.SizeChanged 会触发重新排版，页面宽度跟着内容区走。
            if (i == 0) ApplyWindowSize(size.Width, size.Height);
        }

        ShowOnly(PdfView);
        PdfView.ChangeView(null, 0, null, disableAnimation: true);
        UpdateFooter(path);
        RelayoutPdf();
    }

    /// <summary>
    /// 页面宽度 = 滚动视图的实际宽度（去掉滚动条余量）。宽度变了就重新算每页的位置和高度，
    /// 已渲染的页作废（清空位图），由视口逻辑按新尺寸重新渲染。
    /// </summary>
    private void RelayoutPdf()
    {
        if (_pdf == null || _pdfImages.Count == 0) return;

        var available = PdfView.ActualWidth > 0 ? PdfView.ActualWidth : ContentHost.ActualWidth;
        var width = Math.Max(200, available - 16);
        if (Math.Abs(width - _pdfLayoutWidth) < 0.5) return;
        _pdfLayoutWidth = width;

        double top = 0;
        for (var i = 0; i < _pdfImages.Count; i++)
        {
            var height = width * _pdfAspects[i];
            var image = _pdfImages[i];
            image.Width = width;
            image.Height = height;
            image.Source = null;
            _pdfTops[i] = top;
            _pdfHeights[i] = height;
            top += height + PdfPages.Spacing;
        }

        UpdatePdfViewport();
    }

    private void PdfView_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdatePdfViewport();

    /// <summary>
    /// 根据滚动位置决定渲染哪些页：视口上下各多留一屏，提前渲染，滚动起来不空白。
    /// 离视口很远的页释放位图，所以不论 PDF 多少页，内存只跟视口附近的页数有关。
    /// </summary>
    private void UpdatePdfViewport()
    {
        if (_pdf == null || _pdfImages.Count == 0 || !PdfView.IsLoaded) return;

        var viewTop = PdfView.VerticalOffset / PdfView.ZoomFactor;
        var viewBottom = viewTop + PdfView.ViewportHeight / PdfView.ZoomFactor;
        var margin = PdfView.ViewportHeight / PdfView.ZoomFactor;

        var firstVisible = -1;
        for (var i = 0; i < _pdfImages.Count; i++)
        {
            var top = _pdfTops[i];
            var bottom = top + _pdfHeights[i];
            var nearView = bottom >= viewTop - margin && top <= viewBottom + margin;
            var veryFar = bottom < viewTop - margin * 2 || top > viewBottom + margin * 2;

            if (firstVisible < 0 && bottom >= viewTop) firstVisible = i;

            if (nearView && _pdfImages[i].Source == null)
                StartRenderingPdf();
            else if (veryFar && _pdfImages[i].Source != null)
                _pdfImages[i].Source = null;
        }

        if (firstVisible >= 0 && firstVisible != _pdfPage)
        {
            _pdfPage = firstVisible;
            UpdateFooter(_pdfPath ?? CurrentPath);
        }
    }

    /// <summary>
    /// 逐页渲染视口附近还没渲染的页。同一时间只跑一个循环，循环每渲染完一页都重新计算视口，
    /// 所以滚动过程中新进入视口的页会接着被渲染。UI 线程是单线程的，不存在两个循环同时进入的情况。
    /// </summary>
    private void StartRenderingPdf()
    {
        if (_pdfRendering) return;
        _ = RenderVisiblePdfPagesAsync();
    }

    private async Task RenderVisiblePdfPagesAsync()
    {
        _pdfRendering = true;
        try
        {
            int next;
            while ((next = FirstUnrenderedNearView()) >= 0)
            {
                if (!await RenderPdfPageAsync(next)) break;
            }
        }
        finally
        {
            _pdfRendering = false;
        }
    }

    /// <summary>渲染单页，按内容区的实际宽度（物理像素）输出。返回 false 表示内容已经切换，应停止。</summary>
    private async Task<bool> RenderPdfPageAsync(int index)
    {
        var token = _loadToken;
        try
        {
            if (_pdf == null || index >= _pdfImages.Count) return false;

            var scale = ContentHost.XamlRoot?.RasterizationScale ?? 1.0;
            var targetWidth = (uint)Math.Clamp(_pdfLayoutWidth * scale, 480, 3072);

            using var page = _pdf.GetPage((uint)index);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = targetWidth });
            stream.Seek(0);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (token != _loadToken) return false;

            _pdfImages[index].Source = bitmap;
            return true;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"FilePreviewWindow.PdfPage({index})", ex);
            return false;
        }
    }

    /// <summary>视口附近第一个还没渲染的页，没有则返回 -1。</summary>
    private int FirstUnrenderedNearView()
    {
        var viewTop = PdfView.VerticalOffset / PdfView.ZoomFactor;
        var viewBottom = viewTop + PdfView.ViewportHeight / PdfView.ZoomFactor;
        for (var i = 0; i < _pdfImages.Count; i++)
        {
            if (_pdfTops[i] + _pdfHeights[i] < viewTop) continue;
            if (_pdfTops[i] > viewBottom) break;
            if (_pdfImages[i].Source == null) return i;
        }
        return -1;
    }

    /// <summary>
    /// HTML 用 WebView2 渲染（与 Edge 同一内核）。WebView2 是独立的子窗口，按键不会经过窗口的 PreviewKeyDown，
    /// 所以用 AcceleratorKeyPressed 把空格 / Esc / 方向键转回 <see cref="HandleKey"/>。
    /// 弹窗与新窗口请求被拦截，避免网页把预览窗口之外的东西打开。
    /// </summary>
    private async Task LoadHtmlAsync(string path, int token)
    {
        try
        {
            await HtmlView.EnsureCoreWebView2Async();
            if (token != _loadToken) return;

            var core = HtmlView.CoreWebView2;
            if (core == null)
            {
                ShowFallback(path, "无法显示网页", "需要 WebView2 运行时。按 Enter 用默认程序打开。");
                return;
            }

            if (!_htmlHooked)
            {
                core.NewWindowRequested += (_, args) => args.Handled = true;
                // 网页获得焦点后按键不再经过窗口；注入脚本把预览用到的键转发回来。必须在导航前注入。
                await core.AddScriptToExecuteOnDocumentCreatedAsync(HtmlKeyScript);
                HtmlView.WebMessageReceived += (_, args) => OnHtmlKeyMessage(args.TryGetWebMessageAsString());
                _htmlHooked = true;
            }

            HtmlView.DefaultBackgroundColor = ThemeService.EffectiveTheme == ElementTheme.Dark
                ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250);
            if (_htmlNavigationCompleted != null) core.NavigationCompleted -= _htmlNavigationCompleted;
            if (_htmlNavigationStarting != null) core.NavigationStarting -= _htmlNavigationStarting;
            ulong navigationId = 0;
            var targetUri = ToFileUri(path);
            _htmlNavigationStarting = (_, args) =>
            {
                if (_isOpen && token == _loadToken && string.Equals(args.Uri, targetUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)) navigationId = args.NavigationId;
            };
            _htmlNavigationCompleted = (_, args) =>
            {
                if (!_isOpen || token != _loadToken || navigationId == 0 || args.NavigationId != navigationId) return;
                core.NavigationCompleted -= _htmlNavigationCompleted;
                core.NavigationStarting -= _htmlNavigationStarting;
                _htmlNavigationCompleted = null;
                _htmlNavigationStarting = null;
                if (args.IsSuccess) { ShowOnly(HtmlView); RevealWhenReady(); }
                else ShowFallback(path, "网页加载失败", "用默认程序打开以查看此文件。");
            };
            core.NavigationStarting += _htmlNavigationStarting;
            core.NavigationCompleted += _htmlNavigationCompleted;
            HtmlView.Source = targetUri;
            SetPreviewBounds(900, 640);
            _ = CheckHtmlTimeoutAsync(token, _contentCancellation.Token);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"FilePreviewWindow.Html({path})", ex);
            if (token == _loadToken)
                ShowFallback(path, "无法显示网页", "需要 WebView2 运行时。按 Enter 用默认程序打开。");
        }
    }

    private bool _htmlHooked;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs>? _htmlNavigationCompleted;
    private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs>? _htmlNavigationStarting;

    private async Task CheckHtmlTimeoutAsync(int token, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation);
            if (_isOpen && token == _loadToken && _htmlNavigationCompleted != null)
                ShowFallback(CurrentPath, "网页加载超时", "用默认程序打开以查看此文件。");
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// 注入到网页的脚本：只拦截预览需要的键（空格、Esc、← → 、Enter），其余按键（包括 ↑ ↓ 滚动）保持网页原样。
    /// </summary>
    private const string HtmlKeyScript = @"
document.addEventListener('keydown', function (e) {
  var target = e.target;
  if (e.key !== 'Escape' && target && (target.isContentEditable || target.closest('input,textarea,select,button,a,[role=""slider""]'))) return;
  if (e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
  var keys = ['Escape', ' ', 'ArrowLeft', 'ArrowRight', 'Enter'];
  if (keys.indexOf(e.key) >= 0) {
    e.preventDefault();
    window.chrome.webview.postMessage(e.key);
  }
}, true);";

    private void OnHtmlKeyMessage(string? key)
    {
        var mapped = key switch
        {
            "Escape" => VirtualKey.Escape,
            " " => VirtualKey.Space,
            "ArrowLeft" => VirtualKey.Left,
            "ArrowRight" => VirtualKey.Right,
            "Enter" => VirtualKey.Enter,
            _ => VirtualKey.None,
        };
        if (mapped != VirtualKey.None) HandleKey(mapped);
    }

    /// <summary>无法预览的类型：不读内容，直接显示类型名和提示。图标仍然在后台取，取到再填。</summary>
    private void ShowUnsupported(string path)
    {
        var extension = Path.GetExtension(path);
        var isShortcut = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);
        var title = isShortcut ? "快捷方式" : $"{extension.TrimStart('.').ToUpperInvariant()} 文件";
        ShowFallback(path, title, "这类文件没有可用的预览器。按 Enter 用默认程序打开。");
    }

    /// <summary>
    /// 读取前 <see cref="MaxTextBytes"/> 字节并解码；内容像二进制（含 NUL 字节）时返回 null。
    /// 先只读前 4KB 判断是不是二进制：二进制直接返回，不再读后面的内容。
    /// </summary>
    private static async Task<string?> ReadTextAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

        var length = (int)Math.Min(stream.Length, MaxTextBytes);
        var buffer = new byte[length];

        // UTF-16 文本允许含 NUL；其它情况下前 4KB 有 NUL 就视为二进制。
        var probeLength = Math.Min(length, 4096);
        var read = await ReadFullyAsync(stream, buffer, 0, probeLength);
        var utf16 = read >= 2 && ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF));
        if (!utf16)
        {
            for (var i = 0; i < read; i++)
                if (buffer[i] == 0) return null;
        }

        read += await ReadFullyAsync(stream, buffer, read, length - read);

        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync();
        if (stream.Length > MaxTextBytes)
            text += $"\n\n… 仅显示前 {MaxTextBytes / 1024} KB（文件共 {FormatSize(stream.Length)}）";
        return text;
    }

    /// <summary>读满 <paramref name="count"/> 字节或读到文件末尾，返回实际读到的字节数。</summary>
    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private void ImageView_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ErrorReporter.Log("FilePreviewWindow.ImageFailed", e.ErrorMessage);
        ShowFallback(CurrentPath, "无法显示此图片", "系统没有这个格式的解码器。按 Enter 用默认程序打开。");
    }

    private void ShowFallback(string path, string title, string hint)
    {
        var token = ++_loadToken;
        ReleaseContent();
        _contentSize = null;
        FallbackTitle.Text = title;
        FallbackHint.Text = hint;
        ShowOnly(FallbackPanel);
        UpdateHeader(path);
        UpdateFooter(path);
        SetPreviewBounds(520, 280);
        _ = LoadFallbackIconAsync(path, token);
    }

    /// <summary>图标沿用卡片的 CardItem 解析（带缓存），文件夹、可执行文件等都能拿到真实图标。</summary>
    private async Task LoadFallbackIconAsync(string path, int token)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var item = new CardItem(path);
            await item.LoadIconAsync();
            if (token == _loadToken) FallbackIcon.Source = item.Icon;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.FallbackIcon", ex);
        }
    }

    #endregion

    #region 界面状态

    private void ShowLoading()
    {
        ShowOnly(null);
        var token = _loadToken;
        _ = ShowDelayedLoadingAsync(token);
    }

    /// <summary>内容区只保留 <paramref name="view"/> 可见；传 null 表示只显示加载环。</summary>
    private void ShowOnly(UIElement? view)
    {
        foreach (var element in _views)
            element.Visibility = ReferenceEquals(element, view) ? Visibility.Visible : Visibility.Collapsed;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        if (view != null && view != ImageSurface && view != HtmlView && view != MediaSurface) RevealWhenReady();
    }

    /// <summary>标题栏：文件名 + 大小；图片、视频再加像素尺寸（QuickLook 的显示方式）。</summary>
    private void UpdateHeader(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        TitleText.Text = Path.GetFileName(path);
        ToolTipService.SetToolTip(TitleText, path);
        var parts = new List<string>();
        parts.Add(Directory.Exists(path) ? "文件夹" : Path.GetExtension(path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? $"{ext} 文件" : "文件");
        if (!Directory.Exists(path))
        {
            parts.Add(FormatSize(new FileInfo(path).Length));
        }
        if (_contentSize is { } size) parts.Add($"{size.Width} × {size.Height}");
        if (_previewPlayer?.PlaybackSession.NaturalDuration.TotalSeconds is > 0 and var seconds)
            parts.Add(FormatMediaTime(seconds));
        var modified = Directory.Exists(path) ? new DirectoryInfo(path).LastWriteTime : new FileInfo(path).LastWriteTime;
        if (Directory.Exists(path) && _folderStats is { } stats) parts.Add(FormatSize(stats.Bytes));
        _baseMetaText = string.Join(" · ", parts);
        _modifiedText = $"修改于 {modified:yyyy-MM-dd HH:mm}";
        UpdateCompactHeader();
    }

    /// <summary>页脚放 PDF 页码和文件序号，修改时间统一在标题信息行显示。</summary>
    private void UpdateFooter(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var position = _paths.Count > 1 ? $"{_index + 1} / {_paths.Count}" : "";
        var pages = _pdfPath != null && string.Equals(_pdfPath, path, StringComparison.OrdinalIgnoreCase) && _pdf != null
            ? $"   第 {_pdfPage + 1} / {_pdf.PageCount} 页"
            : "";
        FooterText.Text = pages.Trim() + (pages.Length > 0 && position.Length > 0 ? "   ·   " : "") + position;
        UpdateShortcutHints();
    }

    /// <summary>图片 / 视频的像素尺寸已知：更新标题栏并按尺寸调整窗口。只接受仍是当前文件的结果。</summary>
    private void OnContentSized(int token, string path, int width, int height)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isOpen || token != _loadToken) return;
            _contentSize = (width, height);
            UpdateHeader(path);
            ApplyWindowSize(width, height);
            FitImageSurface();
            if (ImageSurface.Visibility == Visibility.Visible) ShowOnly(ImageSurface);
            RevealWhenReady();
        });
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB",
    };

    private static Uri ToFileUri(string path) =>
        new(path.Replace("%", "%25").Replace("#", "%23").Replace("?", "%3F"));

    #endregion
}

/// <summary>
/// 文件预览的单例入口：卡片按键只关心「切换预览」，不用管窗口怎么建、怎么注册。
/// </summary>
internal static class FilePreviewService
{
    private static FilePreviewWindow? _window;

    /// <summary>桌面分区卡片里的空格预览开关（设置页控制）。资源管理器的开关在 <see cref="ExplorerPreviewService.Enabled"/>。</summary>
    internal static bool CardPreviewEnabled { get; set; } = true;

    /// <summary>启动时在屏幕外创建并渲染一次窗口，第一次按空格就不用现场创建。</summary>
    internal static void Warmup()
    {
        (_window ??= Create()).Warmup();
#if DEBUG
        _window.StartPreviewLayoutCapture();
#endif
    }

    /// <summary>资源管理器按下空格的第一步：马上弹出加载中的窗口，不等选中项读取完成。</summary>
    internal static void ShowPending()
    {
        (_window ??= Create()).ShowPending();
    }

    /// <summary>选中项读取完成后调用：有项目就切到第一个并加载内容；没有就收起还停在加载状态的窗口。</summary>
    internal static void ShowSelection(IReadOnlyList<string> paths)
    {
        if (_window is not { } window) return;
        if (paths.Count == 0)
            window.HideIfPending();
        else
            window.ShowAt(paths, 0);
    }

    /// <summary>
    /// 空格的语义：没开就呼出；已经在看这个文件就收起；看的是别的文件就切过去。
    /// <paramref name="paths"/> 是当前卡片里所有可预览项目（按显示顺序），左右键在其中切换。
    /// </summary>
    public static void Toggle(IReadOnlyList<string> paths, int index)
    {
        if (paths.Count == 0 || index < 0 || index >= paths.Count) return;

        var window = _window ??= Create();
        if (window.IsOpen && string.Equals(window.CurrentPath, paths[index], StringComparison.OrdinalIgnoreCase))
        {
            window.HideWindow();
            return;
        }
        window.ShowAt(paths, index);
    }

    private static FilePreviewWindow Create()
    {
        var window = new FilePreviewWindow();
        // 登记给各子系统（例如输入法切换），与悬浮搜索一样不让 WinTools 自己的窗口触发按程序切换。
        (App.Current as IWindowRegistry)?.Register(window);
        return window;
    }
}
