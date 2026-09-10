using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;
using WinTools.Services;

namespace WinTools;

/// <summary>结果列表里的一行：名称 + 说明 + 真实 shell 图标。</summary>
/// <remarks>
/// 图标解析直接复用 <see cref="CardItem"/>（桌面卡片那套 STA 队列 + 进程内缓存），
/// 不再重写一份 <c>SHGetFileInfo</c> 逻辑。应用商店应用没有对应文件，
/// 取不到图标时显示占位字形。
/// </remarks>
public sealed class SpotlightItem : INotifyPropertyChanged
{
    private readonly CardItem? _iconSource;

    internal SpotlightItem(AppEntry entry)
    {
        Entry = entry;
        if (string.IsNullOrWhiteSpace(entry.IconPath)) return;

        _iconSource = new CardItem(entry.IconPath);
        _iconSource.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(CardItem.Icon)) return;
            Raise(nameof(Icon));
            Raise(nameof(IconVisibility));
            Raise(nameof(PlaceholderVisibility));
        };
    }

    internal AppEntry Entry { get; }

    public string Name => Entry.Name;

    public string Subtitle => Entry.Subtitle;

    public BitmapImage? Icon => _iconSource?.Icon;

    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlaceholderVisibility => Icon != null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>解析图标；没有对应文件的条目直接返回，保持占位字形。</summary>
    internal Task LoadIconAsync() => _iconSource?.LoadIconAsync() ?? Task.CompletedTask;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 悬浮搜索：全局快捷键（默认 Alt+Space）呼出的应用程序检索框，
/// 输入即筛选，Enter 启动，失去焦点或 Esc 关闭。
/// </summary>
public sealed partial class SpotlightWindow : Window, IUiStyleShell
{
    #region 布局常量（与 SpotlightWindow.xaml 中写死的高度一一对应）

    private const int WindowWidthDip = 660;
    private const int SearchRowHeight = 60;
    private const int SeparatorHeight = 1;
    private const int ItemHeight = 52;

    /// <summary>ListView 的上下 Padding 之和。</summary>
    private const int ListPadding = 12;

    private const int FooterHeight = 28;

    /// <summary>列表最多显示几行，再多就滚动——窗口不能长到盖住半个屏幕。</summary>
    private const int MaxVisibleItems = 8;

    /// <summary>参与排序后保留的结果条数。</summary>
    private const int MaxResults = 24;

    /// <summary>窗口顶边距屏幕工作区顶部的比例，与 macOS 聚焦搜索的位置接近。</summary>
    private const double TopRatio = 0.22;

    #endregion

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private readonly ObservableCollection<SpotlightItem> _items = new();

    private bool _everActivated;
    private bool _isVisible;

    /// <summary>正在以代码改写输入框内容，TextChanged 不必再跑一次筛选。</summary>
    private bool _suppressTextChanged;

    public SpotlightWindow()
    {
        InitializeComponent();
        ResultList.ItemsSource = _items;
        ConfigureWindow();
        AppWindow.SetIcon("Assets\\AppIcon.ico");
        ThemeService.Register(this);
        UiStyleService.Register(this);
    }

    #region 窗口配置

