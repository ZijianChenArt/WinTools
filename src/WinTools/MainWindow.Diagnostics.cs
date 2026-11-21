#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinTools;

public sealed partial class MainWindow
{
    // 仅在显式设置输出目录时做静态渲染，不模拟点击、悬停或拖动，不编进发布版。
    private void StartNavigationLayoutCapture()
    {
        var output = Environment.GetEnvironmentVariable("WINTOOLS_NAV_QA_DIR");
        if (!string.IsNullOrWhiteSpace(output)) _ = CaptureNavigationLayoutsAsync(output);
    }

    private async Task CaptureNavigationLayoutsAsync(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            await Task.Delay(3500);
            if (Environment.GetEnvironmentVariable("WINTOOLS_NAV_QA_PAGE") == NavTags.TaskbarInfo)
                MainNav.SelectedItem = NavItemTaskbarInfo;
            else if (Environment.GetEnvironmentVariable("WINTOOLS_NAV_QA_PAGE") == NavTags.ThreeFingerDrag)
                MainNav.SelectedItem = NavItemThreeFingerDrag;
            else if (Environment.GetEnvironmentVariable("WINTOOLS_NAV_QA_PAGE") == NavTags.Clipboard)
                MainNav.SelectedItem = NavItemClipboard;
            var reports = new List<object>();
            foreach (var (name, width, height, open) in new[]
            {
                ("expanded", 1040, 760, true),
                ("compact", 760, 650, false),
                ("narrow", 600, 650, false),
                ("short", 900, 450, true),
                ("wide", 1600, 900, true),
            })
            {
                var scale = ShellRoot.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
                await Task.Delay(350);
                MainNav.IsPaneOpen = open;
                await Task.Delay(750);
                ShellRoot.UpdateLayout();
                await CaptureNavigationFrameAsync(ShellRoot, output, name);
                reports.Add(new
                {
                    name, Width = ShellRoot.ActualWidth, Height = ShellRoot.ActualHeight,
                    MainNav.IsPaneOpen, DisplayMode = MainNav.DisplayMode.ToString(),
                    Items = MainNav.MenuItems.OfType<FrameworkElement>().Select(item => new
                    {
                        item.Name, item.ActualWidth, item.ActualHeight,
                        Y = item.TransformToVisual(ShellRoot).TransformPoint(new Windows.Foundation.Point()).Y,
                        Header = (item as NavigationViewItemHeader)?.Content?.ToString(),
                        Tag = (item as NavigationViewItem)?.Tag?.ToString(),
                    }).ToArray(),
                    FooterTag = NavItemAppSettings.Tag,
                    SelectedTag = (MainNav.SelectedItem as NavigationViewItem)?.Tag,
                });
            }
            if (ReferenceEquals(MainNav.SelectedItem, NavItemClipboard) && App.Current is App app)
            {
                var testScale = ShellRoot.XamlRoot.RasterizationScale;
                AppWindow.Resize(new SizeInt32((int)(1024 * testScale), (int)(712 * testScale)));
                await Task.Delay(300);
                var scrollGaps = new Dictionary<string, double>();
                var mainHistory = FeaturePages.GetControl<ClipboardHistoryView>("ClipboardHistoryPanel");
                CheckClipboardScrollGap(mainHistory, "ClipboardPage", scrollGaps);
                await CaptureNavigationFrameAsync(ShellRoot, output, "clipboard-page-scrollbar");
                MainNav.SelectedItem = NavItemDesktopOrganize;
                await Task.Delay(150);
                if (HotkeyDesktopCardBox.CornerRadius.TopLeft != 10)
                    throw new InvalidOperationException("The settings hotkey field did not use the shared control radius.");
                await CaptureNavigationFrameAsync(ShellRoot, output, "rounded-hotkey");
                var oldZones = ZonesListView.ItemsSource;
                try
                {
                    ZonesListView.ItemsSource = Enumerable.Range(1, 30).Select(i => new DesktopZone { Name = $"检查分区 {i}" }).ToArray();
                    await Task.Delay(150);
                    if (FindVisual<ScrollViewer>(ZonesListView, _ => true) is { } zonesScroller
                        && FindVisual<ItemsWrapGrid>(ZonesListView, _ => true) is { } zonesGrid)
                    {
                        var zonesBar = FeaturePages.GetControl<ScrollBar>("ZonesScrollBar");
                        CheckScrollGap(zonesScroller, zonesGrid, (FrameworkElement)ZonesListView.Parent,
                            "DesktopZones", 1, scrollGaps, zonesBar);
                        var hotkeyY = HotkeyDesktopCardBox.TransformToVisual(ShellRoot)
                            .TransformPoint(new Windows.Foundation.Point()).Y;
                        zonesScroller.ChangeView(null, zonesScroller.ScrollableHeight, null, disableAnimation: true);
                        await Task.Delay(150);
                        if (zonesScroller.VerticalOffset <= 0 || ZonesListView.ContainerFromIndex(29) == null
                            || Math.Abs(zonesBar.Value - zonesScroller.VerticalOffset) > 1
                            || Math.Abs(HotkeyDesktopCardBox.TransformToVisual(ShellRoot)
                                .TransformPoint(new Windows.Foundation.Point()).Y - hotkeyY) > 1)
                            throw new InvalidOperationException("Only the desktop zone cards should scroll.");
                        await CaptureNavigationFrameAsync(ShellRoot, output, "desktop-zone-scrollbar");
                        zonesScroller.ChangeView(null, 0, null, disableAnimation: true);
                    }
                }
                finally { ZonesListView.ItemsSource = oldZones; }
                MainNav.SelectedItem = NavItemTaskbarInfo;
                await Task.Delay(150);
                Activate();
                var shortcutList = TaskbarControl<ListView>("TaskbarShortcutList");
                var defaultConfig = new Config();
                var changedConfig = JsonSerializer.Deserialize<Config>(
                    """{"taskbarShortcutOrder":["audio","clipboard","audio","unknown"]}""")!;
                if (defaultConfig.TaskbarShortcutOrder[^1] != "audio"
                    || !shortcutList.CanReorderItems || !shortcutList.AllowDrop
                    || !TaskbarInfoService.GetShortcutIds(changedConfig).Take(2)
                        .SequenceEqual(new[] { TaskbarInfoService.AudioId, TaskbarInfoService.ClipboardId }))
                    throw new InvalidOperationException("The taskbar shortcut list or its saved order was not applied.");
                try
                {
                    ApplyTaskbarShortcutOrder(changedConfig.TaskbarShortcutOrder);
                    if ((shortcutList.Items[0] as FrameworkElement)?.Tag as string != "audio")
                        throw new InvalidOperationException("The settings list did not reflect the shortcut order.");
                }
                finally { ApplyTaskbarShortcutOrder(_config.TaskbarShortcutOrder); }
                await Task.Delay(150);
                var pageScrollStyle = Application.Current.Resources["PageBodyScrollStyle"];
                var taskbarScroll = FindVisual<ScrollViewer>(
                    FeaturePages.GetControl<Grid>("ContentTaskbarInfo"), viewer => ReferenceEquals(viewer.Style, pageScrollStyle));
                if (taskbarScroll?.Content is FrameworkElement settingsContent)
                    CheckScrollGap(taskbarScroll, settingsContent, PageViewport, "TaskbarSettings", 8, scrollGaps);
                await CaptureNavigationFrameAsync(ShellRoot, output, "taskbar-shortcuts");
                var alignment = TaskbarControl<RoundedComboBox>("TaskbarAlignmentBox");
                alignment.IsDropDownOpen = true;
                try
                {
                    await Task.Delay(125);
                    var dropdownBorder = alignment.GetDropDownBorderForDiagnostics();
                    if (dropdownBorder?.CornerRadius.TopLeft != 12)
                        throw new InvalidOperationException($"Settings dropdown radius: {dropdownBorder?.CornerRadius.TopLeft}. Open: {alignment.IsDropDownOpen}.");
                }
                finally { alignment.IsDropDownOpen = false; }
                MainNav.SelectedItem = NavItemClipboard;
                var style = UiStyleService.Preference;
                UiStyleService.SetPreference(UiStylePreference.Mica);
                var popup = new ClipboardHistoryWindow(app.ClipboardHistory);
                try
                {
                    var work = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
                    var anchor = (work.X + work.Width * 3 / 4, work.Y + work.Height);
                    var animationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
                    popup.Toggle(anchor);
                    var startingY = popup.AppWindow.Position.Y;
                    await Task.Delay(500);
                    var bounds = popup.AppWindow.Position;
                    var size = popup.AppWindow.Size;
                    if (!popup.AppWindow.IsVisible || bounds.X < work.X || bounds.Y < work.Y
                        || bounds.X + size.Width > work.X + work.Width || bounds.Y + size.Height > work.Y + work.Height)
                        throw new InvalidOperationException("The clipboard popup was not visible within the work area.");
                    var popupRoot = (FrameworkElement)popup.Content;
                    if (FindVisual<ClipboardHistoryView>(popupRoot, _ => true) is { } popupHistory)
                    {
                        if (popupHistory.FindName("HistoryList") is ListView popupList
                            && FindVisual<ScrollViewer>(popupList, _ => true) is { } popupScroller)
                            await WaitForNativeScrollbarIdleAsync(popupScroller);
                        CheckClipboardScrollGap(popupHistory, "ClipboardPopup", scrollGaps);
                        var title = (FrameworkElement)popupHistory.FindName("PopupTitle");
                        var status = (FrameworkElement)popupHistory.FindName("StatusText");
                        var clear = (FrameworkElement)popupHistory.FindName("ClearButton");
                        var close = (Button)popupHistory.FindName("PopupCloseButton");
                        var closeIcon = (FrameworkElement)close.Content;
                        double CenterY(FrameworkElement element) => element.TransformToVisual(popupRoot)
                            .TransformPoint(new Windows.Foundation.Point(0, element.ActualHeight / 2)).Y;
                        var titleLeft = title.TransformToVisual(popupRoot).TransformPoint(new Windows.Foundation.Point()).X;
                        var iconRight = closeIcon.TransformToVisual(popupRoot)
                            .TransformPoint(new Windows.Foundation.Point(closeIcon.ActualWidth, 0)).X;
                        if (new[] { status, clear, closeIcon }.Any(element => Math.Abs(CenterY(element) - CenterY(title)) > 1)
                            || Math.Abs(titleLeft - (popupRoot.ActualWidth - iconRight)) > 2)
                            throw new InvalidOperationException("The clipboard toolbar did not share a center line and matching outer spacing.");
                        var historySurface = (FrameworkElement)popupHistory.FindName("HistorySurface");
                        var items = (ListView)popupHistory.FindName("HistoryList");
                        if (items.ContainerFromIndex(0) is FrameworkElement firstRow
                            && items.ContainerFromIndex(1) is FrameworkElement secondRow)
                        {
                            var first = firstRow.TransformToVisual(historySurface).TransformPoint(new Windows.Foundation.Point());
                            var second = secondRow.TransformToVisual(historySurface).TransformPoint(new Windows.Foundation.Point());
                            var rightInset = historySurface.ActualWidth - first.X - firstRow.ActualWidth;
                            if (Math.Abs(first.X - first.Y) > 1 || Math.Abs(first.X - rightInset) > 1
                                || Math.Abs(second.Y - first.Y - firstRow.ActualHeight - 10) > 1)
                                throw new InvalidOperationException($"Uneven clipboard spacing: {first.X}/{first.Y}/{rightInset}, rows {second.Y - first.Y - firstRow.ActualHeight}.");
                        }
                    }
                    await CaptureNavigationFrameAsync(popupRoot, output, "clipboard-popup");
                    File.WriteAllText(Path.Combine(output, "scroll-gaps.json"), JsonSerializer.Serialize(scrollGaps));
                    var toolTip = new ToolTip
                    {
                        Content = "删除记录",
                        PlacementTarget = popupRoot,
                        XamlRoot = popupRoot.XamlRoot,
                    };
                    ToolTipService.SetToolTip(popupRoot, toolTip);
                    try
                    {
                        toolTip.IsOpen = true;
                        await Task.Delay(125);
                        if (toolTip.CornerRadius.TopLeft != 12 || toolTip.ActualWidth <= 0)
                            throw new InvalidOperationException("The WinUI tooltip did not use the shared overlay corner radius.");
                    }
                    finally
                    {
                        if (toolTip.IsOpen) toolTip.IsOpen = false;
                        ToolTipService.SetToolTip(popupRoot, null);
                    }
                    popup.Toggle(anchor);
                    var visibleWhileClosing = popup.AppWindow.IsVisible;
                    popup.Toggle(anchor);
                    await Task.Delay(500);
                    if (!popup.AppWindow.IsVisible || popup.AppWindow.Position.Y != bounds.Y)
                        throw new InvalidOperationException("Reopening did not interrupt the clipboard popup dismissal.");
                    popup.Toggle(anchor);
                    await Task.Delay(300);
                    var dismissedY = popup.AppWindow.Position.Y;
                    if (popup.AppWindow.IsVisible || (animationsEnabled
                        && (startingY <= bounds.Y || !visibleWhileClosing || dismissedY <= bounds.Y)))
                        throw new InvalidOperationException("The clipboard popup did not complete its open and close motion.");
                    File.WriteAllText(Path.Combine(output, "clipboard-motion.json"), JsonSerializer.Serialize(new
                    {
                        animationsEnabled, MicaSupported = Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported(),
                        startingY, OpenY = bounds.Y, dismissedY, visibleWhileClosing,
                    }));
                }
                finally
                {
                    popup.Close();
                    UiStyleService.SetPreference(style);
                }
            }
            File.WriteAllText(Path.Combine(output, "layout-report.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "capture-error.txt"), ex.ToString());
        }
    }

    private static void CheckClipboardScrollGap(ClipboardHistoryView view, string name, IDictionary<string, double> gaps)
    {
        if (view.FindName("HistoryList") is ListView list && list.ContainerFromIndex(0) is FrameworkElement firstRow
            && view.FindName("HistoryScrollBar") is ScrollBar bar && list.Parent is FrameworkElement frame
            && FindVisual<ScrollViewer>(list, _ => true) is { } viewer)
            CheckScrollGap(viewer, firstRow, frame, name, 1, gaps, bar);
    }

    private static async Task WaitForNativeScrollbarIdleAsync(ScrollViewer viewer)
    {
        VisualStateManager.GoToState(viewer, "MouseIndicator", false);
        VisualStateManager.GoToState(viewer, "NoIndicator", true);
        await Task.Delay(2300);
    }

    private static void CheckScrollGap(ScrollViewer viewer, FrameworkElement content, FrameworkElement edge, string name,
        double edgeLimit, IDictionary<string, double> gaps, ScrollBar? external = null)
    {
        if (viewer.ScrollableHeight <= 0) return;
        var bar = external ?? FindVisual<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(viewer,
            candidate => candidate.Name == "VerticalScrollBar", node => node is ScrollContentPresenter)
            ?? throw new InvalidOperationException($"Missing scrollbar in {name}.");
        var thumb = FindVisual<Microsoft.UI.Xaml.Controls.Primitives.Thumb>(bar, candidate => candidate.Name == "VerticalThumb");
        var visual = thumb == null ? null : FindVisual<Border>(thumb, candidate => candidate.Name == "ThumbVisual");
        if (visual?.ActualWidth != 6 || visual.CornerRadius.TopLeft != 3)
            throw new InvalidOperationException($"Missing thicker rounded scrollbar in {name}.");
        if (bar.Visibility != Visibility.Visible || thumb?.Visibility != Visibility.Visible || thumb.Opacity != 1 || visual.ActualHeight <= 0)
            throw new InvalidOperationException($"The vertical scrollbar is hidden in {name}.");
        var contentRight = content.TransformToVisual(viewer)
            .TransformPoint(new Windows.Foundation.Point(content.ActualWidth, 0)).X;
        var gap = bar.TransformToVisual(viewer).TransformPoint(new Windows.Foundation.Point()).X - contentRight;
        if (gap < 2) throw new InvalidOperationException($"Scrollbar overlaps {name}. Clearance: {gap} DIP.");
        var barRight = bar.TransformToVisual(edge).TransformPoint(new Windows.Foundation.Point(bar.ActualWidth, 0)).X;
        var edgeGap = edge.ActualWidth - barRight;
        if (edgeGap < 0 || edgeGap > edgeLimit)
            throw new InvalidOperationException($"Scrollbar is {edgeGap} DIP from the right edge of {name}.");
        gaps.Add(name, gap);
        gaps.Add(name + "Edge", edgeGap);
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> match,
        Func<DependencyObject, bool>? skipChildren = null) where T : DependencyObject
    {
        if (root is T result && match(result)) return result;
        if (skipChildren?.Invoke(root) == true) return null;
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            if (FindVisual(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index), match, skipChildren) is { } child) return child;
        return null;
    }

    private static async Task CaptureNavigationFrameAsync(FrameworkElement root, string output, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root);
        var pixels = await bitmap.GetPixelsAsync();
        var file = await StorageFile.GetFileFromPathAsync(CreateCaptureFile(output, name));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var data = new byte[pixels.Length];
        using var reader = DataReader.FromBuffer(pixels);
        reader.ReadBytes(data);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
        await encoder.FlushAsync();
    }

    private static string CreateCaptureFile(string output, string name)
    {
        var path = Path.GetFullPath(Path.Combine(output, name + ".png"));
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }
}
#endif
