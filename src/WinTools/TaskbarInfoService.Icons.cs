using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WinTools;

internal sealed partial class TaskbarInfoService
{
    private GraphicsPath[]? _featureIconPaths;
    private float _featureIconScale;
    private readonly System.Collections.Generic.Dictionary<(string, float, bool), int> _measuredWidths = new();

    private int MeasureInfoWidth(string text, float scale, bool compact = false)
    {
        if (_measuredWidths.TryGetValue((text, scale, compact), out var cached)) return cached;
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var infoWidth = (int)System.Math.Ceiling(System.Math.Clamp(graphics.MeasureString(text, font).Width + 24 * scale, (compact ? 56 : 100) * scale, (text.StartsWith("Claude") ? 300 : 240) * scale));
        if (_measuredWidths.Count > 32) _measuredWidths.Clear();
        _measuredWidths[(text, scale, compact)] = infoWidth;
        return infoWidth;
    }

    private void DrawFeatureIcons(Graphics graphics, Brush brush, float scale, int height)
    {
        if (_featureIconPaths == null || _featureIconScale != scale)
        {
            DisposeFeatureIcons();
            var glyphs = new[] { FeatureIcons.DesktopCards, FeatureIcons.DragStash, FeatureIcons.Voice, FeatureIcons.AudioDevices, FeatureIcons.More };
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
        for (var slot = 0; slot < _shownIcons.Length; slot++)
        {
            var id = _shownIcons[slot];
            var state = graphics.Save();
            graphics.TranslateTransform(_buttonStart + (slot + .5f) * _buttonWidth, height / 2f);
            graphics.FillPath(brush, _featureIconPaths[id == MenuId ? 4 : id]);
            graphics.Restore(state);
        }
        if (_deviceWidth > 0)
        {
            var state = graphics.Save();
            graphics.TranslateTransform(_deviceIconCenter, height / 2f);
            graphics.FillPath(brush, _featureIconPaths[3]);
            graphics.Restore(state);
        }
    }

    /// <summary>入口上的状态：暂存文件数角标、语音听写中的红点。</summary>
    private void DrawBadges(Graphics graphics, float scale)
    {
        for (var slot = 0; slot < _shownIcons.Length; slot++)
        {
            var right = _buttonStart + (slot + 1) * _buttonWidth - 4 * scale;
            var top = 3 * scale;
            if (_shownIcons[slot] == 1 && _stashCount > 0)
            {
                var label = _stashCount > 99 ? "99+" : _stashCount.ToString();
                using var font = new Font("Microsoft YaHei UI", 9 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
                using var centered = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                var badgeHeight = 14 * scale;
                var badgeWidth = Math.Max(badgeHeight, graphics.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic).Width + 6 * scale);
                var rect = new RectangleF(right - badgeWidth, top, badgeWidth, badgeHeight);
                using var path = new GraphicsPath();
                path.AddArc(rect.Left, rect.Top, badgeHeight, badgeHeight, 90, 180);
                path.AddArc(rect.Right - badgeHeight, rect.Top, badgeHeight, badgeHeight, 270, 180);
                path.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(0, 120, 212));
                graphics.FillPath(fill, path);
                graphics.DrawString(label, font, Brushes.White, rect, centered);
            }
            else if (_shownIcons[slot] == 2 && _voiceActive)
            {
                var diameter = 8 * scale;
                using var fill = new SolidBrush(Color.FromArgb(232, 17, 35));
                graphics.FillEllipse(fill, right - diameter, top + 2 * scale, diameter, diameter);
            }
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
