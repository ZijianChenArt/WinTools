using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinTools;

/// <summary>
/// 桌面卡片里图标的换行面板：列宽固定，**每一行的高度取这一行里最高的那个格子**。
/// </summary>
/// <remarks>
/// 为什么不能继续用 <c>ItemsWrapGrid</c>：它是均匀网格，所有格子共用同一个尺寸，于是每个格子
/// 都得按最坏情况（两行文件名）预留高度。结果就是凡是文件名只有一行的那些行，下面都空一截，
/// 行数越多越明显。Windows 桌面的规则是"同一行里有谁需要两行文件名，才把这一行撑高"，
/// 这里按同样的规则实现。
///
/// 代价是没有虚拟化。一张卡片几十个图标本来就是全部实例化（这个尺寸下 ItemsWrapGrid 也不会
/// 回收容器），不构成问题。
///
/// **换了面板以后 GridView 的内置重排就不干活了**：拖得起来，松手却没人执行插入
/// （2026-09-18 用户反馈"图标排序失效"）。那套内置重排只认 ItemsWrapGrid / ItemsStackPanel，
/// 实现 <see cref="IInsertionPanel"/> 也不够。所以卡片内重排改由 DesktopCardWindow 在 Drop 里
/// 自己做，插入点用 <see cref="GetInsertionIndex"/> 算。注意 GridView 的 CanReorderItems 仍须为
/// True——设成 False 后拖动会话不再把 DragOver/Drop 发回源列表，自己处理也无从下手。
/// </remarks>
internal sealed partial class CardTilesPanel : Panel, IInsertionPanel
{
    private readonly List<double> _rowHeights = new();
    private int _columns = 1;
    private double _itemWidth = 76;

    /// <summary>每一列的固定宽度（DIP），与 ItemTemplate 里格子的宽度保持一致。</summary>
    public double ItemWidth
    {
        get => _itemWidth;
        set
        {
            if (Math.Abs(_itemWidth - value) < 0.01) return;
            _itemWidth = value > 0 ? value : 1;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _rowHeights.Clear();

        var itemWidth = _itemWidth > 0 ? _itemWidth : 1;
        var usable = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? itemWidth
            : availableSize.Width;
        // +0.001：可用宽度经常是 243.99999 这种浮点结果，直接 Floor 会少算一列。
        _columns = Math.Max(1, (int)Math.Floor(usable / itemWidth + 0.001));

        double rowHeight = 0;
        var inRow = 0;
        foreach (var child in Children)
        {
            // 高度给无限：格子自己按文件名占 1 行还是 2 行决定 DesiredSize。
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++inRow < _columns) continue;
            _rowHeights.Add(rowHeight);
            rowHeight = 0;
            inRow = 0;
        }
        if (inRow > 0) _rowHeights.Add(rowHeight);

        return new Size(_columns * itemWidth, TotalHeight());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = _itemWidth > 0 ? _itemWidth : 1;
        double y = 0;
        var index = 0;
        var row = 0;

        foreach (var child in Children)
        {
            var column = index % _columns;
            if (column == 0 && index > 0)
            {
                y += row < _rowHeights.Count ? _rowHeights[row] : 0;
                row++;
            }
            var height = row < _rowHeights.Count ? _rowHeights[row] : child.DesiredSize.Height;
            child.Arrange(new Rect(column * itemWidth, y, itemWidth, height));
            index++;
        }

        return new Size(_columns * itemWidth, TotalHeight());
    }

    private double TotalHeight()
    {
        double total = 0;
        foreach (var height in _rowHeights) total += height;
        return total;
    }

    /// <summary>
    /// 告诉 ListViewBase：松手位置落在哪两个元素中间。
    /// <paramref name="first"/> 是插入点前面那个、<paramref name="second"/> 是后面那个。
    /// </summary>
    public void GetInsertionIndexes(Point position, out int first, out int second)
    {
        first = second = 0;
        var count = Children.Count;
        if (count == 0) return;

        var insertion = GetInsertionIndex(position);
        first = Math.Clamp(insertion - 1, 0, count - 1);
        second = Math.Clamp(insertion, 0, count - 1);
    }

    /// <summary>
    /// 松手位置对应的插入点：0 = 最前面，<c>Children.Count</c> = 最后面。
    /// 卡片内拖动排序由 DesktopCardWindow 自己用它算目标位置（见那边 Drop 的注释）。
    /// </summary>
    public int GetInsertionIndex(Point position)
    {
        var count = Children.Count;
        if (count == 0) return 0;

        // 先按累计行高找到落在第几行。
        var row = 0;
        double y = 0;
        while (row < _rowHeights.Count - 1 && position.Y >= y + _rowHeights[row])
        {
            y += _rowHeights[row];
            row++;
        }

        var itemWidth = _itemWidth > 0 ? _itemWidth : 1;
        var column = (int)Math.Floor(position.X / itemWidth);
        column = Math.Clamp(column, 0, Math.Max(0, _columns - 1));

        // 落在格子左半边 = 插到它前面，右半边 = 插到它后面。
        var index = row * _columns + column;
        var insertion = position.X - column * itemWidth < itemWidth / 2 ? index : index + 1;
        return Math.Clamp(insertion, 0, count);
    }
}