    private void ConfigureWindow()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }

            WindowHelper.ConfigureChromelessWindow(this);
            WindowHelper.DisableWindowTransitions(this);
            ApplyUiStyleSurfaces();

            AppWindow.IsShownInSwitchers = false;

            // 点到别处就收起来——聚焦搜索不该留在屏幕上等人来关。
            Activated += OnActivated;

            // 关闭时只隐藏；进程真正退出时放行（见 App.IsShuttingDown）。
            AppWindow.Closing += (_, e) =>
            {
                if (App.IsShuttingDown) return;
                e.Cancel = true;
                HideWindow();
            };
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.ConfigureWindow", ex);
        }
    }

    public void ApplyUiStyleSurfaces()
    {
        var theme = ThemeService.EffectiveTheme;
        ShellRoot.RequestedTheme = theme;
        ShellRoot.Background = WindowHelper.GetMicaPopupBrush(theme, UiStyleService.IsMica, elevated: true);
        WindowHelper.ApplyWindowBackdrop(this);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _isVisible)
            HideWindow();
    }

    #endregion

    #region 显示 / 隐藏

    /// <summary>快捷键入口：已显示则收起，否则呼出。</summary>
    public void Toggle()
    {
        if (_isVisible) HideWindow();
        else ShowWindow();
    }

    /// <summary>在屏幕外完成首次 WinUI 初始化，避免首次呼出时闪白。</summary>
    internal void EnsureInitialized()
    {
        if (_everActivated) return;
        try
        {
            AppWindow.Move(new PointInt32(-20000, -20000));
            Activate();
            _everActivated = true;
            AppWindow.Hide();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.EnsureInitialized", ex);
        }
    }

    public void ShowWindow()
    {
        try
        {
            EnsureInitialized();

            _suppressTextChanged = true;
            QueryBox.Text = "";
            _suppressTextChanged = false;

            // 先按缓存索引渲染一次（通常已经有了），窗口立刻可用；
            // 索引过期时后台重建完成后再刷新一次列表。
            ApplyQuery("");

            WindowHelper.SetWindowCloak(this, true);
            PositionWindow();
            Activate();
            _isVisible = true;

            // 全局快捷键触发时本进程拿得到前台权限，这一步保证输入框真的能收到按键。
            try { SetForegroundWindow(WindowNativeHandle); } catch { /* ignore */ }
            WindowHelper.UncloakWhenRendered(this);

            QueryBox.Focus(FocusState.Programmatic);
            QueryBox.SelectAll();

            _ = RefreshIndexAsync();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.ShowWindow", ex);
        }
    }

    public void HideWindow()
    {
        _isVisible = false;
        try { AppWindow.Hide(); }
        catch (Exception ex) { ErrorReporter.Log("SpotlightWindow.HideWindow", ex); }
    }

    private IntPtr WindowNativeHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>索引可能已过期：重建完成后，若窗口还开着就用最新结果重刷。</summary>
    private async Task RefreshIndexAsync()
    {
        try
        {
            var before = AppSearchIndex.Snapshot.Count;
            var entries = await AppSearchIndex.GetAsync();
            if (!_isVisible || entries.Count == before) return;
            ApplyQuery(QueryBox.Text);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.RefreshIndexAsync", ex);
        }
    }

    /// <summary>把窗口放到鼠标所在显示器的工作区里，水平居中、垂直靠上。</summary>
    private void PositionWindow()
    {
        try
        {
            var scale = GetScale();
            var width = (int)Math.Round(WindowWidthDip * scale);
            var height = (int)Math.Round(MeasureHeightDip() * scale);

            var area = GetTargetDisplayArea();
            var work = area.WorkArea;
            var x = work.X + (work.Width - width) / 2;
            var y = work.Y + (int)(work.Height * TopRatio);

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.PositionWindow", ex);
        }
    }

    /// <summary>结果条数变化后只改高度，保持左上角不动，避免窗口在屏幕上跳。</summary>
    private void ResizeToContent()
    {
        if (!_isVisible) return;
        try
        {
            var scale = GetScale();
            var height = (int)Math.Round(MeasureHeightDip() * scale);
            var size = AppWindow.Size;
            if (size.Height == height) return;
            AppWindow.Resize(new SizeInt32(size.Width, height));
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("SpotlightWindow.ResizeToContent", ex);
        }
    }

    private double MeasureHeightDip()
    {
        if (_items.Count == 0) return SearchRowHeight;
        var rows = Math.Min(_items.Count, MaxVisibleItems);
        return SearchRowHeight + SeparatorHeight + ListPadding + rows * ItemHeight + FooterHeight;
    }

    private double GetScale()
    {
        var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 0;
        if (scale > 0) return scale;
        try
        {
            var dpi = GetDpiForWindow(WindowNativeHandle);
            if (dpi > 0) return dpi / 96.0;
        }
        catch { /* ignore */ }
        return 1.0;
    }

    private DisplayArea GetTargetDisplayArea()
    {
        try
        {
            if (GetCursorPos(out var point))
            {
                var area = DisplayArea.GetFromPoint(new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
                if (area != null) return area;
            }
        }
        catch { /* ignore */ }
        return DisplayArea.Primary;
    }

    #endregion

    #region 检索与结果

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        ApplyQuery(QueryBox.Text);
    }

    private void ApplyQuery(string query)
    {
        var entries = AppSearchIndex.Snapshot;
        var matches = AppSearchIndex.Search(entries, query, MaxResults);

        _items.Clear();
        foreach (var entry in matches)
        {
            var item = new SpotlightItem(entry);
            _items.Add(item);
            _ = item.LoadIconAsync();
        }

        // 没有命中时给一条“直接执行输入内容”的兜底，等价于运行对话框。
        var trimmed = (query ?? "").Trim();
        if (_items.Count == 0 && trimmed.Length > 0)
        {
            _items.Add(new SpotlightItem(new AppEntry
            {
                Name = $"运行 “{trimmed}”",
                Subtitle = "没有匹配的应用程序，按 Enter 直接执行",
                Target = trimmed,
                IconPath = "",
            }));
        }

        var hasItems = _items.Count > 0;
        Separator.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        FooterRow.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ResultList.SelectedIndex = hasItems ? 0 : -1;
        ResizeToContent();
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HideWindow();
                break;

            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;

            case VirtualKey.Enter:
                e.Handled = true;
                if (ResultList.SelectedItem is SpotlightItem item)
                    Launch(item, revealInExplorer: IsControlDown());
                break;
        }
    }

    private static bool IsControlDown()
    {
        try
        {
            var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        }
        catch { return false; }
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        var index = ResultList.SelectedIndex + delta;
        // 上下越界时回绕，长列表里按住方向键更顺手。
        if (index < 0) index = _items.Count - 1;
        if (index >= _items.Count) index = 0;
        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(_items[index]);
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SpotlightItem item)
            Launch(item, revealInExplorer: IsControlDown());
    }

    #endregion

    #region 启动

    private void Launch(SpotlightItem item, bool revealInExplorer)
    {
        var entry = item.Entry;
        HideWindow();

        try
        {
            if (revealInExplorer && !string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.IconPath}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            if (entry.Target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                // 应用商店应用只有 AUMID，交给 explorer 解析 shell:AppsFolder。
                Process.Start(new ProcessStartInfo("explorer.exe", entry.Target) { UseShellExecute = true });
            }
            else
            {
                var start = new ProcessStartInfo(entry.Target) { UseShellExecute = true };
                var directory = SafeDirectoryOf(entry.Target);
                if (directory != null) start.WorkingDirectory = directory;
                Process.Start(start);
            }

            AppUsageStore.Bump(entry.Key);
        }
        catch (Exception ex)
        {
            // 兜底的“直接执行”条目输错时最常走到这里，记日志即可，不弹窗打断。
            ErrorReporter.Log($"SpotlightWindow.Launch({entry.Target})", ex);
        }
    }

    private static string? SafeDirectoryOf(string target)
    {
        try
        {
            if (!File.Exists(target)) return null;
            var directory = Path.GetDirectoryName(target);
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch { return null; }
    }

    #endregion
}
