using System;
using System.Linq;
using WinTools.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}

var sizes = new[] { (Width: 200, Height: 200), (Width: 160, Height: 200), (Width: 200, Height: 200) };
var desktop = DesktopCardLayout.Calculate(0, 0, 1200, 600, 20, 10, sizes, false);
var original = desktop.ToArray();
var library = DesktopCardLayout.Calculate(0, 0, 1200, 600, 20, 10, sizes, true);
Check(desktop.SequenceEqual(original), "Opening a library must not mutate desktop positions");
Check(desktop.SequenceEqual(new[] { (980, 20), (1020, 230), (770, 20) }), "Desktop must retain its right-aligned columns");
Check(library.SequenceEqual(new[] { (20, 20), (20, 230), (230, 20) }), "Library must independently wrap columns from the left");
Check(DesktopCardLayout.Calculate(0, 0, 1200, 600, 20, 10, sizes, false).SequenceEqual(original), "Desktop relayout after library opening must remain on the right");
var offset = DesktopCardLayout.Calculate(-1200, -100, 1200, 600, 20, 10, sizes, true);
Check(offset.SequenceEqual(new[] { (-1180, -80), (-1180, 130), (-970, -80) }), "Respect negative monitor coordinates");
Check(DesktopCardLayout.Calculate(0, 0, 1200, 600, 20, 10, Array.Empty<(int, int)>(), true).Count == 0, "Empty library must have no placements");
Console.WriteLine("6 desktop/library layout checks passed.");
