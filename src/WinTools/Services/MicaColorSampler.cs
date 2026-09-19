using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinTools.Services;

/// <summary>
/// 用「壁纸取色 + Mica 着色配方」给不能激活的小窗口算出 Mica 底色。
/// </summary>
/// <remarks>
/// 真正的 Mica（<c>DWMWA_SYSTEMBACKDROP_TYPE</c> / <c>MicaBackdrop</c>）只在窗口处于激活状态时才显示材质，
/// 失焦就换成纯色 fallback——README 7.4 的桌面卡片就是为此改用 <c>MicaController</c> + <c>IsInputActive</c>。
/// 语音小球必须永远不激活（否则焦点离开文本框），又不能是 WinUI 窗口，系统材质用不上。
/// Mica 本身的效果就是「窗口所在屏幕位置的壁纸，高度模糊后按主题色调着色」，与窗口背后的内容无关，
/// 所以这里按同样的配方自己算：取壁纸对应位置的平均色，保留色相与饱和度、亮度换成主题色调的亮度，
/// 再按 Mica 的着色不透明度（深色 #202020 × 0.8，浅色 #F3F3F3 × 0.5）混合。
/// </remarks>
internal sealed class MicaColorSampler : IDisposable
{
    private const int ThumbWidth = 64;

    private Bitmap? _thumb;
    private string? _thumbKey;

    public Color GetMicaColor(int monitorLeft, int monitorTop, int monitorWidth, int monitorHeight, int x, int y, bool dark)
    {
        var tint = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        var tintOpacity = dark ? 0.8 : 0.5;

        var wallpaper = SampleWallpaper(monitorLeft, monitorTop, monitorWidth, monitorHeight, x, y) ?? DesktopColor();
        var luminosity = WithLightness(wallpaper, Lightness(tint));
        return Lerp(luminosity, tint, tintOpacity);
    }

    public void Dispose()
    {
        _thumb?.Dispose();
        _thumb = null;
    }

    private Color? SampleWallpaper(int monitorLeft, int monitorTop, int monitorWidth, int monitorHeight, int x, int y)
    {
        if (monitorWidth <= 0 || monitorHeight <= 0) return null;
        var thumb = GetThumbnail(monitorWidth, monitorHeight);
        if (thumb == null) return null;

        var u = (int)((x - monitorLeft) / (double)monitorWidth * thumb.Width);
        var v = (int)((y - monitorTop) / (double)monitorHeight * thumb.Height);
        int r = 0, g = 0, b = 0, n = 0;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            var px = Math.Clamp(u + dx, 0, thumb.Width - 1);
            var py = Math.Clamp(v + dy, 0, thumb.Height - 1);
            var c = thumb.GetPixel(px, py);
            r += c.R; g += c.G; b += c.B; n++;
        }
        return Color.FromArgb(r / n, g / n, b / n);
    }

    /// <summary>把壁纸按「填充」方式裁到显示器比例后缩成 64px 宽：缩放本身就相当于 Mica 的大半径模糊。</summary>
    private Bitmap? GetThumbnail(int monitorWidth, int monitorHeight)
    {
        var path = GetWallpaperPath();
        if (path == null) return null;

        string key;
        try { key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{monitorWidth}x{monitorHeight}"; }
        catch { return null; }
        if (key == _thumbKey) return _thumb;

        _thumb?.Dispose();
        _thumb = null;
        _thumbKey = key;
        try
        {
            // 先整体读进内存再解码，避免 Image.FromFile 锁住壁纸文件导致系统换不了壁纸。
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var image = Image.FromStream(stream);

            var targetAspect = monitorWidth / (double)monitorHeight;
            var imageAspect = image.Width / (double)image.Height;
            RectangleF source = imageAspect > targetAspect
                ? new RectangleF((float)((image.Width - image.Height * targetAspect) / 2), 0, (float)(image.Height * targetAspect), image.Height)
                : new RectangleF(0, (float)((image.Height - image.Width / targetAspect) / 2), image.Width, (float)(image.Width / targetAspect));

            var height = Math.Max(1, (int)Math.Round(ThumbWidth / targetAspect));
            var thumb = new Bitmap(ThumbWidth, height);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, new RectangleF(0, 0, ThumbWidth, height), source, GraphicsUnit.Pixel);
            }
            _thumb = thumb;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("MicaColorSampler.GetThumbnail", ex);
        }
        return _thumb;
    }

    private static string? GetWallpaperPath()
    {
        var buffer = new StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Capacity, buffer, 0) && File.Exists(buffer.ToString()))
            return buffer.ToString();

        // 幻灯片 / Windows 聚焦等情况下上面给出的路径可能不存在，系统当前实际显示的是这份转码副本。
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    private static Color DesktopColor()
    {
        var bgr = GetSysColor(COLOR_DESKTOP);
        return Color.FromArgb((int)(bgr & 0xFF), (int)((bgr >> 8) & 0xFF), (int)((bgr >> 16) & 0xFF));
    }

    private static double Lightness(Color c) => (Math.Max(c.R, Math.Max(c.G, c.B)) + Math.Min(c.R, Math.Min(c.G, c.B))) / 510.0;

    /// <summary>保留色相与饱和度，把 HSL 亮度换成指定值。</summary>
    private static Color WithLightness(Color c, double lightness)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2;
        if (max > min)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }

        l = lightness;
        if (s == 0)
        {
            var gray = (int)Math.Round(l * 255);
            return Color.FromArgb(gray, gray, gray);
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb(
            (int)Math.Round(HueToRgb(p, q, h + 1.0 / 3) * 255),
            (int)Math.Round(HueToRgb(p, q, h) * 255),
            (int)Math.Round(HueToRgb(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    private static Color Lerp(Color from, Color to, double amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    private const uint SPI_GETDESKWALLPAPER = 0x0073;
    private const int COLOR_DESKTOP = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buffer, uint winIni);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);
}
