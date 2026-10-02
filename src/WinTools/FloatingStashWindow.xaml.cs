using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace WinTools;

/// <summary>
/// 悬浮暂存窗口：接受拖入文件暂存、从列表拖出以移动文件。
/// 置顶显示，有文件时持续显示，列表清空后触发 <see cref="BecameEmpty"/> 事件。
/// </summary>
public sealed partial class FloatingStashWindow : Window, IUiStyleShell
{
    public ObservableCollection<StashedItem> Items { get; } = new();

    /// <summary>暂存列表中是否有文件。</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>当暂存列表从有文件变为空时触发（所有文件已被移出）。</summary>
    public event EventHandler? BecameEmpty;

    /// <summary>拖入文件时检测到的扩展名集合（供程序选择窗口过滤）。</summary>
    public event Action<HashSet<string>>? DragExtensionsDetected;

    public FloatingStashWindow()
    {
        InitializeComponent();
        WindowHelper.EnablePersistentMica(this); // 点开后焦点常被任务栏抢走，Mica 不能跟着变灰
        Items.CollectionChanged += Items_CollectionChanged;
        UpdateEmptyHint();
        ConfigureWindow();
        AppWindow.SetIcon("Assets\\AppIcon.ico");
    }

    #region 窗口配置

    private const int DefaultWidthDip = 360;
    private const int MinWidthDip = 260;
    private const int MinHeightDip = 100;

    // 高度随文件数自适应：固定部分（标题栏 + 内外边距）加上每个文件一行，超过 MaxRows 行后列表内滚动。
    private const int FixedHeightDip = 54;
    private const int RowHeightDip = 50;
    private const int MaxRows = 6;

    // 空列表时保留完整标题栏、均衡外边距和紧凑的投放提示。
    private const int MiniWidthDip = 240;
    private const int MiniHeightDip = 132;

