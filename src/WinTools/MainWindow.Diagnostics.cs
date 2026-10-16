#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            var reports = new List<object>();
            foreach (var (name, width, height, open) in new[]
            {
                ("expanded", 1040, 760, true),
                ("compact", 760, 650, false),
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
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(ShellRoot);
                var pixels = await bitmap.GetPixelsAsync();
                var file = await StorageFile.GetFileFromPathAsync(CreateCaptureFile(output, name));
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
            File.WriteAllText(Path.Combine(output, "layout-report.json"),
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "capture-error.txt"), ex.ToString());
        }
    }

    private static string CreateCaptureFile(string output, string name)
    {
        var path = Path.GetFullPath(Path.Combine(output, name + ".png"));
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }
}
#endif
