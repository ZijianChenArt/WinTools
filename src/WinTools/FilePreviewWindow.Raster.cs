using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinTools.Services;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private sealed class RasterPreview(string path, int width, int height, CancellationToken cancellation)
    {
        public string Path { get; } = path;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int LongEdge => Math.Max(Width, Height);
        public CancellationToken Cancellation { get; } = cancellation;
        public int RequestedEdge { get; set; }
        public int LoadedEdge { get; set; }
        public bool Upgrading { get; set; }
    }

    private RasterPreview? _rasterPreview;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _rasterUpgradeTimer;

    private async Task LoadRasterPreviewAsync(string path, int token, CancellationToken cancellation)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".png")
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation);
                using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(cancellation);
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellation);
                var raster = new RasterPreview(path, checked((int)decoder.OrientedPixelWidth),
                    checked((int)decoder.OrientedPixelHeight), cancellation);
                if (raster.Width > 0 && raster.Height > 0 && decoder.FrameCount == 1)
                {
                    var edge = InitialRasterEdge(raster);
                    if (edge < raster.LongEdge)
                    {
                        if (extension is ".jpg" or ".jpeg")
                            await TryShowEmbeddedThumbnailAsync(decoder, raster, token);

                        var bitmap = await DecodeRasterAsync(decoder, raster, edge);
                        cancellation.ThrowIfCancellationRequested();
                        _rasterPreview = raster;
                        raster.LoadedEdge = raster.RequestedEdge = edge;
                        ShowRaster(bitmap, raster, token);
                        QueueRasterUpgrade();
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The original WinUI path also handles codecs and files the metadata decoder rejects.
            }
        }

        cancellation.ThrowIfCancellationRequested();
        var original = new BitmapImage { CreateOptions = BitmapCreateOptions.IgnoreImageCache };
        original.ImageOpened += (_, _) => OnContentSized(token, path, original.PixelWidth, original.PixelHeight);
        original.ImageFailed += (_, e) => { if (token == _loadToken) ImageView_ImageFailed(ImageView, e); };
        ImageView.Source = original;
        ShowOnly(ImageSurface);
        original.UriSource = ToFileUri(path);
    }

    private async Task TryShowEmbeddedThumbnailAsync(BitmapDecoder decoder, RasterPreview raster, int token)
    {
        try
        {
            using var thumbnail = await decoder.GetThumbnailAsync().AsTask(raster.Cancellation);
            if (thumbnail == null) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail).AsTask(raster.Cancellation);
            raster.Cancellation.ThrowIfCancellationRequested();
            // Some cameras store a different crop or orientation in the embedded preview.
            if (bitmap.PixelWidth >= 96 && bitmap.PixelHeight >= 96 && Math.Abs(
                bitmap.PixelWidth / (double)bitmap.PixelHeight * raster.Height / raster.Width - 1) <= .05)
                ShowRaster(bitmap, raster, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An embedded thumbnail is optional. Continue with the window-sized image.
        }
    }

    private void ShowRaster(ImageSource bitmap, RasterPreview raster, int token)
    {
        ImageView.Source = bitmap;
        ShowOnly(ImageSurface);
        if (_contentSize == null)
            OnContentSized(token, raster.Path, raster.Width, raster.Height);
    }

    private int InitialRasterEdge(RasterPreview raster)
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = CurrentScale();
        var width = _userSized ? AppWindow.Size.Width : Math.Min(1100 * scale, work.Width * MaxWidthRatio);
        var height = _userSized ? AppWindow.Size.Height : work.Height * MaxHeightRatio;
        var fit = Math.Min(width / raster.Width, height / raster.Height);
        return LimitRasterEdge(raster, raster.LongEdge * fit);
    }

    private static int LimitRasterEdge(RasterPreview raster, double requested)
    {
        // Bound the whole-image zoom buffer to roughly 32 MiB at 32 bits per pixel.
        var maxEdge = Math.Min(4096, raster.LongEdge * Math.Sqrt(8_000_000d / raster.Width / raster.Height));
        return Math.Clamp((int)Math.Ceiling(Math.Min(requested, maxEdge)), 1, raster.LongEdge);
    }

    private static async Task<SoftwareBitmapSource> DecodeRasterAsync(BitmapDecoder decoder, RasterPreview raster, int edge)
    {
        var scale = edge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Linear,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb)
            .AsTask(raster.Cancellation);
        if (Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) != edge)
            throw new InvalidDataException("The image decoder returned unexpected dimensions.");
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap).AsTask(raster.Cancellation);
        return source;
    }

    private void QueueRasterUpgrade()
    {
        if (!_isOpen || _rasterPreview == null) return;
        if (_rasterUpgradeTimer == null)
        {
            _rasterUpgradeTimer = DispatcherQueue.CreateTimer();
            _rasterUpgradeTimer.Interval = TimeSpan.FromMilliseconds(150);
            _rasterUpgradeTimer.IsRepeating = false;
            _rasterUpgradeTimer.Tick += (_, _) => _ = UpgradeRasterAsync();
        }
        _rasterUpgradeTimer.Stop();
        _rasterUpgradeTimer.Start();
    }

    private async Task UpgradeRasterAsync()
    {
        if (!_isOpen || _rasterPreview is not { Upgrading: false } raster ||
            ImageSurface.Visibility != Visibility.Visible || ImageView.ActualWidth <= 0) return;

        var target = Math.Max(ImageView.ActualWidth, ImageView.ActualHeight) * CurrentScale() * ImageSurface.ZoomFactor;
        var edge = LimitRasterEdge(raster, target);
        if (edge <= raster.RequestedEdge || edge <= raster.LoadedEdge * 1.15) return;
        raster.RequestedEdge = edge;
        raster.Upgrading = true;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(raster.Path).AsTask(raster.Cancellation);
            using var stream = await file.OpenAsync(FileAccessMode.Read).AsTask(raster.Cancellation);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(raster.Cancellation);
            if (decoder.OrientedPixelWidth != raster.Width || decoder.OrientedPixelHeight != raster.Height) return;
            var bitmap = await DecodeRasterAsync(decoder, raster, edge);
            raster.Cancellation.ThrowIfCancellationRequested();
            ImageView.Source = bitmap;
            raster.LoadedEdge = edge;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorReporter.Log("FilePreviewWindow.UpgradeRaster", ex); }
        finally
        {
            raster.Upgrading = false;
            if (_rasterPreview == raster) QueueRasterUpgrade();
        }
    }
}