    private bool _everActivated;
    private bool _sizePersistAllowed;
    private bool _isAutoResizing;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sizeSaveTimer;

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = true;
                p.IsResizable = true;
                p.IsMinimizable = false;
                p.IsMaximizable = false;
            }

            WindowHelper.ConfigureTransparentTitleBar(this, AppTitleBar);
            WindowHelper.HookTitleBarPadding(this, AppTitleBar, LeftPaddingColumn, RightPaddingColumn);
            WindowHelper.DisableWindowTransitions(this);
            ApplyUiStyleSurfaces();

            AppWindow.IsShownInSwitchers = false;

            // 按当前列表状态设定初始尺寸（空=mini，有文件=正常）
            ApplySizeForItemState();

            // 关闭时清空本次暂存记录并隐藏，窗口实例保留供下次拖拽复用。
            AppWindow.Closing += (_, e) =>
            {
                // 进程正在退出时放行，否则窗口会一直拦下 WM_CLOSE（见 App.IsShuttingDown）。
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                Items.Clear();
                AppWindow.Hide();
            };

            // Loaded 时按真实 DPI 重新适配尺寸
            if (Content is FrameworkElement root)
                root.Loaded += (_, _) => ApplySizeForItemState();

            // 窗口大小改变时：按列表状态限制最小尺寸；防抖写入配置
            _sizeSaveTimer = DispatcherQueue.CreateTimer();
            _sizeSaveTimer.Interval = TimeSpan.FromMilliseconds(600);
            _sizeSaveTimer.IsRepeating = false;
            _sizeSaveTimer.Tick += (_, _) => SaveWindowSize();
            AppWindow.Changed += (_, e) =>
            {
                // 用户自己拖动了窗口：不再强行拉回任务栏快捷图标上方。
                if (e.DidPositionChange && _anchor != null && !Animator.IsBusy && AppWindow.Position != _anchorPosition)
                    _anchor = null;
                if (!e.DidSizeChange) return;
                EnforceItemStateMinSize();
                _sizeSaveTimer?.Stop();
                _sizeSaveTimer?.Start();
            };
        }
        catch { /* ignore */ }
    }

    /// <summary>按列表状态调整窗口尺寸：空列表为 mini，有文件恢复为已保存/默认尺寸。</summary>
    private void ApplySizeForItemState()
    {
        try
        {
            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            int dipW, dipH;
            if (HasItems)
            {
                // 读共享配置对象，而不是每次拖拽都读盘；也保证与设置页写回的是同一份数据。
                var cfg = Services.SettingsService.Instance.Current;
                dipW = cfg.StashWindowWidth > 0 ? cfg.StashWindowWidth : DefaultWidthDip;
                dipH = Math.Max(MinHeightDip, FixedHeightDip + Math.Min(Items.Count, MaxRows) * RowHeightDip);
            }
            else
            {
                dipW = MiniWidthDip;
                dipH = MiniHeightDip;
            }

            // 尺寸没变就不调用 Resize：它会触发 Changed 事件与布局，白占点击后首帧的时间。
            var target = new Windows.Graphics.SizeInt32((int)(dipW * scale), (int)(dipH * scale));
            if (AppWindow.Size.Width != target.Width || AppWindow.Size.Height != target.Height)
            {
                _isAutoResizing = true;
                AppWindow.Resize(target);
                _isAutoResizing = false;
            }
            ApplyAnchor(); // 尺寸变化后仍贴着任务栏，向上生长
        }
        catch { _isAutoResizing = false; }
    }

    /// <summary>按列表状态限制最小尺寸（空=mini 尺寸，有文件=正常最小尺寸）。</summary>
    private void EnforceItemStateMinSize()
    {
        if (_isAutoResizing) return;
        try
        {
            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var minWdip = HasItems ? MinWidthDip : MiniWidthDip;
            var minHdip = HasItems ? MinHeightDip : MiniHeightDip;
            var minW = (int)(minWdip * scale);
            var minH = (int)(minHdip * scale);
            var size = AppWindow.Size;
            if (size.Width < minW || size.Height < minH)
            {
                _isAutoResizing = true;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    Math.Max(size.Width, minW), Math.Max(size.Height, minH)));
                _isAutoResizing = false;
            }
        }
        catch { _isAutoResizing = false; }
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        var isMica = UiStyleService.IsMica;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, isMica);
        AppTitleBar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ContentBorder.Background = isMica
            ? WindowHelper.GetMicaPanelBrush(theme)
            : WindowHelper.GetMicaPopupBrush(theme, isMica, elevated: true);
        ContentBorder.BorderBrush = WindowHelper.GetCodexBorderBrush(theme);
        WindowHelper.ApplyWindowBackdrop(this);
        WindowHelper.ApplyTitleBarButtonColors(this, theme);
    }

    /// <summary>将当前窗口尺寸（物理像素）转为 DIP 并保存到配置文件。</summary>
    private void SaveWindowSize()
    {
        try
        {
            // 预热/自动缩放不算用户调整，避免启动时写回配置
            if (!_sizePersistAllowed) return;
            // mini（空列表）尺寸不作为正常尺寸持久化
            if (!HasItems) return;

            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var dipW = (int)(AppWindow.Size.Width / scale);
            if (dipW < 100) return; // 忽略异常小尺寸；高度由文件数决定，不再持久化
            // 必须写进共享的 SettingsService.Current（由它防抖落盘）。直接写文件的话，
            // 设置页任何一次改动或退出时整份保存 Current，都会把这里的尺寸覆盖回旧值。
            var cfg = Services.SettingsService.Instance.Current;
            cfg.StashWindowWidth = dipW;
        }
        catch { /* ignore */ }
    }

    // 暂存窗口贴靠任务栏快捷图标：水平以图标为中心，下沿与任务栏上沿留出统一的弹窗间距。
    private (int CenterX, int TaskbarTop)? _anchor;
    private Windows.Graphics.PointInt32 _anchorPosition;

    /// <summary>定位到任务栏快捷图标正上方；越出屏幕边缘时贴边（默认布局下即左下角）。</summary>
    /// <param name="centerX">快捷图标中心的屏幕 X（物理像素）。</param>
    /// <param name="taskbarTop">任务栏上沿的屏幕 Y（物理像素）。</param>
    internal void PositionAboveAnchor(int centerX, int taskbarTop)
    {
        _anchor = (centerX, taskbarTop);
        ApplyAnchor();
    }

    private void ApplyAnchor()
    {
        if (_anchor is not { } anchor) return;
        try
        {
            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var gap = (int)(WindowHelper.TaskbarPopupGapDip * scale);
            var work = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(anchor.CenterX, anchor.TaskbarTop - 1),
                DisplayAreaFallback.Nearest).WorkArea;
            var size = AppWindow.Size;
            var minX = work.X + gap;
            var maxX = Math.Max(minX, work.X + work.Width - size.Width - gap);
            var x = Math.Clamp(anchor.CenterX - size.Width / 2, minX, maxX);
            var y = Math.Max(work.Y, anchor.TaskbarTop - gap - size.Height);
            _anchorPosition = new Windows.Graphics.PointInt32(x, y);
            AppWindow.Move(_anchorPosition);
        }
        catch { /* ignore */ }
    }

    /// <summary>将窗口定位到屏幕右上角。</summary>
    internal void PositionToTopRight()
    {
        _anchor = null;
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var margin = 16;
            var x = Math.Max(work.X, work.X + work.Width - AppWindow.Size.Width - margin);
            var y = Math.Max(work.Y, work.Y + margin);
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch { /* ignore */ }
    }

    #endregion

    #region 显示 / 隐藏

    /// <summary>在屏幕外完成首次 WinUI 初始化，避免拖拽时首次弹出卡顿或闪白。</summary>
    internal void EnsureInitialized()
    {
        if (_everActivated) return;
        var anchor = _anchor; // 移到屏幕外不算用户拖动，保留贴靠状态
        try
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            Activate();
            _everActivated = true;
            AppWindow.Hide();
        }
        catch { /* ignore */ }
        _anchor = anchor;
    }

    private PopupAnimator? _animatorInstance;
    private PopupAnimator Animator => _animatorInstance ??= new PopupAnimator(this);

    /// <summary>窗口可见且不在收起动画中。</summary>
    internal bool IsShown => AppWindow.IsVisible && !Animator.IsHiding;

    /// <summary>显示悬浮窗。</summary>
    /// <param name="activate">是否抢焦点（拖拽触发时应为 false）。</param>
    /// <param name="animate">
    /// 是否播放与音频弹窗一致的滑入淡入。仅用于点击打开；拖拽中弹出不要动画，
    /// 窗口在拖动预览下方改变形态会让系统拖动预览闪动。
    /// </param>
    public void ShowWindow(bool activate = false, bool animate = false)
    {
        try
        {
            _sizePersistAllowed = true;
            EnsureInitialized();
            Animator.Reset(); // 打断可能正在进行的收起动画，恢复为普通不透明窗口
            ApplySizeForItemState();

            if (animate)
            {
                var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
                var rest = AppWindow.Position;
                Animator.PrepareReveal(rest.Y, scale);
                AppWindow.Move(new Windows.Graphics.PointInt32(rest.X, Animator.RevealStartY));
            }

            // 先 Cloak 隐藏，渲染完成后再显示，避免白色闪动
            WindowHelper.SetWindowCloak(this, true);
            if (activate)
                Activate();
            else
                AppWindow.Show();
            WindowHelper.UncloakWhenRendered(this, animate ? Animator.PlayReveal : null);
        }
        catch { Animator.Reset(); }
    }

    /// <summary>带下沉淡出动画地隐藏（点击任务栏图标收起时使用），暂存的文件保留。</summary>
    public void HideAnimated()
    {
        if (!IsShown) return;
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        Animator.PlayDismiss(scale, () => AppWindow.Hide());
    }

    /// <summary>隐藏悬浮窗。</summary>
    public void HideWindow()
    {
        try { AppWindow.Hide(); } catch { /* ignore */ }
        _animatorInstance?.Reset();
    }

    #endregion

    #region 暂存管理

    /// <summary>将文件/文件夹路径添加到暂存列表（跳过重复与不存在的路径），并异步加载缩略图。</summary>
    public void AddPaths(string[] paths)
    {
        if (paths.Length == 0) return;
        var existing = Items.Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            if (existing.Contains(path)) continue;
            var item = new StashedItem(path);
            Items.Add(item);
            _ = item.LoadVisualAsync();
        }
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyHint();
        ApplySizeForItemState();
        if (Items.Count == 0)
            BecameEmpty?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmptyHint()
    {
        var empty = Items.Count == 0;
        if (EmptyHint != null)
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region 事件处理

    private void Item_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g && g.Children.LastOrDefault() is Button btn)
            btn.Opacity = 1;
    }

    private void Item_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g && g.Children.LastOrDefault() is Button btn)
            btn.Opacity = 0.72;
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is StashedItem item)
            Items.Remove(item);
    }

    /// <summary>拖入：检测文件扩展名并通知程序选择窗口过滤。</summary>
    private async void DropArea_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // 必须在任何 await 之前声明接受，否则部分系统会一直显示“禁止拖放”光标。
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var ext = System.IO.Path.GetExtension(item.Name);
                if (!string.IsNullOrEmpty(ext))
                    exts.Add(ext.ToLowerInvariant());
            }
            if (exts.Count > 0)
                DragExtensionsDetected?.Invoke(exts);
        }
        catch { /* ignore */ }
    }

    /// <summary>拖入：接受 Copy 操作（最通用，兼容资源管理器）。</summary>
    private void DropArea_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
        else
            e.AcceptedOperation = DataPackageOperation.None;
        e.Handled = true;
    }

    /// <summary>拖入：接受文件和文件夹。</summary>
    private async void DropArea_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();
            AddPaths(paths);
        }
        catch { /* 跨权限拖放会被系统拒绝，保持窗口可继续使用。 */ }
    }

    /// <summary>拖出：将暂存文件作为 Move 操作提供给目标（同步，使用缓存的 StorageItem）。</summary>
    private void ItemsList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var selected = e.Items.OfType<StashedItem>().ToList();
        if (selected.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        var storageItems = selected
            .Where(s => s.StorageItem != null)
            .Select(s => s.StorageItem!)
            .ToList();

        if (storageItems.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        e.Data.SetStorageItems(storageItems);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void ItemsList_DragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
    {
        if (e.DropResult != DataPackageOperation.Move) return;
        foreach (var item in e.Items.OfType<StashedItem>().ToList())
            Items.Remove(item);
    }

    #endregion
}

/// <summary>暂存文件项，支持异步加载缩略图与预览，并缓存 StorageItem 供拖放使用。</summary>
public sealed class StashedItem : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private BitmapImage? _preview;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"
    };

    public StashedItem(string path)
    {
        Path = path;
        DisplayName = System.IO.Path.GetFileName(path);
    }

    /// <summary>文件完整路径。</summary>
    public string Path { get; }

    /// <summary>显示名称（文件名）。</summary>
    public string DisplayName { get; }

    /// <summary>缓存的 StorageItem，供拖放操作同步使用，避免事件中 await。</summary>
    public IStorageItem? StorageItem { get; private set; }

    /// <summary>文件缩略图/图标（异步加载后更新 UI）。</summary>
    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            if (_icon == value) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>文件内容预览图（图片文件显示实际内容，其余显示图标）。</summary>
    public BitmapImage? Preview
    {
        get => _preview;
        private set
        {
            if (_preview == value) return;
            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 顺序加载图标与预览，同时缓存 StorageItem。
    /// 图片文件直接从文件内容生成预览；其他文件仅加载系统图标。
    /// </summary>
    public async System.Threading.Tasks.Task LoadVisualAsync()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(Path);
                StorageItem = folder;
                var thumb = await folder.GetThumbnailAsync(ThumbnailMode.SingleItem, 48);
                if (thumb != null)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    Icon = bmp;
                }
            }
            else if (File.Exists(Path))
            {
                var file = await StorageFile.GetFileFromPathAsync(Path);
                StorageItem = file;

                var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 48);
                if (thumb != null)
                {
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(thumb);
                    Icon = bmp;
                }

                var ext = System.IO.Path.GetExtension(Path);
                if (ImageExtensions.Contains(ext))
                {
                    using var stream = await file.OpenReadAsync();
                    var previewBmp = new BitmapImage { DecodePixelWidth = 256 };
                    await previewBmp.SetSourceAsync(stream);
                    Preview = previewBmp;
                }
            }
        }
        catch { /* ignore */ }
    }
}
