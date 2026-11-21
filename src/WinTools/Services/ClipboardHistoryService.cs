using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinTools.Services;

internal sealed class ClipboardHistoryService : IDisposable
{
    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "ClipboardHistory");
    private static readonly uint ExcludeFormat = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");

    private readonly DispatcherQueue _dispatcher;
    private readonly string _root;
    private readonly SemaphoreSlim _writes = new(1);
    private readonly Task _initialization;
    private bool _recording;
    private uint _lastSequence;
    private uint _restoredSequence;
    private int _recordingVersion;
    private int _historyVersion;

    private sealed record Payload(string? Text, string? Html, string? Rtf, string[] Paths, string? Link);
    private sealed record Metadata(DateTimeOffset Time, string Type, string Summary, bool HasBitmap);

    internal ClipboardHistoryService(DispatcherQueue dispatcher, string? storageRoot = null)
    {
        _dispatcher = dispatcher;
        _root = storageRoot ?? DefaultRoot;
        _initialization = LoadAsync();
    }

    internal ObservableCollection<ClipboardHistoryItem> Items { get; } = new();

    internal void SetRecording(bool value)
    {
        if (_recording == value) return;
        _recordingVersion++;
        _recording = value;
        if (value)
        {
            _lastSequence = GetClipboardSequenceNumber();
            Clipboard.ContentChanged += OnClipboardChanged;
        }
        else Clipboard.ContentChanged -= OnClipboardChanged;
    }

    private async Task LoadAsync()
    {
        var items = await Task.Run(() =>
        {
            var existing = new List<ClipboardHistoryItem>();
            if (!Directory.Exists(_root)) return existing;
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                try
                {
                    var meta = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(Path.Combine(directory, "info.json")));
                    if (meta != null) existing.Add(CreateItem(directory, meta));
                }
                catch (Exception ex) { ErrorReporter.Log("ClipboardHistory.LoadItem", ex); }
            }
            return existing;
        }).ConfigureAwait(false);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            foreach (var item in items.OrderByDescending(item => item.Time)) Items.Add(item);
            ready.TrySetResult();
        })) ready.TrySetCanceled();
        await ready.Task;
    }

    private void OnClipboardChanged(object? sender, object? args)
    {
        var sequence = GetClipboardSequenceNumber();
        _dispatcher.TryEnqueue(() => _ = CaptureAsync(sequence));
    }

    private async Task CaptureAsync(uint sequence)
    {
        if (!_recording || sequence == _restoredSequence || sequence == _lastSequence) return;
        var version = _recordingVersion;
        var historyVersion = _historyVersion;
        _lastSequence = sequence;
        try
        {
            DataPackageView? view = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (version != _recordingVersion || sequence != GetClipboardSequenceNumber()
                    || (ExcludeFormat != 0 && IsClipboardFormatAvailable(ExcludeFormat))) return;
                try { view = Clipboard.GetContent(); break; }
                catch (COMException) when (attempt < 3) { await Task.Delay(40); }
            }
            if (view == null || sequence != GetClipboardSequenceNumber()) return;

            await CaptureViewAsync(view, version, historyVersion);
        }
        catch (Exception ex) { ErrorReporter.Log("ClipboardHistory.Capture", ex); }
    }

    private async Task<ClipboardHistoryItem?> CaptureViewAsync(DataPackageView view, int version, int historyVersion)
    {
        var time = DateTimeOffset.Now;
        var text = await ReadFormat(StandardDataFormats.Text, async () => await view.GetTextAsync());
        var html = await ReadFormat(StandardDataFormats.Html, async () => await view.GetHtmlFormatAsync());
        var rtf = await ReadFormat(StandardDataFormats.Rtf, async () => await view.GetRtfAsync());
        var link = await ReadFormat(StandardDataFormats.WebLink, async () => (await view.GetWebLinkAsync()).AbsoluteUri);
        var paths = await ReadFormat(StandardDataFormats.StorageItems, async () =>
            (await view.GetStorageItemsAsync()).Select(item => item.Path).Where(path => !string.IsNullOrEmpty(path)).ToArray());
        var bitmap = await ReadFormat(StandardDataFormats.Bitmap, async () => await view.GetBitmapAsync());
        if (text == null && html == null && rtf == null && link == null && paths is not { Length: > 0 } && bitmap == null) return null;
        return await SaveAsync(time, new Payload(text, html, rtf, paths ?? Array.Empty<string>(), link), bitmap, version, historyVersion);

        async Task<T?> ReadFormat<T>(string format, Func<Task<T>> read) where T : class
        {
            if (!view.Contains(format)) return null;
            try { return await read(); }
            catch (Exception ex) { ErrorReporter.Log("ClipboardHistory.ReadFormat", $"{format}: {ex.HResult:X8}"); return null; }
        }
    }

    private async Task<ClipboardHistoryItem?> SaveAsync(DateTimeOffset time, Payload payload, RandomAccessStreamReference? bitmap,
        int version, int historyVersion)
    {
        await _initialization;
        await _writes.WaitAsync();
        try
        {
            if (version != _recordingVersion || historyVersion != _historyVersion) return null;
            var item = await Task.Run(async () =>
            {
                var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
                var staging = directory + ".pending";
                Directory.CreateDirectory(staging);
                try
                {
                    var hasBitmap = bitmap != null;
                    if (bitmap != null)
                    {
                        try
                        {
                            using var source = await bitmap.OpenReadAsync();
                            using var input = source.AsStreamForRead();
                            await using var output = File.Create(Path.Combine(staging, "bitmap"));
                            await input.CopyToAsync(output);
                        }
                        catch (Exception ex)
                        {
                            ErrorReporter.Log("ClipboardHistory.Bitmap", ex);
                            hasBitmap = false;
                            File.Delete(Path.Combine(staging, "bitmap"));
                            if (payload is { Text: null, Html: null, Rtf: null, Link: null, Paths.Length: 0 }) return null;
                        }
                    }
                    var type = payload.Paths.Length > 0 ? "文件" : hasBitmap ? "图片"
                        : payload.Html != null || payload.Rtf != null ? "富文本" : payload.Link != null ? "链接" : "文本";
                    var summary = payload.Paths.Length > 0 ? string.Join("、", payload.Paths.Select(Path.GetFileName))
                        : payload.Text ?? payload.Link ?? (hasBitmap ? "剪贴板图片" : "格式化内容");
                    var meta = new Metadata(time, type, summary.Length > 300 ? summary[..300] + "…" : summary, hasBitmap);
                    await File.WriteAllTextAsync(Path.Combine(staging, "content.json"), JsonSerializer.Serialize(payload));
                    await File.WriteAllTextAsync(Path.Combine(staging, "info.json"), JsonSerializer.Serialize(meta));
                    Directory.Move(staging, directory);
                    return CreateItem(directory, meta);
                }
                finally { DeleteFiles(staging); }
            });
            if (item == null) return null;
            var index = 0;
            while (index < Items.Count && Items[index].Time > item.Time) index++;
            Items.Insert(index, item);
            return item;
        }
        finally { _writes.Release(); }
    }

    internal async Task RestoreAsync(ClipboardHistoryItem item)
    {
        var data = await BuildPackageAsync(item);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        _restoredSequence = GetClipboardSequenceNumber();
    }

    private static async Task<DataPackage> BuildPackageAsync(ClipboardHistoryItem item)
    {
        var payload = await Task.Run(async () => JsonSerializer.Deserialize<Payload>(
            await File.ReadAllTextAsync(Path.Combine(item.Directory, "content.json"))))
            ?? throw new InvalidDataException("无法读取这条剪贴记录。");
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (payload.Text != null) data.SetText(payload.Text);
        if (payload.Html != null) data.SetHtmlFormat(payload.Html);
        if (payload.Rtf != null) data.SetRtf(payload.Rtf);
        if (Uri.TryCreate(payload.Link, UriKind.Absolute, out var uri)) data.SetWebLink(uri);
        if (payload.Paths.Length != 0)
        {
            var files = new List<IStorageItem>();
            foreach (var path in payload.Paths)
                files.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
            data.SetStorageItems(files);
        }
        if (item.BitmapPath is { } bitmap)
        {
            var file = await StorageFile.GetFileFromPathAsync(bitmap);
            data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        }
        return data;
    }

    internal async Task DeleteAsync(ClipboardHistoryItem? item)
    {
        if (item == null) _historyVersion++;
        await _initialization;
        await _writes.WaitAsync();
        try
        {
            var entries = item != null ? new[] { item } : Items.ToArray();
            foreach (var entry in entries)
            {
                await Task.Run(() => DeleteFiles(entry.Directory));
                Items.Remove(entry);
            }
        }
        finally { _writes.Release(); }
    }

    private static ClipboardHistoryItem CreateItem(string directory, Metadata meta) =>
        new(directory, meta.Time, meta.Type, meta.Summary, meta.HasBitmap ? Path.Combine(directory, "bitmap") : null);

    private static void DeleteFiles(string directory)
    {
        if (!Directory.Exists(directory)) return;
        File.Delete(Path.Combine(directory, "bitmap"));
        File.Delete(Path.Combine(directory, "content.json"));
        File.Delete(Path.Combine(directory, "info.json"));
        Directory.Delete(directory);
    }

