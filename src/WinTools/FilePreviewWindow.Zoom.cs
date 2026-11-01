using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace WinTools;

public sealed partial class FilePreviewWindow
{
    private ScrollViewer? _panScroll;
    private Pointer? _panPointer;
    private Point _panStart;
    private double _panStartHorizontal, _panStartVertical;
    private IntPtr _panPreviousCursor;

    private void InitializeZoomInteractions()
    {
        InitializeZoomSurface(ImageSurface, ImageView);
        InitializeZoomSurface(PdfView, PdfPages);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) StopPreviewPan();
        };
    }

    private void InitializeZoomSurface(ScrollViewer scroll, FrameworkElement content)
    {
        // 内容先于 ScrollViewer 收到滚轮，标为已处理可避免系统再次缩放。
        // 空白视口保留 ScrollViewer 自带的 Ctrl+滚轮缩放；仅在它未处理时补上。
        content.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, args) => PreviewZoomWheel(scroll, content, args)), true);
        scroll.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, args) =>
            {
                if (!args.Handled) PreviewZoomWheel(scroll, content, args);
            }), true);
        scroll.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, args) => PreviewPanPressed(scroll, args)), true);
        scroll.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, args) => PreviewPanMoved(scroll, args)), true);
        scroll.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, args) =>
            {
                if (_panScroll == scroll && _panPointer?.PointerId == args.Pointer.PointerId)
                {
                    StopPreviewPan();
                    args.Handled = true;
                }
            }), true);
        scroll.PointerCanceled += (_, args) =>
        {
            if (_panPointer?.PointerId == args.Pointer.PointerId) StopPreviewPan();
        };
        scroll.PointerCaptureLost += (_, args) =>
        {
            if (_panPointer?.PointerId == args.Pointer.PointerId) StopPreviewPan();
        };
    }

    private void PreviewZoomWheel(ScrollViewer scroll, FrameworkElement content, PointerRoutedEventArgs args)
    {
        if (!_isOpen || scroll.Visibility != Visibility.Visible
            || !args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)
            || IsPreviewScrollControl(args.OriginalSource as DependencyObject, scroll)) return;
        var point = args.GetCurrentPoint(scroll);
        if (point.Properties.IsHorizontalMouseWheel || point.Properties.MouseWheelDelta == 0) return;

        args.Handled = true;
        StopPreviewPan();
        var zoom = (float)Math.Clamp(scroll.ZoomFactor * Math.Pow(1.2, point.Properties.MouseWheelDelta / 120d),
            scroll.MinZoomFactor, scroll.MaxZoomFactor);
        if (Math.Abs(zoom - scroll.ZoomFactor) < .0001) return;

        // 内容坐标已经去掉旧缩放；让鼠标指向的同一点在新缩放下仍落在原来的视口位置。
        var anchor = args.GetCurrentPoint(content).Position;
        var horizontal = Math.Max(0, anchor.X * zoom - point.Position.X);
        var vertical = Math.Max(0, anchor.Y * zoom - point.Position.Y);
        // ChangeView 根据新缩放后的实际范围夹取偏移，不能使用旧 ScrollableWidth/Height 限制放大。
        scroll.ChangeView(horizontal, vertical, zoom, disableAnimation: true);
    }

    private void PreviewPanPressed(ScrollViewer scroll, PointerRoutedEventArgs args)
    {
        if (!_isOpen || scroll.Visibility != Visibility.Visible
            || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || scroll.ZoomFactor <= scroll.MinZoomFactor + .001
            || IsPreviewScrollControl(args.OriginalSource as DependencyObject, scroll)) return;
        var point = args.GetCurrentPoint(scroll);
        if (!point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed
            || point.Properties.IsMiddleButtonPressed) return;

        StopPreviewPan();
        if (!scroll.CapturePointer(args.Pointer)) return;
        _panScroll = scroll;
        _panPointer = args.Pointer;
        _panStart = point.Position;
        _panStartHorizontal = scroll.HorizontalOffset;
        _panStartVertical = scroll.VerticalOffset;
        _panPreviousCursor = GetPreviewCursor();
        SetPreviewCursor(LoadPreviewCursor(IntPtr.Zero, new IntPtr(32646))); // IDC_SIZEALL
        args.Handled = true;
    }

    private void PreviewPanMoved(ScrollViewer scroll, PointerRoutedEventArgs args)
    {
        if (_panScroll != scroll || _panPointer?.PointerId != args.Pointer.PointerId) return;
        var point = args.GetCurrentPoint(scroll);
        if (!point.Properties.IsLeftButtonPressed || scroll.ZoomFactor <= scroll.MinZoomFactor + .001)
        {
            StopPreviewPan();
            return;
        }
        var horizontal = Math.Clamp(_panStartHorizontal - (point.Position.X - _panStart.X), 0, scroll.ScrollableWidth);
        var vertical = Math.Clamp(_panStartVertical - (point.Position.Y - _panStart.Y), 0, scroll.ScrollableHeight);
        scroll.ChangeView(horizontal, vertical, null, disableAnimation: true);
        SetPreviewCursor(LoadPreviewCursor(IntPtr.Zero, new IntPtr(32646)));
        args.Handled = true;
    }

    private static bool IsPreviewScrollControl(DependencyObject? source, ScrollViewer scroll)
    {
        while (source != null && source != scroll)
        {
            if (source is ScrollBar or Thumb or ButtonBase or Slider or TextBox or ComboBox) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void StopPreviewPan()
    {
        var scroll = _panScroll;
        var pointer = _panPointer;
        _panScroll = null;
        _panPointer = null;
        if (scroll == null) return;
        if (pointer != null) scroll.ReleasePointerCapture(pointer);
        SetPreviewCursor(_panPreviousCursor != IntPtr.Zero
            ? _panPreviousCursor : LoadPreviewCursor(IntPtr.Zero, new IntPtr(32512))); // IDC_ARROW
        _panPreviousCursor = IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadPreviewCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", EntryPoint = "GetCursor")]
    private static extern IntPtr GetPreviewCursor();

    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    private static extern IntPtr SetPreviewCursor(IntPtr cursor);
}
