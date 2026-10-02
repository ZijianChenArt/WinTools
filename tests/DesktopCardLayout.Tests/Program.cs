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

// ---- Fit：放不下时逐级收紧 ----
// 放得下：和原来的严格顺序排版完全一致，高度不变。
var fitOk = DesktopCardLayout.Fit(0, 0, 1200, 600, 20, 10, sizes, false);
Check(fitOk.Select(p => (p.X, p.Y)).SequenceEqual(original), "Fit must keep the sequential layout when everything fits");
Check(fitOk.Select(p => p.Height).SequenceEqual(sizes.Select(s => s.Height)), "Fit must not cap heights when everything fits");

// 见缝插针：第一列只有一张矮卡，第二张高卡换了列，第三张卡严格顺序会再开第三列（总宽 340 > 300），
// 但它放得进第一列底部的空位（两列总宽 230）。
var fill = new[] { (Width: 100, Height: 100), (Width: 100, Height: 400), (Width: 100, Height: 300) };
var strict = DesktopCardLayout.Calculate(0, 0, 300, 450, 10, 10, fill, true);
Check(strict[2].X != 10, "Sequential layout does not fill the gap under the first card");
var filled = DesktopCardLayout.Fit(0, 0, 300, 450, 10, 10, fill, true);
Check(filled[2].X == 10 && filled[2].Y == 120, "Third card must fill the gap under the first card");
Check(filled[1].X == 120 && filled[1].Y == 10, "Tall card stays in its own column");
Check(filled.All(p => p.Height == fill[filled.ToList().IndexOf(p)].Height), "Gap filling must not cap heights");

// 收紧间距：三列 100 宽：20/20 需 380、12/12 需 348、8/8 需 332，宽度 340 只有最后一档放得下。
var three = new[] { (Width: 100, Height: 500), (Width: 100, Height: 500), (Width: 100, Height: 500) };
var tight = DesktopCardLayout.Fit(0, 0, 340, 540, 20, 20, three, true);
Check(tight[0].X == 8 && tight[1].X == 116 && tight[2].X == 224, "Gap and margin must shrink to 8 when width is short");
Check(tight.All(p => p.Height == 500), "Shrinking gap must not cap heights");
// 空间够了回到设置值（Fit 不记忆状态）。
var roomy = DesktopCardLayout.Fit(0, 0, 400, 540, 20, 20, three, true);
Check(roomy[0].X == 20 && roomy[1].X == 140 && roomy[2].X == 260, "Layout must return to configured gap when space allows");
// 间距设置本来就比 8 小时不会被放大。
var small = DesktopCardLayout.Fit(0, 0, 400, 540, 4, 4, three, true);
Check(small[0].X == 4 && small[1].X == 108, "Gap smaller than the tiers must stay as configured");

// 限高：四张高 500 的卡片在 320x540 里最小要 4 列（宽 4*100+3*8+16=440 > 320），必须压矮，让多张卡合列。
var four = new[] { (Width: 100, Height: 500), (Width: 100, Height: 500), (Width: 100, Height: 500), (Width: 100, Height: 500) };
var capped = DesktopCardLayout.Fit(0, 0, 320, 540, 8, 8, four, true);
Check(capped.All(p => p.X >= 8 && p.X + 100 <= 320 - 8), "Capped layout must stay inside the work area horizontally");
Check(capped.All(p => p.Height < 500 && p.Height >= DesktopCardLayout.MinCardHeight), "Overflowing cards must be capped within the minimum");
Check(capped.All(p => p.Y + p.Height <= 540 - 8), "Capped cards must stay inside the work area vertically");
for (var i = 0; i < capped.Count; i++)
    for (var j = i + 1; j < capped.Count; j++)
        Check(capped[i].X != capped[j].X || capped[i].Y + capped[i].Height <= capped[j].Y || capped[j].Y + capped[j].Height <= capped[i].Y,
            "Capped cards must not overlap");

// 单张卡片比一整列还高：任何情况下都被压到一列高。
var tall = DesktopCardLayout.Fit(0, 0, 1000, 400, 20, 10, new[] { (Width: 100, Height: 900) }, false);
Check(tall[0].Height == 360 && tall[0].Y == 20, "A card taller than the screen must be capped to one column");

// 桌面（靠右）与库（靠左）同样适用。
var right = DesktopCardLayout.Fit(0, 0, 340, 540, 20, 20, three, false);
Check(right[0].X == 232 && right[1].X == 124 && right[2].X == 16, "Right-aligned layout must also shrink gap");
Console.WriteLine("Desktop/library layout and Fit checks passed.");