#if DEBUG
    // Round-trip synthetic clipboard packages in isolated storage without changing the system clipboard.
    internal async Task RunDiagnosticsAsync()
    {
        try
        {
            Directory.CreateDirectory(_root);
            const string text = "WinTools 剪贴板测试文字：这是一条足够长的记录，用来检查过长内容会在同一行内截断。\n第二行原文";
            var rich = new DataPackage();
            rich.SetText(text);
            rich.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<strong>WinTools QA</strong>"));
            rich.SetRtf("{\\rtf1\\ansi WinTools QA}");
            rich.SetWebLink(new Uri("https://example.com/"));
            var richItem = await Capture(rich);
            var richView = (await BuildPackageAsync(richItem)).GetView();
            if (await richView.GetTextAsync() != text || !(await richView.GetHtmlFormatAsync()).Contains("WinTools QA")
                || !(await richView.GetRtfAsync()).Contains("WinTools QA")
                || (await richView.GetWebLinkAsync()).Host != "example.com")
                throw new InvalidDataException("Text, HTML, RTF or link was not restored.");

            using var image = new InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, image);
            var pixels = Enumerable.Repeat(new byte[] { 192, 117, 32, 255 }, 32 * 24).SelectMany(pixel => pixel).ToArray();
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 32, 24, 96, 96, pixels);
            await encoder.FlushAsync();
            image.Seek(0);
            var picture = new DataPackage();
            picture.SetBitmap(RandomAccessStreamReference.CreateFromStream(image));
            var pictureItem = await Capture(picture);
            var pictureView = (await BuildPackageAsync(pictureItem)).GetView();
            var bitmap = await pictureView.GetBitmapAsync();
            using var bitmapStream = await bitmap.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(bitmapStream);
            if (decoder.PixelWidth != 32 || decoder.PixelHeight != 24)
                throw new InvalidDataException("Bitmap dimensions changed on restore.");

            var path = Path.Combine(_root, "WinTools QA.txt");
            await File.WriteAllTextAsync(path, text);
            var fileList = new DataPackage();
            fileList.SetStorageItems(new[] { await StorageFile.GetFileFromPathAsync(path) });
            var fileItem = await Capture(fileList);
            var fileView = (await BuildPackageAsync(fileItem)).GetView();
            if ((await fileView.GetStorageItemsAsync()).Single().Path != path)
                throw new InvalidDataException("File list was not restored.");

            var temporary = await Capture(rich);
            await DeleteAsync(temporary);
            if (Items.Contains(temporary) || Directory.Exists(temporary.Directory))
                throw new InvalidOperationException("Deleted clipboard item was retained.");
            await File.WriteAllTextAsync(Path.Combine(_root, "round-trip.txt"),
                "Text, HTML, RTF, link, bitmap, file list and deletion passed.");
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(_root, "round-trip-error.txt"), ex.ToString());
        }

        async Task<ClipboardHistoryItem> Capture(DataPackage data) =>
            await CaptureViewAsync(data.GetView(), _recordingVersion, _historyVersion)
            ?? throw new InvalidOperationException("No clipboard history item was created.");
    }
#endif

    public void Dispose() => SetRecording(false);

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);
}

public sealed record ClipboardHistoryItem(string Directory, DateTimeOffset Time, string Type, string Summary, string? BitmapPath)
{
    public Microsoft.UI.Xaml.Visibility ThumbnailVisibility => BitmapPath == null
        ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Thumbnail => BitmapPath == null ? null : new()
    {
        DecodePixelWidth = 64,
        DecodePixelHeight = 64,
        UriSource = new UriBuilder { Scheme = Uri.UriSchemeFile, Host = "", Path = BitmapPath }.Uri,
    };
}
