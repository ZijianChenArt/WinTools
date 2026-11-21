using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace WinTools;

internal sealed class ListScrollbar
{
    private readonly ScrollBar _bar;
    private readonly ListViewBase _list;
    private ScrollViewer? _viewer;
    private long _rangeCallback, _viewportCallback;

    internal static void Attach(ScrollBar bar, ListViewBase list) => _ = new ListScrollbar(bar, list);

    private ListScrollbar(ScrollBar bar, ListViewBase list)
    {
        _bar = bar;
        _list = list;
        bar.Loaded += (_, _) => Connect();
        bar.Unloaded += (_, _) => Disconnect();
        list.Loaded += (_, _) => Connect();
        list.Unloaded += (_, _) => Disconnect();
        bar.Scroll += (_, args) => _viewer?.ChangeView(null, args.NewValue, null, disableAnimation: true);
        bar.PointerWheelChanged += OnWheel;
    }

    private void Connect()
    {
        if (!_list.IsLoaded) return;
        _list.ApplyTemplate();
        var viewer = FindScrollViewer(_list);
        if (ReferenceEquals(viewer, _viewer)) return;
        Disconnect();
        if (viewer == null) return;
        _viewer = viewer;
        viewer.ViewChanged += ViewerChanged;
        _rangeCallback = viewer.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, ViewportChanged);
        _viewportCallback = viewer.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, ViewportChanged);
        UpdateRange();
    }

    private void Disconnect()
    {
        if (_viewer == null) return;
        _viewer.ViewChanged -= ViewerChanged;
        _viewer.UnregisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, _rangeCallback);
        _viewer.UnregisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, _viewportCallback);
        _viewer = null;
        _bar.Visibility = Visibility.Collapsed;
    }

    private void ViewerChanged(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateRange();
    private void ViewportChanged(DependencyObject sender, DependencyProperty property) => UpdateRange();

    private void UpdateRange()
    {
        if (_viewer is not { } viewer) return;
        _bar.Maximum = viewer.ScrollableHeight;
        _bar.ViewportSize = viewer.ViewportHeight;
        _bar.LargeChange = viewer.ViewportHeight;
        _bar.SmallChange = 40;
        _bar.Value = viewer.VerticalOffset;
        _bar.Visibility = _bar.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        if (_viewer is not { } viewer) return;
        var delta = args.GetCurrentPoint(_bar).Properties.MouseWheelDelta;
        viewer.ChangeView(null, viewer.VerticalOffset - delta, null);
        args.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }
}
