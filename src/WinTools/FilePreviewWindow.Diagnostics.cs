#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private bool _capturingPreviewLayouts;
    // 显式 opt-in 的 Debug 渲染检查：不模拟鼠标、键盘或桌面拖拽，不编进正式版。
    internal void StartPreviewLayoutCapture()
    {
        var root = Environment.GetEnvironmentVariable("WINTOOLS_PREVIEW_QA_DIR");
        if (!string.IsNullOrWhiteSpace(root)) _ = CapturePreviewLayoutsAsync(root);
    }

    private async Task CapturePreviewLayoutsAsync(string root)
    {
        var reports = new List<object>();
        try
        {
            _capturingPreviewLayouts = true;
            await Task.Delay(2500);
            foreach (var name in new[] { "sample.md", "sample.txt", "empty.txt", "sample.zip", "small.zip", "empty.zip", "sample-folder",
                "data.csv", "book.xlsx", "data.json", "broken.json", "large.json", "sample.docx", "slides.pptx", "sample.doc", "slides.ppt", "sample.html", "sample.obj", "textured.obj", "sample.fbx", "ascii.fbx", "complex.fbx", "broken.fbx", "sample.pdf", "sample.svg", "tiny.png", "sample.blend", "sample.wav", "sample.mp4", "portrait.mp4" })
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                _paths = new[] { path };
                _index = 0;
                Present();
                var loadingClock = Stopwatch.StartNew();
                var loading = LoadCurrentAsync();
                var waitingText = false;
                var waitingSpinner = false;
                if (OfficePreviewRenderer.IsSupported(path))
                {
                    while (!loading.IsCompleted && TextView.Visibility != Visibility.Visible && FallbackPanel.Visibility != Visibility.Visible)
                        await Task.WhenAny(loading, Task.Delay(10));
                    if (!loading.IsCompleted)
                    {
                        waitingText = TextView.Visibility == Visibility.Visible;
                        waitingSpinner = LoadingRing.IsActive;
                        await CapturePreviewFrameAsync(root, name + "-waiting.png");
                    }
                }
                await loading;
                var loadMs = loadingClock.Elapsed.TotalMilliseconds;
                while (loadingClock.Elapsed.TotalSeconds < 12 && FallbackPanel.Visibility != Visibility.Visible
                    && ((name.EndsWith(".html") && HtmlView.Visibility != Visibility.Visible)
                        || (name.EndsWith(".pdf") && (_pdfImages.Count == 0 || _pdfImages[0].Source == null))
                        || (name.EndsWith(".png") && _contentSize == null)
                        || (name.EndsWith(".mp4") && !_mediaReady)))
                    await Task.Delay(10);
                var readyMs = loadingClock.Elapsed.TotalMilliseconds;
                await Task.Delay(1800);
                string? webState = null;
                bool? modelIdleFramesStable = null;
                if (HtmlView.Visibility == Visibility.Visible && HtmlView.CoreWebView2 is { } web)
                {
                    webState = await web.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,width:innerWidth,height:innerHeight,model:window.__modelPreview||null})");
                    if (_modelReady)
                    {
                        var frames = await web.ExecuteScriptAsync("window.__modelPreview.frames");
                        await Task.Delay(150);
                        modelIdleFramesStable = frames == await web.ExecuteScriptAsync("window.__modelPreview.frames");
                    }
                    var capturePath = Path.Combine(root, name + "-web.png");
                    File.WriteAllBytes(capturePath, Array.Empty<byte>());
                    var captureFile = await StorageFile.GetFileFromPathAsync(capturePath);
                    using var captureStream = await captureFile.OpenAsync(FileAccessMode.ReadWrite);
                    await web.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, captureStream);
                }
                var naturalWidth = AppWindow.Size.Width / CurrentScale();
                var naturalHeight = AppWindow.Size.Height / CurrentScale();
                foreach (var width in new[] { 0, 420, 960 })
                {
                    var scale = CurrentScale();
                    if (width > 0) AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(480 * scale)));
                    await Task.Delay(400);
                    ShellRoot.UpdateLayout();
                    await CapturePreviewFrameAsync(root, $"{name}-{width}.png");
                    var controls = new Dictionary<string, object>();
                    foreach (var element in new FrameworkElement[] { HeaderBar, TitleText, MetaText, ContentHost,
                        PreviewToolbar, SourceButton, WrapButton, SheetSelector, ViewStatus, ReadingView, ReadingBody, ListSurface, EntryList,
                        TableSurface, TableRows, TableEmpty, ImageSurface, TextView, TextBody, HtmlView, LoadingRing, MediaSurface, MediaBar, PlayButton, SeekSlider, MediaTimeText, MuteButton,
                        VolumeSlider, FooterBar, HintText, FooterText })
                    {
                        var origin = element.TransformToVisual(ShellRoot).TransformPoint(new Windows.Foundation.Point());
                        controls[element.Name] = new { origin.X, origin.Y, Width = element.ActualWidth, Height = element.ActualHeight,
                            Visible = element.Visibility == Visibility.Visible };
                    }
                    reports.Add(new { name, width, naturalWidth, naturalHeight, RootWidth = ShellRoot.ActualWidth, RootHeight = ShellRoot.ActualHeight,
                        Status = ViewStatus.Text, Entries = EntryList.Items.Count, Rows = TableRows.Items.Count, loadMs, readyMs,
                        PdfPageCount = _pdf?.PageCount, PdfVisible = PdfView.Visibility == Visibility.Visible,
                        PdfSourceMatchesOriginal = _pdfPath == CurrentPath, OriginalPath = CurrentPath,
                        WaitingTextShown = waitingText, WaitingSpinnerActive = waitingSpinner, OfficeRenderer = _officePreviewBackend,
                        ModelReady = _modelReady, ModelIdleFramesStable = modelIdleFramesStable, ModelBlockedRequests = _modelBlockedRequests, WebState = webState,
                        AnimationsEnabled, AnimationBusy = _previewAnimator.IsBusy, _previewAnimator.LastDurationMs,
                        _previewAnimator.LastMaxFrameGapMs, _previewAnimator.LastFrameCount,
                        HtmlSource = HtmlView.CoreWebView2?.Source,
                        HtmlReady = name.EndsWith(".html") && HtmlView.CoreWebView2 != null
                            ? await HtmlView.CoreWebView2.ExecuteScriptAsync("JSON.stringify({ready:document.readyState,title:document.title,text:document.body?document.body.innerText.slice(0,100):''})") : null,
                        Text = TextBody.Text[..Math.Min(TextBody.Text.Length, 160)], ExtentWidth = TextView.ExtentWidth,
                        TextViewportWidth = TextView.ViewportWidth, Media = _previewPlayer?.PlaybackSession.PlaybackState.ToString(),
                        Duration = _previewPlayer?.PlaybackSession.NaturalDuration.TotalSeconds, controls });
                }
                HideWindow(immediate: true);
            }
            await CapturePreviewSwitchChecksAsync(root);
            await CaptureModelFreshnessChecksAsync(root);
            // Static render/lifecycle checks only: no mouse, keyboard, hover, or progress interactions.
            var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
            for (var repeat = 0; repeat < 12; repeat++)
            {
                _paths = new[] { Path.Combine(root, "small.zip") }; _index = 0;
                Present(); await LoadCurrentAsync(); await Task.Delay(180);
                HideWindow(immediate: true);
            }
            await Task.Delay(300);
            File.WriteAllText(Path.Combine(root, "lifecycle-report.json"), JsonSerializer.Serialize(new
            {
                ManagedBefore = managedBefore, ManagedAfter = GC.GetTotalMemory(forceFullCollection: true),
                PrivateBytes = Process.GetCurrentProcess().PrivateMemorySize64,
                AnimationBusy = _previewAnimator.IsBusy, MediaTimerRunning = _mediaTimer?.IsRunning,
                Loading = LoadingRing.IsActive, NativeRowsRetained = EntryList.Items.Count,
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(root, "layout-report.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "capture-error.txt"), ex.ToString()); }
        finally { _capturingPreviewLayouts = false; HideWindow(immediate: true); }
    }

    private async Task CapturePreviewSwitchChecksAsync(string root)
    {
        var pdf = Path.Combine(root, "sample.pdf");
        if (!File.Exists(pdf)) return;
        _paths = new[] { pdf }; _index = 0; Present();
        var previous = LoadCurrentAsync();
        await Task.Delay(1);
        var latest = LoadCurrentAsync();
        await Task.WhenAll(previous, latest);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 3 && (_pdfImages.Count == 0 || _pdfImages[0].Source == null)) await Task.Delay(10);
        var pdfReady = _pdfPath == pdf && _pdfImages.Count > 0 && _pdfImages[0].Source != null;
        var html = Path.Combine(root, "sample.html");
        var model = Path.Combine(root, "sample.obj");
        var modelReady = false;
        if (File.Exists(html) && File.Exists(model))
        {
            _paths = new[] { html };
            previous = LoadCurrentAsync();
            _paths = new[] { model };
            latest = LoadCurrentAsync();
            await Task.WhenAll(previous, latest);
            modelReady = _modelReady && CurrentPath == model;
        }
        _paths = new[] { Path.Combine(root, "missing-owned-fixture.txt") };
        await LoadCurrentAsync();
        var missingFileShown = _currentKind != Kind.Folder && FallbackTitle.Text == "文件已移动或删除";
        var mappingReleased = !_modelActive && _modelUri == null;
        File.WriteAllText(Path.Combine(root, "switch-report.json"), JsonSerializer.Serialize(new
        {
            PdfReadyAfterReplacement = pdfReady, ModelReadyAfterHtmlReplacement = modelReady,
            MissingFileShown = missingFileShown, ModelMappingReleased = mappingReleased,
        }, new JsonSerializerOptions { WriteIndented = true }));
        HideWindow(immediate: true);
    }

    private async Task CaptureModelFreshnessChecksAsync(string root)
    {
        var first = Path.Combine(root, "model-one", "shared.fbx");
        var second = Path.Combine(root, "model-two", "shared.fbx");
        if (!File.Exists(first) || !File.Exists(second)) return;
        var triangles = new List<int>();
        var requests = new List<string>();
        try
        {
            foreach (var path in new[] { first, second, first })
            {
                if (triangles.Count == 2) File.Copy(second, first, overwrite: true); // Owned synthetic copy only.
                _paths = new[] { path }; _index = 0; Present();
                await LoadCurrentAsync();
                if (!_modelReady) throw new InvalidOperationException("Model freshness fixture failed");
                var web = HtmlView.CoreWebView2;
                triangles.Add(int.Parse(await web.ExecuteScriptAsync("window.__modelPreview.triangles")));
                requests.Add(await web.ExecuteScriptAsync("performance.getEntriesByType('resource').find(e=>e.name.includes('wintools-model-files.local')).name"));
                HideWindow(immediate: true);
            }
            File.WriteAllText(Path.Combine(root, "model-freshness-report.json"), JsonSerializer.Serialize(new
            {
                SameNameDifferentFolders = triangles[0] == 12 && triangles[1] == 970,
                UpdatedModelReloaded = triangles[2] == 970,
                UniqueLocalResourceURLs = requests[0] != requests[1] && requests[1] != requests[2], Triangles = triangles, Requests = requests,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { File.Copy(Path.Combine(root, "sample.fbx"), first, overwrite: true); HideWindow(immediate: true); }
    }

    private async Task CapturePreviewFrameAsync(string root, string name)
    {
        ShellRoot.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(ShellRoot);
        var pixels = await bitmap.GetPixelsAsync();
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var data = new byte[pixels.Length];
        using var reader = DataReader.FromBuffer(pixels);
        reader.ReadBytes(data);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
        await encoder.FlushAsync();
    }
}
#endif
