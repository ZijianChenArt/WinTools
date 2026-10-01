using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace WinTools;

/// <summary>每个程序拖放目标的关联数据。</summary>
internal sealed class ProgramItemData
{
    public string ProgramPath { get; init; } = "";
    public HashSet<string> Extensions { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 程序选择悬浮窗：在拖拽文件时显示于暂存窗口上方。
/// 每个已配置程序显示为一个拖放目标，用户将文件拖放到对应程序即可打开。
/// 拖入文件后自动按扩展名过滤，只显示匹配的程序。
/// </summary>
public sealed partial class ProgramDropWindow : Window, IUiStyleShell
{
    private int _programCount;
    private bool _everActivated;
    private double _lastScale = 1.0;
    private DateTime _lastConfigWriteTimeUtc = DateTime.MinValue;
    private bool _hasBuiltPrograms;

    /// <summary>拖放高亮用画刷（半透明灰，深浅主题通用）。</summary>
    private static readonly SolidColorBrush HighlightBrush =
        new(ColorHelper.FromArgb(45, 128, 128, 128));

    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush NormalBorderBrush =
        new(ColorHelper.FromArgb(40, 128, 128, 128));
    private static readonly SolidColorBrush HighlightBorderBrush =
        new(ColorHelper.FromArgb(140, 0, 120, 215));

    public ProgramDropWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        AppWindow.SetIcon("Assets\\AppIcon.ico");
    }

    #region 窗口配置

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.IsAlwaysOnTop = true;
                p.IsResizable = false;
                p.IsMinimizable = false;
                p.IsMaximizable = false;
            }

            WindowHelper.ConfigureTransparentTitleBar(this, AppTitleBar);
            WindowHelper.HookTitleBarPadding(this, AppTitleBar, LeftPaddingColumn, RightPaddingColumn);
            WindowHelper.DisableWindowTransitions(this);
            ApplyUiStyleSurfaces();

            AppWindow.IsShownInSwitchers = false;

