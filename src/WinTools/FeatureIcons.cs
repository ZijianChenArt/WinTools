using System.Drawing;
using Microsoft.UI.Xaml.Controls;

namespace WinTools;

/// <summary>One glyph per feature, shared by navigation, menus and native taskbar rendering.</summary>
public static class FeatureIcons
{
    public static string ThreeFingerDrag => "\uE7C9";
    public static string DragStash => "\uE718";
    public static string ProgramAssociation => "\uE71B";
    public static string InputMethod => "\uE765";
    public static string DesktopCards => "\uF0E2";
    public static string DesktopClick => "\uE80F";
    public static string Search => "\uE721";
    public static string Voice => "\uE720";
    public static string TaskbarInfo => "\uE9D9";
    public static string Settings => "\uE713";
    public static string AudioDevices => "\uE95B";

    public static string FontName { get; } = ResolveFont();
    public static Microsoft.UI.Xaml.Media.FontFamily FontFamily => new(FontName);
    internal static FontIcon Create(string glyph) => new() { Glyph = glyph, FontFamily = FontFamily };

    private static string ResolveFont()
    {
        try { using var font = new System.Drawing.FontFamily("Segoe Fluent Icons"); return font.Name; }
        catch { return "Segoe MDL2 Assets"; }
    }
}
