using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private GraphicsPath[]? _featureIconPaths;
    private float _featureIconScale;
    private string? _measuredInfo;
    private float _measuredScale;
    private int _infoWidth;

    private int MeasureInfoWidth(string text, float scale)
    {
        if (_measuredInfo == text && _measuredScale == scale) return _infoWidth;
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        _infoWidth = (int)System.Math.Ceiling(System.Math.Clamp(graphics.MeasureString(text, font).Width + 24 * scale, 100 * scale, 240 * scale));
        _measuredInfo = text;
        _measuredScale = scale;
        return _infoWidth;
    }

    private void DrawFeatureIcons(Graphics graphics, Brush brush, float scale, int height)
    {
        if (_featureIconPaths == null || _featureIconScale != scale)
        {
            DisposeFeatureIcons();
            var glyphs = new[] { FeatureIcons.DesktopCards, FeatureIcons.DragStash, FeatureIcons.Voice, FeatureIcons.AudioDevices };
            _featureIconPaths = new GraphicsPath[glyphs.Length];
            using var font = new FontFamily(FeatureIcons.FontName);
            for (var index = 0; index < glyphs.Length; index++)
            {
                var path = new GraphicsPath();
                _featureIconPaths[index] = path;
                path.AddString(glyphs[index], font, (int)FontStyle.Regular, 18 * scale, PointF.Empty, StringFormat.GenericTypographic);
                var bounds = path.GetBounds();
                using var matrix = new Matrix();
                matrix.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
                path.Transform(matrix);
            }
            _featureIconScale = scale;
        }
        for (var index = 0; index < _featureIconPaths.Length; index++)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(index == 3 ? _deviceIconCenter : _buttonStart + (index + .5f) * _buttonWidth, height / 2f);
            graphics.FillPath(brush, _featureIconPaths[index]);
            graphics.Restore(state);
        }
    }

    private void DisposeFeatureIcons()
    {
        if (_featureIconPaths != null)
            foreach (var path in _featureIconPaths) path?.Dispose();
        _featureIconPaths = null;
    }

    private static void DrawChip(Graphics graphics, RectangleF rect, float scale, bool dark, bool hover)
    {
        rect.Inflate(-scale, -scale);
        var chipHeight = System.Math.Min(34 * scale, rect.Height);
        rect.Y += (rect.Height - chipHeight) / 2;
        rect.Height = chipHeight;
        var diameter = System.Math.Min(rect.Width, rect.Height);
        if (diameter <= 0) return;
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        using var stroke = new Pen(dark ? Color.FromArgb(hover ? 180 : 100, 255, 255, 255) : Color.FromArgb(hover ? 150 : 80, 0, 0, 0), 1.4f * scale);
        graphics.DrawPath(stroke, path);
    }
}
