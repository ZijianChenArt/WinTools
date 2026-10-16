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
                "data.csv", "book.xlsx", "data.json", "broken.json", "large.json", "sample.docx", "slides.pptx", "sample.html", "sample.pdf", "sample.svg", "tiny.png", "sample.blend", "sample.wav", "sample.mp4", "portrait.mp4" })
            {
                var path = Path.Combine(root, name);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                _paths = new[] { path };
                _index = 0;
                Present();
                var loadingClock = Stopwatch.StartNew();
                await LoadCurrentAsync();
                var loadMs = loadingClock.Elapsed.TotalMilliseconds;
                await Task.Delay(1800);
                var naturalWidth = AppWindow.Size.Width / CurrentScale();
                var naturalHeight = AppWindow.Size.Height / CurrentScale();
                foreach (var width in new[] { 0, 420, 960 })
                {
                    var scale = CurrentScale();
                    if (width > 0) AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(480 * scale)));
                    await Task.Delay(400);
                    ShellRoot.UpdateLayout();
                    var bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(ShellRoot);
                    var pixels = await bitmap.GetPixelsAsync();
                    var file = await StorageFile.GetFileFromPathAsync(Path.Combine(root, "capture-target.png"));
                    using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                        var data = new byte[pixels.Length];
                        using var reader = DataReader.FromBuffer(pixels);
                        reader.ReadBytes(data);
                        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, data);
                        await encoder.FlushAsync();
                    }
                    File.Copy(file.Path, Path.Combine(root, $"{name}-{width}.png"), true);
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
                        Status = ViewStatus.Text, Entries = EntryList.Items.Count, Rows = TableRows.Items.Count, loadMs,
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
}
#endif