            // 关闭时隐藏而非销毁；进程正在退出时放行（见 App.IsShuttingDown）。
            AppWindow.Closing += (_, e) =>
            {
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                AppWindow.Hide();
            };
        }
        catch { /* ignore */ }
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica);
        AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);
        WindowHelper.ApplyWindowBackdrop(this);
        WindowHelper.ApplyTitleBarButtonColors(this, theme);
    }

    #endregion

    #region 显示 / 隐藏

    /// <summary>在屏幕外完成首次 WinUI 初始化，避免拖拽时首次弹出卡顿或闪白。</summary>
    internal void EnsureInitialized()
    {
        if (_everActivated) return;
        try
        {
            AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
            Activate();
            _everActivated = true;
            AppWindow.Hide();
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 显示窗口。WinUI 3 窗口首次显示必须调用 Activate() 完成原生句柄初始化，
    /// 否则 AppWindow.Show() 不会生效。
    /// </summary>
    /// <param name="animate">保留参数（当前不播放入场动画）。</param>
    public void ShowWindow(bool activate = false, bool animate = false)
    {
        try
        {
            EnsureInitialized();

            // 先 Cloak 隐藏，渲染完成后再显示，避免白色闪动
            WindowHelper.SetWindowCloak(this, true);
            if (activate)
                Activate();
            else
                AppWindow.Show();
            WindowHelper.UncloakWhenRendered(this);

            // 程序选择悬浮窗口禁用入场动画，减少拖拽打断感
        }
        catch { /* ignore */ }
    }

    /// <summary>隐藏窗口。</summary>
    public void HideWindow()
    {
        try { AppWindow.Hide(); } catch { /* ignore */ }
    }

    #endregion

    #region 程序列表构建

    /// <summary>
    /// 根据配置重建程序列表。
    /// 按程序路径去重，合并扩展名显示。初始构建时显示全部程序；
    /// 当文件拖入窗口后会通过 FilterByExtensions 自动过滤。
    /// </summary>
    /// <returns>是否有可用程序（true 时才需要显示窗口）。</returns>
    public bool RebuildPrograms()
    {
        // 配置未变化时复用已构建 UI，避免每次拖拽都重建大量控件
        var configWriteTimeUtc = GetConfigWriteTimeUtc(ConfigService.ConfigFilePath);
        if (_hasBuiltPrograms && configWriteTimeUtc == _lastConfigWriteTimeUtc)
            return _programCount > 0;

        _hasBuiltPrograms = true;
        _lastConfigWriteTimeUtc = configWriteTimeUtc;

        ProgramPanel.Children.Clear();
        _programCount = 0;

        var config = ConfigService.Load();
        var entries = config.Entries;
        if (entries == null || entries.Count == 0)
        {
            EmptyHint.Text = "尚未配置程序";
            EmptyHint.Visibility = Visibility.Visible;
            return false;
        }

        // 按规范化路径去重，同一个程序合并显示所有扩展名
        var grouped = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Path))
            .GroupBy(e => NormalizePath(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouped.Count == 0)
        {
            EmptyHint.Text = "尚未配置程序";
            EmptyHint.Visibility = Visibility.Visible;
            return false;
        }

        EmptyHint.Visibility = Visibility.Collapsed;

        foreach (var group in grouped)
        {
            var first = group.First();
            var extSet = group
                .Select(g => g.Ext?.ToLowerInvariant() ?? "")
                .Where(e => e.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extsDisplay = string.Join("  ", extSet.OrderBy(e => e));
            var displayName = string.IsNullOrWhiteSpace(first.Name)
                ? Path.GetFileNameWithoutExtension(first.Path)
                : first.Name;

            var border = CreateDropTarget(first.Path, displayName, extsDisplay, extSet);
            ProgramPanel.Children.Add(border);
            _programCount++;
        }

        return _programCount > 0;
    }

    /// <summary>检查当前程序列表中是否有至少一个程序匹配指定扩展名。</summary>
    public bool HasMatchingPrograms(HashSet<string> extensions)
    {
        foreach (var child in ProgramPanel.Children)
        {
            if (child is Border { Tag: ProgramItemData data } && data.Extensions.Overlaps(extensions))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 按扩展名过滤程序列表：只显示关联了指定扩展名的程序，隐藏其余。
    /// 传入 null 或空集合则显示全部。
    /// </summary>
    public void FilterByExtensions(HashSet<string>? extensions)
    {
        bool showAll = extensions == null || extensions.Count == 0;
        int visibleCount = 0;

        foreach (var child in ProgramPanel.Children)
        {
            if (child is not Border b || b.Tag is not ProgramItemData data)
                continue;

            bool match = showAll || data.Extensions.Overlaps(extensions!);
            b.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) visibleCount++;
        }

        if (visibleCount == 0)
        {
            EmptyHint.Text = "没有可用于此文件的程序";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }

        // 调整窗口高度以适应过滤后的程序数量
        ResizeToFitVisible(visibleCount);
    }

    /// <summary>为单个程序创建可接受拖放的 UI 元素。</summary>
    private static Border CreateDropTarget(string programPath, string displayName, string extensions, HashSet<string> extSet)
    {
        var nameBlock = new TextBlock
        {
            Text = displayName,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var extBlock = new TextBlock
        {
            Text = extensions,
            FontSize = 11,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
        };

        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(nameBlock);
        stack.Children.Add(extBlock);

        var border = new Border
        {
            AllowDrop = true,
            Background = TransparentBrush,
            BorderBrush = NormalBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Tag = new ProgramItemData { ProgramPath = programPath, Extensions = extSet },
        };
        border.Child = stack;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            border, $"将文件交给 {displayName} 打开");
        ToolTipService.SetToolTip(border, $"拖放文件到这里，使用 {displayName} 打开");

        // 拖入高亮
        border.DragEnter += (s, _) =>
        {
            if (s is Border b) { b.Background = HighlightBrush; b.BorderBrush = HighlightBorderBrush; }
        };
        // 拖出恢复
        border.DragLeave += (s, _) =>
        {
            if (s is Border b) { b.Background = TransparentBrush; b.BorderBrush = NormalBorderBrush; }
        };
        // 接受文件拖放
        border.DragOver += ProgramItem_DragOver;
        border.Drop += ProgramItem_Drop;

        return border;
    }

    #endregion

    #region 内容区域拖放事件（用于检测文件扩展名并过滤列表）

    /// <summary>
    /// 文件拖入窗口内容区域时，读取文件扩展名并过滤程序列表，
    /// 只显示与当前文件匹配的程序。
    /// </summary>
    private async void ContentArea_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var ext = Path.GetExtension(item.Name);
                if (!string.IsNullOrEmpty(ext))
                    exts.Add(ext.ToLowerInvariant());
            }
            if (exts.Count > 0)
                FilterByExtensions(exts);
        }
        catch { /* ignore */ }
    }

    /// <summary>内容区域背景不接受拖放（仅子元素的程序项接受）。</summary>
    private void ContentArea_DragOver(object sender, DragEventArgs e)
    {
        // 不设置 Handled，让事件继续传递到子元素的程序项
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.None;
    }

    #endregion

    #region 程序项拖放事件

    private static void ProgramItem_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
        else
            e.AcceptedOperation = DataPackageOperation.None;
        e.Handled = true;
    }

    private static async void ProgramItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border border || border.Tag is not ProgramItemData data)
            return;

        // 重置高亮
        border.Background = TransparentBrush;
        border.BorderBrush = NormalBorderBrush;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
                    LaunchHelper.OpenWith(data.ProgramPath, item.Path);
            }
        }
        catch { /* 忽略 */ }
    }

    #endregion

    #region 定位与尺寸

    /// <summary>
    /// 将此窗口定位到鼠标光标右上方。
    /// 若上方空间不足则放到右下方。
    /// </summary>
    /// <param name="cursorX">光标屏幕 X 坐标（物理像素）。</param>
    /// <param name="cursorY">光标屏幕 Y 坐标（物理像素）。</param>
    /// <param name="width">窗口宽度（物理像素，与暂存窗口一致）。</param>
    /// <param name="scale">DPI 缩放比例。</param>
    public void PositionAboveCursor(int cursorX, int cursorY, int width, double scale)
    {
        try
        {
            var (gapX, gapY, windowGap) = GetOffsets();
            if (scale < 0.5) scale = 1.0;
            _lastScale = scale;

            // 根据可见程序数量计算高度（内容 + 内边距 + 标题栏 + 窗口边框）
            var visibleCount = GetVisibleProgramCount();
            if (visibleCount <= 0) visibleCount = 1;
            var heightDip = Math.Max(88, Math.Min(visibleCount * 52 + 60, 420));
            var heightPx = (int)(heightDip * scale);

            AppWindow.Resize(new SizeInt32(width, heightPx));

            var x = cursorX + gapX;               // 光标右侧
            var y = cursorY - heightPx - gapY - windowGap;     // 光标上方

            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;

            // 超出右边界则翻到左侧
            if (x + width > work.X + work.Width)
                x = cursorX - width - gapX;
            // 超出顶部则翻到下方
            if (y < work.Y)
                y = cursorY + gapY + windowGap;

            x = Math.Max(work.X, x);
            y = Math.Max(work.Y, y);

            AppWindow.Move(new PointInt32(x, y));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 将此窗口叠放在暂存窗口正上方（同宽同左边），空间不足时贴住工作区顶部。
    /// 用于暂存窗口贴靠任务栏快捷图标的场景。
    /// </summary>
    /// <param name="stashPosition">暂存窗口位置（物理像素）。</param>
    /// <param name="stashSize">暂存窗口尺寸（物理像素）。</param>
    /// <param name="scale">DPI 缩放比例。</param>
    public void PositionAboveWindow(PointInt32 stashPosition, SizeInt32 stashSize, double scale)
    {
        try
        {
            if (scale < 0.5) scale = 1.0;
            _lastScale = scale;

            var visibleCount = GetVisibleProgramCount();
            if (visibleCount <= 0) visibleCount = 1;
            var heightDip = Math.Max(88, Math.Min(visibleCount * 52 + 60, 420));
            var heightPx = (int)(heightDip * scale);
            AppWindow.Resize(new SizeInt32(stashSize.Width, heightPx));

            var work = DisplayArea.GetFromPoint(stashPosition, DisplayAreaFallback.Nearest).WorkArea;
            var y = Math.Max(work.Y, stashPosition.Y - heightPx - (int)(8 * scale));
            AppWindow.Move(new PointInt32(stashPosition.X, y));
        }
        catch { /* ignore */ }
    }

    /// <summary>读取配置中的偏移与间距，值无效时回退默认。</summary>
    private static (int GapX, int GapY, int WindowGap) GetOffsets()
    {
        var cfg = Services.SettingsService.Instance.Current; // 拖拽热路径上不读盘
        var gapX = cfg.ProgramOffsetX > 0 ? cfg.ProgramOffsetX : 80;
        var gapY = cfg.ProgramOffsetY > 0 ? cfg.ProgramOffsetY : 40;
        var windowGap = cfg.WindowGap >= 0 ? cfg.WindowGap : 0;
        return (gapX, gapY, windowGap);
    }

    /// <summary>获取当前可见的程序项数量。</summary>
    private int GetVisibleProgramCount()
    {
        int count = 0;
        foreach (var child in ProgramPanel.Children)
        {
            if (child is Microsoft.UI.Xaml.Controls.Border b && b.Visibility == Visibility.Visible)
                count++;
        }
        return count > 0 ? count : _programCount;
    }

    /// <summary>根据当前可见程序数量调整窗口高度。</summary>
    private void ResizeToFitVisible(int visibleCount)
    {
        try
        {
            if (visibleCount <= 0) visibleCount = 1; // 至少给 EmptyHint 留空间
            var scale = _lastScale > 0.5 ? _lastScale : 1.0;
            var heightDip = Math.Max(88, Math.Min(visibleCount * 52 + 60, 420));
            var heightPx = (int)(heightDip * scale);
            var currentSize = AppWindow.Size;
            AppWindow.Resize(new SizeInt32(currentSize.Width, heightPx));
        }
        catch { /* ignore */ }
    }

    #endregion

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch { return path; }
    }

    private static DateTime GetConfigWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
