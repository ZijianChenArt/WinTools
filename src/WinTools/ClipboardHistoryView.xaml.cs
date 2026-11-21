using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinTools.Services;

namespace WinTools;

public sealed partial class ClipboardHistoryView : UserControl
{
    private ClipboardHistoryService? _history;
    private bool _busy;
    internal event Action? Copied;
    internal event Action? CloseRequested;
    internal UIElement PopupDragRegion => HeaderDragRegion;

    public ClipboardHistoryView()
    {
        InitializeComponent();
        ListScrollbar.Attach(HistoryScrollBar, HistoryList);
    }

    internal void UsePopupToolbar()
    {
        PopupTitle.Visibility = Visibility.Visible;
        PopupCloseButton.Visibility = Visibility.Visible;
        HistoryToolbar.Height = 32;
        HistoryToolbar.Margin = new Thickness(6, 0, 0, 0);
        ClearButton.MinHeight = 0;
        ClearButton.Height = 28;
        ClearButton.FontSize = 12;
        ClearButton.Padding = new Thickness(8, 0, 8, 0);
        StatusText.Text = "选择一条记录";
        // 记录容器上、下各有 5 DIP，右侧另有 12 DIP 滚动条槽，四边视觉留白均为 16 DIP。
        HistoryList.Margin = new Thickness(16, 11, 4, 11);
        HistoryScrollBar.Margin = new Thickness(0, 11, 0, 11);
        HistorySurface.BorderThickness = new Thickness(1);
    }

    internal void ApplyPopupSurface(Brush background, Brush border)
    {
        HistorySurface.Background = background;
        HistorySurface.BorderBrush = border;
    }

    private void PopupClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    internal void Bind(ClipboardHistoryService history)
    {
        if (ReferenceEquals(_history, history)) return;
        _history = history;
        HistoryList.ItemsSource = history.Items;
        history.Items.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        var hasItems = _history?.Items.Count > 0;
        EmptyText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        ClearButton.IsEnabled = hasItems && !_busy;
    }

    private async void History_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_history is not { } history || e.ClickedItem is not ClipboardHistoryItem item) return;
        await RunAsync(async () =>
        {
            await history.RestoreAsync(item);
            StatusText.Text = "已复制";
            Copied?.Invoke();
        }, "复制失败，文件可能已移动或不可访问");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_history is not { } history || sender is not Button { Tag: ClipboardHistoryItem item }) return;
        await RunAsync(async () =>
        {
            await history.DeleteAsync(item);
            StatusText.Text = "已删除";
        }, "无法删除记录");
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_history is not { } history) return;
        await RunAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "清空剪贴板历史？",
                PrimaryButtonText = "清空",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await history.DeleteAsync(null);
                StatusText.Text = "历史已清空";
            }
        }, "无法清空历史");
    }

    private async Task RunAsync(Func<Task> action, string failure)
    {
        if (_busy) return;
        _busy = true;
        UpdateEmptyState();
        try { await action(); }
        catch (Exception ex)
        {
            ErrorReporter.Log("ClipboardHistory.Action", ex.GetType().Name);
            StatusText.Text = failure;
        }
        finally
        {
            _busy = false;
            UpdateEmptyState();
        }
    }
}
