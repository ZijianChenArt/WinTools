using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Microsoft.UI.Text;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private readonly PopupAnimator _previewAnimator;
    private readonly Windows.UI.ViewManagement.UISettings _animationSettings = new();
    private CancellationTokenSource _contentCancellation = new();
    private int _animationVersion;
    private bool _revealQueued;
    private bool _revealed;
    private bool _userSized;
    private int _automaticWidth, _automaticHeight;
    private double _frameWidthDip = 16, _frameHeightDip = 16;
    private (double Width, double Height)? _pendingBounds;
    private string _rawText = "";
    private IReadOnlyList<MarkdownPreviewBlock>? _markdownBlocks;
    private IReadOnlyList<PreviewTable> _workbook = Array.Empty<PreviewTable>();
    private IReadOnlyList<PreviewEntry> _archiveEntries = Array.Empty<PreviewEntry>();
    private string _archivePrefix = "";
    private string _listingFolder = "";
    private bool _isArchive;
    private int _directoryVersion;
    private bool _sourceMode;
    private double ToolbarHeight => 0;
    private string _baseMetaText = "", _modifiedText = "";
    private PreviewFolderStats? _folderStats;
    private bool AnimationsEnabled => _animationSettings.AnimationsEnabled;
    private static Style PreviewStyle(string name) => (Style)Application.Current.Resources[name];
    private static bool IsMarkdown(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";
    private static bool IsReadableText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".markdown" or ".srt" or ".vtt" or ".rst" or ".text";
    private static bool IsAudio(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".wav" or ".flac" or ".aac" or ".wma";

    private void ResetDocumentState()
    {
        _directoryVersion++;
        _pendingBounds = null;
        _rawText = "";
        _markdownBlocks = null;
        _workbook = Array.Empty<PreviewTable>();
        _archiveEntries = Array.Empty<PreviewEntry>();
        _archivePrefix = "";
        _listingFolder = "";
        _sourceMode = false;
        _folderStats = null;
        _baseMetaText = _modifiedText = "";
        ReadingBody.Children.Clear();
        EntryList.ItemsSource = null;
        TableRows.ItemsSource = null;
        TableRows.ItemTemplate = null;
        TableHeader.Children.Clear();
        TableHeader.ColumnDefinitions.Clear();
        SheetSelector.ItemsSource = null;
        ViewStatus.Text = "";
        EntryEmpty.Visibility = Visibility.Collapsed;
        TableEmpty.Visibility = Visibility.Collapsed;
        PreviewToolbar.Visibility = Visibility.Collapsed;
        foreach (var view in _views) view.Visibility = Visibility.Collapsed;
        ImageSurface.ChangeView(0, 0, 1, true);
        PdfView.ChangeView(0, 0, 1, true);
        TextView.ChangeView(0, 0, null, true);
        ReadingView.ChangeView(0, 0, null, true);
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
    }

    private void ConfigurePresentation(string path)
    {
        foreach (var control in new FrameworkElement[] { SourceButton, WrapButton, ListUpButton, SheetSelector, ZoomTools })
            control.Visibility = Visibility.Collapsed;
        SourceButton.Content = "源码";
        WrapButton.Content = "自动换行";
        var kind = Classify(path);
        var width = 540d;
        var height = 300d;
        switch (kind)
        {
            case Kind.Image: case Kind.Pdf:
                ZoomTools.Visibility = Visibility.Visible;
                width = 720; height = 560; break;
            case Kind.Html: width = 900; height = 640; break;
            case Kind.Text:
                SourceButton.Visibility = IsMarkdown(path) ? Visibility.Visible : Visibility.Collapsed;
                WrapButton.Visibility = IsMarkdown(path) ? Visibility.Collapsed : Visibility.Visible;
                width = IsReadableText(path) ? 760 : 860; height = 420; break;
            case Kind.StructuredText:
                WrapButton.Visibility = Path.GetExtension(path).ToLowerInvariant() is ".json" or ".xml" ? Visibility.Visible : Visibility.Collapsed;
                width = 860; height = 420; break;
            case Kind.OfficeText:
                width = path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? 900 : 760; height = 440; break;
            case Kind.Archive:
                ListUpButton.Visibility = Visibility.Visible;
                ListUpButton.IsEnabled = false;
                width = 620; height = 320; break;
            case Kind.Folder: width = 640; height = 440; break;
            case Kind.Media:
                width = IsAudio(path) ? 540 : 760; height = IsAudio(path) ? 220 : 520; break;
        }
        PreviewToolbar.Visibility = kind is Kind.Text or Kind.StructuredText or Kind.OfficeText or Kind.Archive or Kind.Image or Kind.Pdf
            ? Visibility.Visible : Visibility.Collapsed;
        SetPreviewBounds(width, height);
    }

    private void UpdateCompactHeader()
    {
        if (MetaText == null || TitleText == null || HeaderHelp == null || HeaderOpen == null) return;
        var pieces = new[] { _baseMetaText, ViewStatus.Text, FooterText.Text }.Where(s => !string.IsNullOrWhiteSpace(s));
        var details = string.Join(" · ", pieces);
        MetaText.Text = details;
        var width = HeaderBar.ActualWidth;
        var available = width - PreviewToolbar.ActualWidth - HeaderHelp.ActualWidth - HeaderOpen.ActualWidth - 200;
        MetaText.Visibility = available >= 96 ? Visibility.Visible : Visibility.Collapsed;
        MetaText.MaxWidth = Math.Max(0, Math.Min(320, available));
        ToolTipService.SetToolTip(MetaText, details + "\n" + _modifiedText);
        ToolTipService.SetToolTip(TitleText, CurrentPath + "\n" + details + "\n" + _modifiedText + "\n空格 / Esc 关闭 · Enter 打开");
    }

    private void ShowFolder(string path, int token)
    {
        FallbackTitle.Text = Path.GetFileName(path.TrimEnd('\\', '/'));
        FallbackHint.Text = "正在计算文件夹大小…";
        PreviewToolbar.Visibility = Visibility.Collapsed;
        ShowOnly(FallbackPanel);
        SetPreviewBounds(640, 440);
        _ = LoadFallbackIconAsync(path, token);
        _ = LoadFolderStatsAsync(path, token, _contentCancellation.Token);
    }

    private async Task LoadFolderStatsAsync(string path, int token, CancellationToken cancellation)
    {
        try
        {
            var stats = await Task.Run(() => PreviewDataReader.ReadFolderStats(path, cancellation));
            if (!_isOpen || token != _loadToken) return;
            _folderStats = stats;
            UpdateHeader(path);
            FallbackHint.Text = $"含 {stats.Directories} 个目录和 {stats.Files} 个文件\n按 Enter 或点击“打开”进入文件夹。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorReporter.Log("FilePreviewWindow.FolderStats", ex);
            if (token == _loadToken) FallbackHint.Text = "无法统计此文件夹（可能没有访问权限）。按 Enter 用资源管理器打开。";
        }
    }

    private void SetPreviewBounds(double width, double height)
    {
        if (_userSized || !_isOpen) return;
        if (_previewAnimator.IsBusy) { _pendingBounds = (width, height); return; }
        var scale = CurrentScale();
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        var fit = PreviewLayoutPolicy.Fit(width, height, work.Width / scale, work.Height / scale);
        var pixelWidth = (int)Math.Round(fit.Width * scale);
        var pixelHeight = (int)Math.Round(fit.Height * scale);
        var old = AppWindow.Size;
        var origin = AppWindow.Position;
        var x = Math.Clamp(origin.X + (old.Width - pixelWidth) / 2, work.X, Math.Max(work.X, work.X + work.Width - pixelWidth));
        var y = Math.Clamp(origin.Y + (old.Height - pixelHeight) / 2, work.Y, Math.Max(work.Y, work.Y + work.Height - pixelHeight));
        _automaticWidth = pixelWidth; _automaticHeight = pixelHeight;
        if (pixelWidth != old.Width || pixelHeight != old.Height || x != origin.X || y != origin.Y)
            AppWindow.MoveAndResize(new RectInt32(x, y, pixelWidth, pixelHeight));
    }

    private double MeasureWidth(double preferred)
    {
        var scale = CurrentScale();
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        return PreviewLayoutPolicy.Fit(preferred, 400, work.Width / scale, work.Height / scale).Width;
    }

    private void UpdateFrameInsets()
    {
        if (ShellRoot.ActualWidth <= 0 || ShellRoot.ActualHeight <= 0) return;
        var scale = CurrentScale();
        var width = AppWindow.Size.Width / scale - ShellRoot.ActualWidth;
        var height = AppWindow.Size.Height / scale - ShellRoot.ActualHeight;
        if (width is >= 0 and <= 48) _frameWidthDip = width;
        if (height is >= 0 and <= 48) _frameHeightDip = height;
    }

    private void FitTextWindow(double preferred)
    {
        var width = MeasureWidth(preferred);
        TextBody.Measure(new Size(Math.Max(1, width - ChromeWidthDip - _frameWidthDip - 2), double.PositiveInfinity));
        var scrollAllowance = TextBody.TextWrapping == TextWrapping.NoWrap ? 16 : 0;
        SetPreviewBounds(width, Math.Min(760, TextBody.DesiredSize.Height + ChromeHeightDip + ToolbarHeight + _frameHeightDip + scrollAllowance + 4));
        WrapButton.Content = TextBody.TextWrapping == TextWrapping.Wrap ? "取消换行" : "自动换行";
        if (TextBody.Text.Length == 0) { TextBody.Text = "此文件为空。"; ViewStatus.Text = "空文件"; }
    }

    private async Task ShowDelayedLoadingAsync(int token)
    {
        await Task.Delay(120);
        if (!_isOpen || token != _loadToken || _views.Any(v => v.Visibility == Visibility.Visible && v != ImageSurface && v != HtmlView && v != MediaSurface)
            || ImageSurface.Visibility == Visibility.Visible && _contentSize != null
            || MediaSurface.Visibility == Visibility.Visible && _mediaReady) return;
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        RevealWhenReady();
    }

    private void RevealWhenReady()
    {
        if (!_isOpen || _revealed || _revealQueued) return;
        _revealQueued = true;
        var version = _animationVersion;
        WindowHelper.WhenRendered(this, () =>
        {
            if (!_isOpen || version != _animationVersion) return;
            UpdateFrameInsets();
            _revealQueued = false;
            // Prime the real DWM surface at an imperceptible alpha before starting the animation clock.
            // Otherwise the first visible frame can spend >100ms initializing the native surface mid-fade.
            if (AnimationsEnabled) _previewAnimator.PrepareReveal(AppWindow.Position.Y, CurrentScale(), initialAlpha: 1);
            WindowHelper.SetWindowCloak(this, false);
            _revealed = true;
            if (AnimationsEnabled)
                WindowHelper.WhenRendered(this, () =>
                {
                    if (_isOpen && version == _animationVersion) _previewAnimator.PlayReveal();
                }, framesToWait: 3);
            else TryStartMediaPlayback();
        }, framesToWait: 4);
    }

    private void UpdateShortcutHints()
    {
        HintText.Text = _paths.Count > 1 && ShellRoot.ActualWidth >= 580 ? "Esc 关闭 · ← → 切换" : "空格 / Esc 关闭";
        ShortcutHelp.Text = "Esc 关闭预览\n空格 关闭预览（正文焦点）\nEnter 用默认程序打开"
            + (_paths.Count > 1 ? "\n← → 切换文件" : "")
            + (Classify(CurrentPath) is Kind.Text or Kind.StructuredText or Kind.OfficeText or Kind.Archive or Kind.Folder or Kind.Pdf or Kind.Html
                ? "\n↑ ↓ / PageUp / PageDown 滚动" : "")
            + "\n按钮、滑条和输入框保留各自的键盘操作。";
        ToolTipService.SetToolTip(HintText, ShortcutHelp.Text);
    }

    private bool ScrollActiveContent(VirtualKey key)
    {
        ScrollViewer? scroll = ReadingView.Visibility == Visibility.Visible ? ReadingView
            : ImageSurface.Visibility == Visibility.Visible ? ImageSurface
            : ListSurface.Visibility == Visibility.Visible ? FindScrollViewer(EntryList)
            : TableSurface.Visibility == Visibility.Visible ? FindScrollViewer(TableRows) : null;
        if (scroll == null) return false;
        var down = key is VirtualKey.Down or VirtualKey.PageDown;
        var amount = key is VirtualKey.PageUp or VirtualKey.PageDown ? scroll.ViewportHeight * .9 : 60;
        scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + (down ? amount : -amount), 0, scroll.ScrollableHeight), null);
        return true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(node, i)) is { } result) return result;
        return null;
    }

    private void ContentHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitImageSurface();
        if (TitleText != null && HintText != null) UpdateShortcutHints();
    }

    private void FitImageSurface()
    {
        if (ImageSurface.Visibility != Visibility.Visible || _contentSize is not { } size || ContentHost.ActualWidth <= 0) return;
        var fit = Math.Min(1, Math.Min(ContentHost.ActualWidth / size.Width, ContentHost.ActualHeight / size.Height));
        ImageView.Width = Math.Max(1, size.Width * fit);
        ImageView.Height = Math.Max(1, size.Height * fit);
    }

    private async Task LoadSvgAsync(string path, int token)
    {
        var text = await ReadTextAsync(path);
        if (token != _loadToken) return;
        var root = System.Xml.Linq.XDocument.Parse(text ?? "").Root;
        var width = 300d; var height = 150d;
        var viewBox = ((string?)root?.Attribute("viewBox"))?.Split(new[] { ' ', ',', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (viewBox is { Length: 4 }
            && double.TryParse(viewBox[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            && double.TryParse(viewBox[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)
            && double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0) { width = Math.Clamp(w, 1, 100000); height = Math.Clamp(h, 1, 100000); }
        double? Length(string name)
        {
            var value = ((string?)root?.Attribute(name))?.Trim();
            if (value?.EndsWith("px", StringComparison.OrdinalIgnoreCase) == true) value = value[..^2];
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) && number > 0 ? Math.Clamp(number, 1, 100000) : null;
        }
        if (Length("width") is { } nativeWidth && Length("height") is { } nativeHeight) { width = nativeWidth; height = nativeHeight; }
        var image = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
        var rasterFit = Math.Min(1, 2048 / Math.Max(width * CurrentScale(), height * CurrentScale()));
        image.RasterizePixelWidth = width * CurrentScale() * rasterFit;
        image.RasterizePixelHeight = height * CurrentScale() * rasterFit;
        image.Opened += (_, _) => OnContentSized(token, path, (int)width, (int)height);
        image.OpenFailed += (_, _) => { if (token == _loadToken) ShowFallback(path, "无法显示此 SVG", "系统无法渲染此矢量图。用默认程序打开以查看。"); };
        ImageView.Source = image;
        ShowOnly(ImageSurface);
        // 使用本次打开的文件流，避免同名 SVG 更新后仍命中 URI 缓存。
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        if (token != _loadToken) return;
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        if (token != _loadToken) return;
        await image.SetSourceAsync(stream);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ChangeZoom(.8f);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ChangeZoom(1.25f);
    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        var scroll = PdfView.Visibility == Visibility.Visible ? PdfView : ImageSurface;
        scroll.ChangeView(0, 0, 1, true);
    }
    private void ChangeZoom(float factor)
    {
        var scroll = PdfView.Visibility == Visibility.Visible ? PdfView : ImageSurface;
        scroll.ChangeView(null, null, Math.Clamp(scroll.ZoomFactor * factor, scroll.MinZoomFactor, scroll.MaxZoomFactor));
    }

    private void WrapButton_Click(object sender, RoutedEventArgs e)
    {
        SetTextPresentation(TextBody.TextWrapping != TextWrapping.Wrap, IsReadableText(CurrentPath) && !_sourceMode ? "Segoe UI" : "Cascadia Mono");
        WrapButton.Content = TextBody.TextWrapping == TextWrapping.Wrap ? "取消换行" : "自动换行";
    }

    private async void SourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_markdownBlocks == null) return;
        _sourceMode = !_sourceMode;
        if (_sourceMode)
        {
            SetTextPresentation(true, "Cascadia Mono"); TextBody.Text = _rawText;
            ShowOnly(TextView); WrapButton.Visibility = Visibility.Visible;
            SourceButton.Content = "阅读"; ViewStatus.Text = "Markdown 源码";
        }
        else
        {
            // Yield before constructing the bounded native document, so button input is acknowledged.
            var token = _loadToken;
            await Task.Yield();
            if (token != _loadToken || _markdownBlocks == null) return;
            RenderMarkdown(_markdownBlocks);
        }
    }

    private async Task ShowRawTextAsync(string path, int token, string status)
    {
        var text = await ReadTextAsync(path);
        if (token != _loadToken) return;
        if (text == null) { ShowUnsupported(path); return; }
        _rawText = text;
        TextBody.Text = text;
        SetTextPresentation(true, "Cascadia Mono");
        WrapButton.Visibility = Visibility.Visible; PreviewToolbar.Visibility = Visibility.Visible;
        ViewStatus.Text = status; ToolTipService.SetToolTip(ViewStatus, status);
        ShowOnly(TextView); FitTextWindow(860);
    }

    private void RenderMarkdown(IReadOnlyList<MarkdownPreviewBlock> blocks)
    {
        _markdownBlocks = blocks;
        ReadingBody.Children.Clear();
        foreach (var block in blocks)
        {
            if (block.Kind == "table")
            {
                var lines = block.Text.Split('\n');
                var rows = lines.Take(100).Select(line => line.Trim().Trim('|').Split('|').Take(12).Select(c => c.Trim()).ToArray()).ToArray();
                var panel = new StackPanel();
                var columns = rows.Max(r => r.Length);
                foreach (var (row, index) in rows.Select((r, i) => (r, i)))
                {
                    var grid = new Grid();
                    for (var c = 0; c < columns; c++)
                    {
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
                        var cell = new TextBlock { Style = PreviewStyle("PreviewReadingTextStyle"), Text = c < row.Length ? row[c] : "" };
                        if (index == 0) cell.FontWeight = FontWeights.SemiBold;
                        Grid.SetColumn(cell, c); grid.Children.Add(cell);
                    }
                    panel.Children.Add(grid);
                }
                ReadingBody.Children.Add(new ScrollViewer { Style = PreviewStyle("PreviewCodeSurfaceStyle"), Content = panel });
                if (lines.Length > 100) ReadingBody.Children.Add(new TextBlock { Text = "… 表格较长，仅展示前100行；源码中可查看剩余内容。", Style = PreviewStyle("PreviewCaptionStyle") });
                continue;
            }
            var text = new TextBlock { Style = PreviewStyle(block.Kind == "heading" ? $"PreviewHeading{Math.Min(3, block.Level)}Style"
                : block.Kind == "code" ? "PreviewCodeTextStyle" : "PreviewReadingTextStyle") };
            if (block.Kind == "code")
            {
                text.Text = block.Text;
                ReadingBody.Children.Add(new ScrollViewer { Style = PreviewStyle("PreviewCodeSurfaceStyle"), Content = text });
            }
            else if (block.Kind == "rule")
            {
                ReadingBody.Children.Add(new Border { Style = PreviewStyle("PreviewRuleStyle") });
            }
            else
            {
                AddMarkdownInlines(text.Inlines, block.Text);
                if (block.Kind == "quote") ReadingBody.Children.Add(new Border { Style = PreviewStyle("PreviewQuoteStyle"), Child = text });
                else
                {
                    if (block.Kind == "list") text.Style = PreviewStyle($"PreviewListLevel{block.Level}Style");
                    ReadingBody.Children.Add(text);
                }
            }
        }
        if (blocks.Count == 0) ReadingBody.Children.Add(new TextBlock { Text = "此文件为空。", Style = PreviewStyle("PreviewReadingTextStyle") });
        SourceButton.Visibility = Visibility.Visible; SourceButton.Content = "源码"; WrapButton.Visibility = Visibility.Collapsed;
        ViewStatus.Text = "Markdown 阅读";
        ShowOnly(ReadingView);
        var width = MeasureWidth(780);
        ReadingBody.Measure(new Size(Math.Max(1, width - ChromeWidthDip - _frameWidthDip - 2), double.PositiveInfinity));
        SetPreviewBounds(width, Math.Min(760, ReadingBody.DesiredSize.Height + ChromeHeightDip + ToolbarHeight + _frameHeightDip + 4));
    }

    private static readonly Regex InlineMarkdown = new(@"(`+)(.+?)\1|\*\*(.+?)\*\*|__(.+?)__|\*([^*\n]+)\*|(?<image>!)?\[([^\]\n]+)\]\(([^)\s]+)\)", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static void AddMarkdownInlines(InlineCollection inlines, string source)
    {
        var position = 0;
        foreach (Match match in InlineMarkdown.Matches(source))
        {
            if (match.Index > position) inlines.Add(new Run { Text = source[position..match.Index] });
            if (match.Groups[2].Success) inlines.Add(new Run { Text = match.Groups[2].Value, FontFamily = new FontFamily("Cascadia Mono") });
            else if (match.Groups[3].Success || match.Groups[4].Success)
                inlines.Add(new Bold { Inlines = { new Run { Text = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value } } });
            else if (match.Groups[5].Success) inlines.Add(new Italic { Inlines = { new Run { Text = match.Groups[5].Value } } });
            else
            {
                var label = match.Groups[6].Value;
                var target = match.Groups[7].Value;
                if (match.Groups["image"].Success) inlines.Add(new Run { Text = $"[图片：{label}]" });
                else if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                    inlines.Add(new Hyperlink { NavigateUri = uri, Inlines = { new Run { Text = label } } });
                else inlines.Add(new Run { Text = label + " (" + target + ")" });
            }
            position = match.Index + match.Length;
        }
        if (position < source.Length) inlines.Add(new Run { Text = source[position..] });
    }

    private void ShowWorkbook(IReadOnlyList<PreviewTable> tables)
    {
        _workbook = tables;
        PreviewToolbar.Visibility = Visibility.Visible;
        SheetSelector.Visibility = tables.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        SheetSelector.ItemsSource = tables.Select(t => t.Name).ToArray();
        if (tables.Count == 0) { ShowFallback(CurrentPath, "没有可显示的工作表", "用默认程序打开以查看完整文档。"); return; }
        SheetSelector.SelectedIndex = 0;
        if (TableRows.ItemsSource == null) ShowTable(tables[0]);
    }

    private void SheetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SheetSelector.SelectedIndex >= 0 && SheetSelector.SelectedIndex < _workbook.Count) ShowTable(_workbook[SheetSelector.SelectedIndex]);
    }

    public sealed record TableRow(string[] Cells, string[] Details);
    private void ShowTable(PreviewTable table)
    {
        var count = Math.Clamp(table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Length), 1, PreviewDataReader.MaxColumns);
        var widths = Enumerable.Range(0, count).Select(c => Math.Clamp(table.Rows.Take(40).Select(r => c < r.Length ? r[c].Length : 0).DefaultIfEmpty(0).Max() * 8 + 24, 100, 260)).ToArray();
        TableHeader.Children.Clear(); TableHeader.ColumnDefinitions.Clear();
        var template = new StringBuilder("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='32'><Grid.ColumnDefinitions>");
        foreach (var width in widths) template.Append($"<ColumnDefinition Width='{width}'/>");
        template.Append("</Grid.ColumnDefinitions>");
        for (var c = 0; c < count; c++)
        {
            TableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[c]) });
            var header = new TextBlock { Text = ColumnName(c), Style = PreviewStyle("PreviewTableCellStyle") };
            Grid.SetColumn(header, c); TableHeader.Children.Add(header);
            template.Append($"<TextBlock Grid.Column='{c}' Style='{{StaticResource PreviewTableCellStyle}}' Text='{{Binding Cells[{c}]}}' ToolTipService.ToolTip='{{Binding Details[{c}]}}'/>");
        }
        template.Append("</Grid></DataTemplate>");
        TableGrid.Width = widths.Sum() + 24;
        TableRows.ItemTemplate = (DataTemplate)XamlReader.Load(template.ToString());
        static string Limit(string value, int length) => value.Length > length ? value[..length] + "…" : value;
        TableRows.ItemsSource = table.Rows.Select(row =>
        {
            var values = Enumerable.Range(0, count).Select(c => c < row.Length ? row[c] : "").ToArray();
            return new TableRow(values.Select(v => Limit(v, 512).Replace("\r", "").Replace("\n", " ↵ ")).ToArray(), values.Select(v => Limit(v, 2048)).ToArray());
        }).ToArray();
        TableEmpty.Visibility = table.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TableHeader.Visibility = table.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ViewStatus.Text = $"{table.Name} · {table.Rows.Count} 行 × {(table.Rows.Count == 0 ? 0 : count)} 列" + (table.Truncated ? " · 仅显示前一部分" : "");
        ToolTipService.SetToolTip(ViewStatus, ViewStatus.Text + "\n显示单元格已有值，不计算公式或还原格式。");
        ShowOnly(TableSurface);
        SetPreviewBounds(Math.Clamp(TableGrid.Width + ChromeWidthDip + _frameWidthDip, 620, 1000), PreviewLayoutPolicy.ListHeight(table.Rows.Count, ChromeHeightDip + ToolbarHeight + _frameHeightDip));
        EntryEmpty.Visibility = Visibility.Collapsed;
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (var value = index + 1; value > 0; value = (value - 1) / 26) name = (char)('A' + (value - 1) % 26) + name;
        return name;
    }

    private async Task LoadEntriesAsync(string path, int token, bool archive)
    {
        _isArchive = archive;
        var cancellation = _contentCancellation.Token;
        if (archive)
        {
            var entries = await Task.Run(() => PreviewDataReader.ReadZip(path, cancellation));
            if (token != _loadToken) return;
            _archiveEntries = entries; _archivePrefix = ""; ShowArchiveLevel();
        }
        else await LoadFolderLevelAsync(path, token);
    }

    private void ShowArchiveLevel()
    {
        var rows = _archiveEntries.Where(e => e.Path.StartsWith(_archivePrefix, StringComparison.Ordinal)
            && !e.Path[_archivePrefix.Length..].TrimEnd('/').Contains('/')).ToArray();
        ShowEntryRows(rows, _archivePrefix.Length == 0 ? "压缩包" : _archivePrefix, _archiveEntries.Count >= PreviewDataReader.MaxEntries);
        ListUpButton.Visibility = _archivePrefix.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListUpButton.IsEnabled = _archivePrefix.Length > 0;
    }

    private async Task LoadFolderLevelAsync(string path, int token)
    {
        var navigation = ++_directoryVersion;
        var cancellation = _contentCancellation.Token;
        var entries = await Task.Run(() => PreviewDataReader.ReadFolder(path, cancellation));
        if (token != _loadToken || navigation != _directoryVersion) return;
        _listingFolder = path;
        ShowEntryRows(entries, Path.GetFileName(path.TrimEnd('\\', '/')), entries.Count >= PreviewDataReader.MaxEntries);
        ListUpButton.IsEnabled = !string.Equals(_listingFolder, CurrentPath, StringComparison.OrdinalIgnoreCase);
        ListUpButton.Visibility = ListUpButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEntryRows(IReadOnlyList<PreviewEntry> entries, string name, bool limited)
    {
        EntryList.ItemsSource = entries;
        EntryEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewStatus.Text = $"{name} · {entries.Count} 项" + (limited ? " · 仅显示前一部分" : "");
        ToolTipService.SetToolTip(ViewStatus, ViewStatus.Text + "\n点击文件夹查看内容；预览不会解压或执行文件。");
        ShowOnly(ListSurface);
        SetPreviewBounds(620, PreviewLayoutPolicy.ListHeight(entries.Count, ChromeHeightDip + ToolbarHeight + _frameHeightDip));
    }

    private async void EntryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PreviewEntry { IsDirectory: true } entry) return;
        var token = _loadToken;
        try
        {
            if (_isArchive) { _archivePrefix = entry.Path; ShowArchiveLevel(); }
            else await LoadFolderLevelAsync(entry.Path, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (token == _loadToken) { ViewStatus.Text = "无法读取此目录"; ToolTipService.SetToolTip(ViewStatus, ex.Message); } }
    }

    private async void ListUpButton_Click(object sender, RoutedEventArgs e)
    {
        var token = _loadToken;
        try
        {
            if (_isArchive)
            {
                var parent = _archivePrefix.TrimEnd('/');
                var slash = parent.LastIndexOf('/'); _archivePrefix = slash < 0 ? "" : parent[..(slash + 1)];
                ShowArchiveLevel();
            }
            else if (!string.Equals(_listingFolder, CurrentPath, StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(_listingFolder) is { } parent)
                await LoadFolderLevelAsync(parent, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (token == _loadToken) { ViewStatus.Text = "无法读取此目录"; ToolTipService.SetToolTip(ViewStatus, ex.Message); } }
    }
}
