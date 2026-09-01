using System;


using System.Collections.Generic;


using System.Collections.ObjectModel;


using System.IO;


using System.Linq;


using System.Runtime.InteropServices;


using System.Threading.Tasks;


using System.Text.Json;


using Microsoft.UI;


using Microsoft.UI.Windowing;


using Microsoft.UI.Xaml;


using Microsoft.UI.Xaml.Controls;


using Microsoft.UI.Xaml.Media;


using Windows.UI;


using Windows.Storage.Pickers;


using Windows.Win32;


using Windows.Win32.Foundation;


using Windows.Win32.UI.Input.KeyboardAndMouse;


using Windows.Win32.UI.WindowsAndMessaging;


using WinRT.Interop;


using WinTools.Services;


namespace WinTools;


public sealed partial class MainWindow : Window, IUiStyleShell
{


    #region 常量
    private const uint WM_HOTKEY = 0x0312;


    private const int GWLP_WNDPROC = -4;


    /// <summary>桌面卡片悬浮窗全局快捷键 ID（避免与 QuickSettingFeatures 的 1/2/3 冲突）。</summary>
    private const int HotkeyIdDesktopCard = 10;
    /// <summary>桌面卡片快捷键在 <see cref = "Config.QuickSettingsHotkeys"/> 里的键名。</summary>
    private const string DesktopCardHotkeyKey = "DesktopCard";
    private const string DesktopCardHotkeyDefault = "Ctrl+Alt+D";

    /// <summary>手动声明 SetWindowLongPtrW（CsWin32 不生成此宏对应函数）。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr NativeSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    /// <summary>侧边栏导航项标签（与 XAML 中 NavigationViewItem.Tag 一致）。</summary>
    private static class NavTags
    {
        public const string QuickSettings = "QuickSettings";

        public const string ThreeFingerDrag = "ThreeFingerDrag";

        public const string DragStash = "DragStash";

        public const string ProgramAssociation = "ProgramAssociation";

        public const string PerAppIme = "PerAppIme";

        public const string DesktopOrganize = "DesktopOrganize";

        public const string DesktopClick = "DesktopClick";

        public const string AppSettings = "AppSettings";


    }

    #endregion
    #region 字段
    private Config _config = new();

    private HWND _hwnd;

    private WNDPROC? _originalWndProc;

    private WNDPROC? _hotkeyWndProc;

    // 保留委托引用，防止被 GC 回收
    // 子页面首次访问时才由 x:Load 创建；已访问页面保留在缓存中，切换时不重复构建。
    private readonly Dictionary<string, FrameworkElement> _contentPanels = new();
    private bool _isLoadingDragStashSettings;

    private readonly TouchpadInputHost _touchpadInputHost = new();

    private QuickSettingsPopupWindow? _quickPanel;

    private DesktopCardManager? _desktopCardManager;

    private bool _quickFeaturesUiBuilt;

    private readonly Dictionary<string, QuickFeatureUi> _quickFeatureUi = new(StringComparer.OrdinalIgnoreCase);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _quickPanelPaddingSaveTimer;

    private sealed class QuickFeatureUi
    {

        public required ToggleSwitch Toggle
        {

            get;

            init;


        }

        public required TextBox HotkeyBox
        {

            get;

            init;


        }


    }

    // 程序关联内嵌编辑
    private readonly ObservableCollection<ConfigEntry> _assocEntries = new();
    private int _assocEditingIndex = -1;

    // -1 = 新建 // 输入法切换规则
    private readonly ObservableCollection<ImeRuleEntry> _imeRules = new();
    private bool _assocListInitialized;

    private bool _imeRulesListInitialized;

    // 桌面分区
    private readonly ObservableCollection<DesktopZone> _desktopZones = new();
    private readonly ObservableCollection<string> _zoneFileItems = new();

    // 当前过滤后显示的候选
    private List<string> _zoneFileAll = new();
    // 全部候选（桌面图标 + 已指定）
    private readonly HashSet<string> _zoneSelectedNames = new(StringComparer.OrdinalIgnoreCase);
    // 已勾选（独立于过滤）
    private bool _suppressZoneSelectionSync;
    private int _zoneEditingIndex = -1; // -1 = 新建

    #endregion

    // 懒解析的窗口注册入口。App 启动早期 App.Current 可能尚未设置，懒解析可以
    // 把"App 是否就绪"和"是否要注册窗口"解耦——错过时记一条日志而非吞异常。
    private IWindowRegistry? _windowRegistry;
    private IWindowRegistry WindowRegistry => _windowRegistry ??= (App.Current as IWindowRegistry) ?? NullWindowRegistry.Instance;

    /// <summary>关闭窗口时触发（缩小到托盘而非退出）。</summary>
    public event EventHandler? ClosedToTray;
    public MainWindow() : this(ConfigService.Load())
    {


    }

    internal MainWindow(Config startupConfig)
    {

        _config = startupConfig;

        _touchpadInputHost.ConfigurePointerCurve(_config.ThreeFingerLowSpeedGain, _config.ThreeFingerHighSpeedGain, _config.ThreeFingerAccelerationStart, _config.ThreeFingerAccelerationEnd);

        _isLoadingDragStashSettings = true;

        App.TraceStartup("MainWindow: before InitializeComponent");

        InitializeComponent();

        FeaturePages.Owner = this;

        App.TraceStartup("MainWindow: after InitializeComponent");

        ApplyNavigationOrder();

        App.TraceStartup("MainWindow: after ApplyNavigationOrder");

        InitContentPanels();

        App.TraceStartup("MainWindow: after InitContentPanels");

        InitTitleBar();

        App.TraceStartup("MainWindow: after InitTitleBar");

        InitWindowBehavior();

        App.TraceStartup("MainWindow: after InitWindowBehavior");

        SyncAllControlsFromConfig();

        App.TraceStartup("MainWindow: after SyncAllControlsFromConfig");

        AppWindow.SetIcon("Assets\\AppIcon.ico");

        ThemeService.Initialize(_config.AppTheme);

        UiStyleService.Initialize(_config.AppUiStyle);

        ThemeService.Register(this);

        UiStyleService.Register(this);

        ApplyShellStyle();

        ThemeService.EffectiveThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {

            ApplyShellStyle();

            UpdateNavStatusIndicators();


        }

);

        UiStyleService.StyleChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyShellStyle);

        // 先注册事件，再设置初始选中项，确保首次选择触发面板切换和配置加载
        MainNav.SelectionChanged += MainNav_SelectionChanged;
        MainNav.SelectedItem = NavItemDesktopOrganize;

        UpdateNavStatusIndicators();

        // 桌面卡片由 App 在主窗口首帧完成后初始化，避免多窗口创建抢占首屏。    
    }

    #region 初始化
    private void ApplyNavigationOrder()
    {

        MainNav.MenuItems.Clear();

        MainNav.MenuItems.Add(NavItemDesktopOrganize);

        MainNav.MenuItems.Add(NavItemDesktopClick);

        MainNav.MenuItems.Add(NavItemQuickSettings);

        MainNav.MenuItems.Add(NavItemDragStash);

        MainNav.MenuItems.Add(NavItemPerAppIme);

        MainNav.MenuItems.Add(NavItemProgramAssoc);

        MainNav.MenuItems.Add(NavItemThreeFingerDrag);


    }

    /// <summary>建立导航标签与内容面板的映射，统一切换可见页面。</summary>
    private void InitContentPanels()
    {
        _contentPanels[NavTags.QuickSettings] = ContentQuickSettings;

        _contentPanels[NavTags.ThreeFingerDrag] = ContentThreeFingerDrag;

        _contentPanels[NavTags.DragStash] = ContentDragStash;

        _contentPanels[NavTags.ProgramAssociation] = ContentProgramAssociation;

        _contentPanels[NavTags.PerAppIme] = ContentPerAppIme;

        _contentPanels[NavTags.DesktopOrganize] = ContentDesktopOrganize;

        _contentPanels[NavTags.DesktopClick] = ContentDesktopClick;

        _contentPanels[NavTags.AppSettings] = ContentAppSettings;


    }

    /// <summary>配置与侧栏、内容画布连成一体的深色自定义标题栏。</summary>
    private void InitTitleBar()
    {
        WindowHelper.ConfigureTransparentTitleBar(this, AppTitleBar);

        WindowHelper.HookTitleBarPadding(this, AppTitleBar, LeftPaddingColumn, RightPaddingColumn, syncHeight: true);

        // 窄窗口下标题栏塞不下"标识 + 搜索框 + 窗口按钮"，先收起标识文字，再整块收起搜索框。
        ShellRoot.SizeChanged += (_, e) => ApplyTitleBarDensity(e.NewSize.Width);
        ApplyTitleBarDensity(ShellRoot.ActualWidth);


    }

    /// <summary>窄窗口下搜索框是否已被用户展开。</summary>
    private bool _searchExpanded;

    /// <summary>按窗口宽度决定标题栏里哪些元素还放得下。</summary>
    private void ApplyTitleBarDensity(double width)
    {

        var narrow = width < 620;

        if (!narrow) _searchExpanded = false;

        // 标识文字最先让位，其次才是搜索框——窄窗口下它收成一个放大镜按钮，点开再展开。
        AppTitleText.Visibility = width >= 720 && !_searchExpanded ? Visibility.Visible : Visibility.Collapsed;

        ShellSearchHost.Visibility = !narrow || _searchExpanded ? Visibility.Visible : Visibility.Collapsed;

        ShellSearchButton.Visibility = narrow && !_searchExpanded ? Visibility.Visible : Visibility.Collapsed;


    }

    private void ShellSearchButton_Click(object sender, RoutedEventArgs e)
    {

        _searchExpanded = true;

        ApplyTitleBarDensity(ShellRoot.ActualWidth);

        ShellSearchBox.Focus(FocusState.Programmatic);


    }

    private void TitleBarPaneToggle_Click(object sender, RoutedEventArgs e)
    {

        MainNav.IsPaneOpen = !MainNav.IsPaneOpen;


    }

    /// <summary>配置窗口关闭行为（缩到托盘）与首次加载时的尺寸适配和快捷键注册。</summary>
    private void InitWindowBehavior()
    {
        try
        {

            if (AppWindow.Presenter is OverlappedPresenter p) p.IsResizable = true;

            AppWindow.Closing += (_, e) =>
            {

                e.Cancel = true;

                AppWindow.Hide();

                ClosedToTray?.Invoke(this, EventArgs.Empty);


            }

;

            // 限制最小尺寸，防止导航面板 + 内容区域 UI 被裁切
            WindowHelper.EnforceMinSize(this, minWidthDip: 640, minHeightDip: 450);
            if (Content is FrameworkElement root)
            {

                root.Loaded += (_, _) =>
                {

                    ApplyInitialWindowBounds();

                    ApplyResponsiveLayout(root.ActualWidth);

                    InitGlobalHotkeys();

                    // 控件初始化可能延迟触发 ValueChanged/Toggled，待 UI 稳定后再允许写入磁盘
                    DispatcherQueue.TryEnqueue(() => _isLoadingDragStashSettings = false);

                }

;

                root.SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);


            }


        }

        catch
        {

            /* 回退：Closed 仍会触发，应用可能退出 */
        }


    }

    /// <summary>使用紧凑默认尺寸，并确保窗口完整位于当前显示器工作区内。</summary>
    internal void PrepareInitialWindowBounds() => ApplyInitialWindowBounds();
    private void ApplyInitialWindowBounds()
    {

        try
        {

            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);

            var work = display.WorkArea;

            var scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;

            const int safeMargin = 24;

            var maxWidth = Math.Max(1, work.Width - safeMargin * 2);

            var maxHeight = Math.Max(1, work.Height - safeMargin * 2);

            var w = Math.Min((int)Math.Round(1040 * scale), maxWidth);

            var h = Math.Min((int)Math.Round(720 * scale), maxHeight);

            AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            var x = work.X + (work.Width - w) / 2;

            var y = work.Y + (work.Height - h) / 2;

            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));


        }

        catch
        {

            /* ignore */
        }


    }

    /// <summary>滚动区域始终铺满内容面板，让滚动条固定贴在最右侧；正文宽度和留白由功能页内部控制。</summary>
    private void ApplyResponsiveLayout(double width)
    {
        ContentFrame.Padding = new Thickness(0);


    }

    #endregion
    #region 导航切换
    private void MainNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {

        if (args.SelectedItem is not NavigationViewItem
            {

                Tag: string tag
            }

) return;

        _isLoadingDragStashSettings = true;

        SwitchContentPanel(tag);

        // 切换到特定页面时加载对应配置
        switch (tag)
        {
            case NavTags.QuickSettings:
                LoadQuickSettingsFromConfig();

                break;

            case NavTags.ThreeFingerDrag:
                LoadThreeFingerDragFromConfig();

                break;

            case NavTags.DragStash:
                LoadDragStashFromConfig();

                break;

            case NavTags.ProgramAssociation:
                LoadProgramAssocFromConfig();

                break;

            case NavTags.PerAppIme:
                LoadPerAppImeFromConfig();

                break;

            case NavTags.DesktopOrganize:
                LoadDesktopOrganizeFromConfig();

                break;

            case NavTags.DesktopClick:
                LoadDesktopClickFromConfig();

                break;

            case NavTags.AppSettings:
                LoadAppSettingsFromConfig();

                break;


        }

        _isLoadingDragStashSettings = false;

        UpdateNavStatusIndicators();


    }

    /// <summary>把主窗口的导航切到"应用设置"内嵌页。供托盘菜单、全局快捷键和     /// <see cref = "App.OpenSettingsWindow"/> 调用。     /// <para>设置 UI 已经统一在主窗口 <c>MainNav.NavItemAppSettings</c> 下面，不再使用独立的设置窗口（2026-08-31 删除了上一版     /// <c>SettingsWindow</c> 方案及 <c>_diff_backup\Settings\</c> 备份）。</para>     </summary>
    internal void NavigateToSettings()
    {
        if (App.Current is App app)
        {

            app.OpenSettingsWindow();

            return;


        }

        // 极端情况下 App 还没初始化完成，直接选中导航项兜底。
        MainNav.SelectedItem = NavItemAppSettings;

    }

    /// <summary>启动时将 config.json 同步到所有控件，避免关闭时把 XAML 默认值写回配置。</summary>
    private void SyncAllControlsFromConfig()
    {
        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        EnsureQuickFeaturesUi();

        if (HotkeyQuickPanelBox != null) HotkeyQuickPanelBox.Text = GetQuickHotkey("QuickPanel", "Ctrl+Win+W");

        if (QuickSettingsToggle != null) QuickSettingsToggle.IsOn = _config.EnableQuickPanel;

        SyncQuickPanelPaddingFromConfig();

        SyncQuickFeatureControlsFromConfig();

        if (DragStashToggle != null) DragStashToggle.IsOn = _config.EnableDragStash;

        if (ThreeFingerDragToggle != null) ThreeFingerDragToggle.IsOn = _config.EnableThreeFingerDrag;

        if (ProgramAssocToggle != null) ProgramAssocToggle.IsOn = _config.EnableProgramAssoc;

        if (PerAppImeToggle != null) PerAppImeToggle.IsOn = _config.EnablePerAppIme;

        if (DesktopClickToggle != null) DesktopClickToggle.IsOn = _config.EnableDesktopClickToShow;

        if (AutoStartToggle != null) AutoStartToggle.IsOn = AutostartService.IsEnabled();

        SyncThemeRadioFromConfig();

        var offsetX = _config.StashOffsetX > 0 ? _config.StashOffsetX : 150;

        var offsetY = _config.StashOffsetY > 0 ? _config.StashOffsetY : 40;

        SetNumberBoxValue(StashOffsetXBox, offsetX);

        SetNumberBoxValue(StashOffsetYBox, offsetY);


    }

    /// <summary>将当前 UI 状态同步到配置并写入 config.json（关闭窗口或退出时调用）。</summary>
    internal void FlushAndSaveConfig()
    {
        if (_isLoadingDragStashSettings) return;

        // 开关与滑块在操作时已写入 _config；此处仅补充可能未点「保存」的文本框内容
        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();
        if (HotkeyQuickPanelBox != null) _config.QuickSettingsHotkeys["QuickPanel"] = HotkeyQuickPanelBox.Text?.Trim() ?? "";

        if (HotkeyDesktopCardBox != null) _config.QuickSettingsHotkeys[DesktopCardHotkeyKey] = HotkeyDesktopCardBox.Text?.Trim() ?? "";

        SaveQuickFeatureHotkeysToConfig();

        if (_assocListInitialized)
        {

            _config.Entries = _assocEntries.Select(e => new ConfigEntry
            {

                Ext = e.Ext,
                Name = e.Name,
                Path = e.Path
            }

).ToList();


        }

        if (_imeRulesListInitialized)
        {

            _config.PerAppImeRules = _imeRules.Select(r => new ImeRuleEntry
            {

                ProcessName = r.ProcessName,
                DisplayName = r.DisplayName,
                UseChinese = r.UseChinese
            }

).ToList();


        }

        ConfigService.Save(_config);


    }

    /// <summary>显示指定标签的内容面板，隐藏已经创建的其他页面。</summary>
    private void SwitchContentPanel(string activeTag)
    {
        foreach (var (tag, panel) in _contentPanels) panel.Visibility = tag == activeTag ? Visibility.Visible : Visibility.Collapsed;


    }

    #endregion
    #region 全局搜索
    /// <summary>标题栏搜索框的条目：关键词 → 目标页面标签。</summary>
    private static readonly (string Title, string Keywords, string Tag)[] _searchEntries =    {
        ("快捷设置", "quick settings 面板快捷键 hotkey", NavTags.QuickSettings),        ("三指拖拽", "three finger drag touchpad 触控板校准", NavTags.ThreeFingerDrag),        ("悬浮暂存", "drag stash 拖拽暂存悬浮", NavTags.DragStash),        ("程序关联", "program association 打开方式关联扩展名", NavTags.ProgramAssociation),        ("输入法切换", "ime input method 输入法中英文", NavTags.PerAppIme),        ("桌面分区", "desktop cards 桌面分区卡片图标", NavTags.DesktopOrganize),        ("点击桌面", "desktop click 点击桌面显示桌面", NavTags.DesktopClick),        ("设置 · 主题", "settings theme appearance 设置外观主题浅色深色跟随系统", NavTags.AppSettings),        ("设置 · 开机启动", "settings autostart 开机启动自动", NavTags.AppSettings),
}

;

    private void SelectNavByTag(string tag)
    {

        foreach (var item in MainNav.MenuItems.Concat(MainNav.FooterMenuItems))
        {

            if (item is NavigationViewItem
                {

                    Tag: string t
                }

        navItem && t == tag)
            {

                MainNav.SelectedItem = navItem;

                return;


            }


        }


    }

    private void ShellSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {

        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var q = sender.Text?.Trim() ?? "";

        if (q.Length == 0)
        {

            sender.ItemsSource = null;

            return;


        }

        sender.ItemsSource = _searchEntries.Where(e => e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Keywords.Contains(q, StringComparison.OrdinalIgnoreCase)).Select(e => e.Title).ToList();


    }

    private void ShellSearch_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {

        if (args.SelectedItem is string title) NavigateToSearchResult(title);


    }

    private void ShellSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {

        var target = args.ChosenSuggestion as string ?? _searchEntries.Where(e => e.Title.Contains(args.QueryText ?? "", StringComparison.OrdinalIgnoreCase) || e.Keywords.Contains(args.QueryText ?? "", StringComparison.OrdinalIgnoreCase)).Select(e => e.Title).FirstOrDefault();

        if (target != null) NavigateToSearchResult(target);


    }

    private void NavigateToSearchResult(string title)
    {

        var entry = _searchEntries.FirstOrDefault(e => e.Title == title);

        if (entry.Tag != null) SelectNavByTag(entry.Tag);

        ShellSearchBox.Text = "";

        ShellSearchBox.ItemsSource = null;


    }

    private void LoadQuickSettingsFromConfig()
    {

        EnsureQuickFeaturesUi();

        _isLoadingDragStashSettings = true;

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        HotkeyQuickPanelBox.Text = GetQuickHotkey("QuickPanel", "Ctrl+Win+W");

        QuickSettingsToggle.IsOn = _config.EnableQuickPanel;

        SyncQuickPanelPaddingFromConfig();

        SyncQuickFeatureControlsFromConfig();

        SyncQuickFeaturesEnabledState();

        _isLoadingDragStashSettings = false;


    }

    /// <summary>总开关关掉时，功能快捷键区域整体置灰——它们此时确实不生效。</summary>
    private void SyncQuickFeaturesEnabledState()
    {
        // StackPanel 是 Panel，没有 IsEnabled；用 IsHitTestVisible 挡住输入即可。
        QuickFeaturesList.IsHitTestVisible = _config.EnableQuickPanel;
        QuickFeaturesList.Opacity = _config.EnableQuickPanel ? 1.0 : 0.5;


    }

    private void SyncQuickPanelPaddingFromConfig()
    {

        // 配置里仍是四个方向，界面上只给水平 / 垂直两个：上下、左右分别取同一个值。
        SetNumberBoxValue(QuickPanelPaddingHorizontalBox, _config.QuickPanelPaddingLeft);
        SetNumberBoxValue(QuickPanelPaddingVerticalBox, _config.QuickPanelPaddingTop);


    }

    internal void QuickPanelPadding_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender == null || double.IsNaN(sender.Value)) return;

        var value = (int)Math.Clamp(Math.Round(sender.Value), 0, 100);

        sender.Value = value;

        if (sender.Name == nameof(QuickPanelPaddingHorizontalBox))
        {

            _config.QuickPanelPaddingLeft = value;

            _config.QuickPanelPaddingRight = value;


        }

        else if (sender.Name == nameof(QuickPanelPaddingVerticalBox))
        {

            _config.QuickPanelPaddingTop = value;

            _config.QuickPanelPaddingBottom = value;


        }

        ScheduleQuickPanelPaddingSave();


    }

    private void ScheduleQuickPanelPaddingSave()
    {

        if (_quickPanelPaddingSaveTimer == null)
        {

            _quickPanelPaddingSaveTimer = DispatcherQueue.CreateTimer();

            _quickPanelPaddingSaveTimer.Interval = TimeSpan.FromMilliseconds(350);

            _quickPanelPaddingSaveTimer.IsRepeating = false;

            _quickPanelPaddingSaveTimer.Tick += (_, _) =>
            {

                _quickPanelPaddingSaveTimer.Stop();

                ConfigService.Save(_config);

                _quickPanel?.ApplyPanelPadding();


            }

;


        }

        _quickPanelPaddingSaveTimer.Stop();

        _quickPanelPaddingSaveTimer.Start();


    }

    private void LoadDragStashFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (DragStashToggle != null) DragStashToggle.IsOn = _config.EnableDragStash;

        var offsetX = _config.StashOffsetX > 0 ? _config.StashOffsetX : 150;

        var offsetY = _config.StashOffsetY > 0 ? _config.StashOffsetY : 40;

        SetNumberBoxValue(StashOffsetXBox, offsetX);

        SetNumberBoxValue(StashOffsetYBox, offsetY);

        _config.ProgramOffsetX = offsetX;

        _config.ProgramOffsetY = offsetY;

        _config.WindowGap = offsetY;

        _isLoadingDragStashSettings = false;


    }

    private void LoadThreeFingerDragFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (ThreeFingerDragToggle != null) ThreeFingerDragToggle.IsOn = _config.EnableThreeFingerDrag;

        if (ThreeFingerCalibrationStatusText != null) ThreeFingerCalibrationStatusText.Text = string.IsNullOrWhiteSpace(_config.ThreeFingerCalibrationUtc) ? "尚未校准 · 使用默认曲线" : "已完成本机速度校准";

        _isLoadingDragStashSettings = false;


    }

    internal async void OpenTouchpadSettings_Click(object sender, RoutedEventArgs e)
    {

        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:devices-touchpad"));


    }

    internal async void ThreeFingerCalibrate_Click(object sender, RoutedEventArgs e)
    {

        if (MainNav.XamlRoot == null || ThreeFingerCalibrateButton == null) return;

        var instructions = new ContentDialog
        {

            Title = "校准三指拖拽速度",
            Content = "接下来依次进行慢速、正常和快速三档校准。每一档都只需用单指从触控板最左侧完整滑到最右侧，然后抬起；程序会自动进入下一档。无需按下触控板。",
            PrimaryButtonText = "开始校准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainNav.XamlRoot
        }

;

        if (await instructions.ShowAsync() != ContentDialogResult.Primary) return;

        ThreeFingerCalibrateButton.IsEnabled = false;

        ThreeFingerCalibrationProgress.Visibility = Visibility.Visible;

        ThreeFingerCalibrationProgress.Value = 0;

        ThreeFingerCalibrationStatusText.Text = "校准中：请按提示完成慢速、正常和快速三次完整滑动……";

        var result = await RunGuidedThreeFingerCalibrationAsync();

        ThreeFingerCalibrationProgress.Visibility = Visibility.Collapsed;

        ThreeFingerCalibrateButton.IsEnabled = true;

        if (result == null)
        {

            ThreeFingerCalibrationStatusText.Text = "校准已取消，原有速度曲线未改变。";

            return;


        }

        if (!result.Value.Success || result.Value.FitQuality < 0.25)
        {

            ThreeFingerCalibrationStatusText.Text = "本次有效移动数据不足，原有速度曲线未改变。";

            var failed = new ContentDialog
            {

                Title = "校准未完成",
                Content = "没有采集到足够完整的慢速和快速单指移动。请重试，并尽量避免光标碰到屏幕边缘。",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot
            };

            await failed.ShowAsync();

            return;


        }

        var measured = result.Value;

        var calibrated = new TouchpadCalibrationResult(true, ApplyCalibrationFineTune(ThreeFingerCalibrationDefaults.LowSpeedGain, measured.LowSpeedGain), ApplyCalibrationFineTune(ThreeFingerCalibrationDefaults.HighSpeedGain, measured.HighSpeedGain), ApplyCalibrationFineTune(ThreeFingerCalibrationDefaults.AccelerationStart, measured.AccelerationStart), ApplyCalibrationFineTune(ThreeFingerCalibrationDefaults.AccelerationEnd, measured.AccelerationEnd), measured.SampleCount, measured.FitQuality);

        _config.ThreeFingerLowSpeedGain = calibrated.LowSpeedGain;

        _config.ThreeFingerHighSpeedGain = calibrated.HighSpeedGain;

        _config.ThreeFingerAccelerationStart = calibrated.AccelerationStart;

        _config.ThreeFingerAccelerationEnd = calibrated.AccelerationEnd;

        _config.ThreeFingerCalibrationUtc = DateTime.UtcNow.ToString("O");

        ConfigService.Save(_config);

        _touchpadInputHost.ConfigurePointerCurve(calibrated.LowSpeedGain, calibrated.HighSpeedGain, calibrated.AccelerationStart, calibrated.AccelerationEnd);

        ThreeFingerCalibrationStatusText.Text =
            $"校准完成：已分析{calibrated.SampleCount}个有效移动样本。";

        var completed = new ContentDialog
        {

            Title = "校准完成",
            Content = "已在稳定的基础速度上应用本机微调，并立即生效。校准结果设有安全范围，不会因一次异常滑动而大幅偏离默认手感。",
            CloseButtonText = "完成",
            XamlRoot = MainNav.XamlRoot
        };

        await completed.ShowAsync();


    }

    private static double ApplyCalibrationFineTune(double baseline, double measured)
    {

        // First limit a single measurement to ±40%, then blend only half of that difference.
        // The applied result therefore stays within ±20% of the stable factory curve.
        var boundedMeasurement = Math.Clamp(measured, baseline * 0.60, baseline * 1.40);

        return Math.Round(baseline + (boundedMeasurement - baseline) * 0.50, 3);


    }

    private async Task<TouchpadCalibrationResult?> RunGuidedThreeFingerCalibrationAsync()
    {

        var captureStarted = false;

        var guide = new ThreeFingerCalibrationWindow();

        var completed = await guide.RunAsync(() =>
        {

            _touchpadInputHost.BeginCalibration(_config.EnableThreeFingerDrag);

            captureStarted = true;


        }

, () => _touchpadInputHost.IsCalibrationFingerDown, () => _touchpadInputHost.CalibrationCapturedFrameCount, progress => ThreeFingerCalibrationProgress.Value = progress);

        if (!captureStarted) return null;

        var result = _touchpadInputHost.CompleteCalibration();

        return completed ? result : null;


    }

    private void LoadAppSettingsFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (AutoStartToggle != null)
        {

            var autoStartEnabled = AutostartService.IsEnabled();

            AutoStartToggle.IsOn = autoStartEnabled;

            _config.AutoStart = autoStartEnabled;


        }

        SyncThemeRadioFromConfig();

        _isLoadingDragStashSettings = false;


    }

    private void SyncThemeRadioFromConfig()
    {

        if (ThemeRadioButtons == null) return;

        _isLoadingDragStashSettings = true;

        var theme = ThemePreference.Normalize(_config.AppTheme);

        ThemeRadioButtons.SelectedItem = theme switch
        {

            ThemePreference.Light => ThemeRadioLight,
            ThemePreference.Dark => ThemeRadioDark,
            _ => ThemeRadioSystem
        }

;

        _isLoadingDragStashSettings = false;


    }

    internal void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (ThemeRadioButtons.SelectedItem is not RadioButton selected) return;

        var theme = ThemePreference.Normalize(selected.Tag as string);

        _config.AppTheme = theme;

        ThemeService.SetPreference(theme);

        ConfigService.Update(c => c.AppTheme = theme);

        ApplyShellStyle();

        UpdateNavStatusIndicators();


    }

    private static void SetNumberBoxValue(NumberBox? box, int value)
    {

        if (box == null) return;

        box.Value = value;


    }

    #endregion
    public void ApplyUiStyleSurfaces() => ApplyShellStyle();

    /// <summary>同步主题、Mica 材质与壳层资源。</summary>
    private void ApplyShellStyle()
    {
        var theme = ThemeService.EffectiveTheme;

        var isMica = UiStyleService.IsMica;

        var contentBrush = WindowHelper.GetCodexContentBrush(theme, isMica);

        var sidebarBrush = WindowHelper.GetCodexSidebarBrush(theme);

        var shellBrush = WindowHelper.GetCodexShellBrush(theme, isMica);

        var surfaceBrush = WindowHelper.GetCodexSurfaceBrush(theme);

        var borderBrush = WindowHelper.GetCodexBorderBrush(theme);

        ShellRoot.RequestedTheme = theme;

        MainNav.RequestedTheme = theme;

        UpdateLocalBrush("CodexContentMicaBrush", contentBrush.Color);

        UpdateLocalBrush("CodexSidebarBrush", sidebarBrush.Color);

        UpdateLocalBrush("CodexSurfaceBrush", surfaceBrush.Color);

        UpdateLocalBrush("CodexBorderBrush", borderBrush.Color);

        // 全窗口只有两种颜色：外壳层（标题栏 + 侧栏 + 内容区背后）与叠在其上的内容层。
        // 外壳层必须由 ShellRoot 统一铺满，否则内容区左上角的圆角缺口会露出第三种颜色。
        ShellRoot.Background = shellBrush;
        AppTitleBar.Background = new SolidColorBrush(Colors.Transparent);

        ContentFrame.Background = contentBrush;

        WindowHelper.ApplyWindowBackdrop(this);

        WindowHelper.ApplyTitleBarButtonColors(this, theme);

        RefreshQuickFeatureCardsIfNeeded();


    }

    private void UpdateLocalBrush(string key, Color color)
    {

        if (ShellRoot.Resources[key] is SolidColorBrush brush) brush.Color = color;


    }

    private void RefreshQuickFeatureCardsIfNeeded()
    {

        if (!_quickFeaturesUiBuilt) return;

        _quickFeaturesUiBuilt = false;

        _quickFeatureUi.Clear();

        QuickFeaturesList.Children.Clear();

        EnsureQuickFeaturesUi();

        SyncQuickFeatureControlsFromConfig();


    }

    #region 侧边栏状态

    /// <summary>同步功能菜单项右侧状态圆点：强调色表示开启，弱化色表示关闭。</summary>
    private void UpdateNavStatusIndicators()
    {
        var onBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));

        var offBrush = Application.Current.Resources["TextFillColorDisabledBrush"] as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

        // 圆点一律只反映该页的总开关：关掉「快捷设置」后即使个别功能还留着开关状态，
        // 面板也不会响应，圆点必须跟着灭掉。
        SetNavDot(NavDotQuickSettings, _config.EnableQuickPanel, onBrush, offBrush);
        SetNavDot(NavDotThreeFingerDrag, _config.EnableThreeFingerDrag, onBrush, offBrush);

        SetNavDot(NavDotDragStash, _config.EnableDragStash, onBrush, offBrush);

        SetNavDot(NavDotProgramAssoc, _config.EnableProgramAssoc, onBrush, offBrush);

        SetNavDot(NavDotPerAppIme, _config.EnablePerAppIme, onBrush, offBrush);

        SetNavDot(NavDotDesktopOrganize, _config.EnableDesktopCard, onBrush, offBrush);

        SetNavDot(NavDotDesktopClick, _config.EnableDesktopClickToShow, onBrush, offBrush);


    }

    private static void SetNavDot(Microsoft.UI.Xaml.Shapes.Ellipse? dot, bool enabled, SolidColorBrush onBrush, SolidColorBrush offBrush)
    {

        if (dot == null) return;

        dot.Fill = enabled ? onBrush : offBrush;


    }

    #endregion
    #region 全局快捷键
    private void InitGlobalHotkeys()
    {

        try
        {

            _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

            _hotkeyWndProc = HotkeyWndProc;

            var newProcPtr = Marshal.GetFunctionPointerForDelegate(_hotkeyWndProc);

            var oldProc = NativeSetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProcPtr);

            _originalWndProc = Marshal.GetDelegateForFunctionPointer<WNDPROC>(oldProc);

            if (_config.EnableThreeFingerDrag) _touchpadInputHost.Start(true);

            RegisterAllHotkeys();


        }

        catch
        {

            /* 注册失败时仅无法使用快捷键，不影响其他功能 */
        }


    }

    private LRESULT HotkeyWndProc(HWND hwnd, uint uMsg, WPARAM wParam, LPARAM lParam)
    {

        if (uMsg == WM_HOTKEY)
        {

            var id = (int)(nuint)wParam.Value;

            if (id == QuickSettingFeatures.HotkeyIdQuickPanel)
            {

                ToggleQuickPanel();

                return (LRESULT)IntPtr.Zero;


            }

            if (id == HotkeyIdDesktopCard)
            {

                RaiseDesktopCards();

                return (LRESULT)IntPtr.Zero;


            }

            var feature = QuickSettingFeatures.FindByHotkeyId(id);

            if (feature != null)
            {

                _ = ExecuteQuickFeatureAsync(feature.Id);

                return (LRESULT)IntPtr.Zero;


            }


        }

        return PInvoke.CallWindowProc(_originalWndProc!, hwnd, uMsg, wParam, lParam);


    }

    private void RegisterAllHotkeys()
    {

        UnregisterAllHotkeys();

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        // 「快捷设置」总开关关掉时，功能快捷键也一并注销。否则侧栏圆点显示已关闭，        // Ctrl+Win+N 之类的键却还在后台生效，用户会以为没关掉。
        if (_config.EnableQuickPanel)
        {
            foreach (var feature in QuickSettingFeatures.All)
            {

                if (!feature.GetEnabled(_config)) continue;

                var hotkey = GetQuickHotkey(feature.Id, feature.DefaultHotkey);

                RegisterOneHotkey(_hwnd, feature.HotkeyId, hotkey);


            }


        }

        if (_config.EnableQuickPanel) RegisterOneHotkey(_hwnd, QuickSettingFeatures.HotkeyIdQuickPanel, GetQuickHotkey("QuickPanel", "Ctrl+Win+W"));

        RegisterOneHotkey(_hwnd, HotkeyIdDesktopCard, GetDesktopCardHotkey());


    }

    private void UnregisterAllHotkeys()
    {

        foreach (var feature in QuickSettingFeatures.All)
        {

            try
            {

                PInvoke.UnregisterHotKey(_hwnd, feature.HotkeyId);


            }

            catch
            {

                /* ignore */
            }


        }

        try
        {

            PInvoke.UnregisterHotKey(_hwnd, QuickSettingFeatures.HotkeyIdQuickPanel);


        }

        catch
        {

            /* ignore */
        }

        try
        {

            PInvoke.UnregisterHotKey(_hwnd, HotkeyIdDesktopCard);


        }

        catch
        {

            /* ignore */
        }


    }

    private static void RegisterOneHotkey(HWND hwnd, int id, string? hotkeyString)
    {

        var parsed = HotkeyHelper.Parse(hotkeyString);

        if (parsed == null) return;

        try
        {

            PInvoke.RegisterHotKey(hwnd, id, HotkeyHelper.ToWin32(parsed.Value.Modifiers), parsed.Value.VirtualKey);


        }

        catch
        {

            /* ignore */
        }


    }

    #endregion
    #region 快捷设置
    private void EnsureQuickFeaturesUi()
    {

        if (_quickFeaturesUiBuilt) return;

        _quickFeaturesUiBuilt = true;

        // 卡片与标题样式来自全局 UI 规范 Styles/PageStyles.xaml（不要在此处另写外观）。
        var appResources = Application.Current.Resources;
        var cardStyle = (Style)appResources["PageCardStyle"];

        foreach (var feature in QuickSettingFeatures.All)
        {

            var toggle = new ToggleSwitch
            {

                OnContent = "已启用",
                OffContent = "已禁用",
                VerticalAlignment = VerticalAlignment.Center,
                Tag = feature.Id,
            }

;

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, $"{feature.DisplayName}开关");

            toggle.Toggled += QuickFeatureToggle_Toggled;

            var hotkeyBox = new TextBox
            {

                PlaceholderText = $"例如 {feature.DefaultHotkey}",
                MinWidth = 120,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = feature.Id,
            }

;

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(hotkeyBox, $"{feature.DisplayName}快捷键");

            var testButton = new Button
            {

                Content = "测试",
                MinWidth = 56,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = feature.Id,
            }

;

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(testButton, $"测试{feature.DisplayName}");

            testButton.Click += QuickFeatureTest_Click;

            var settingRow = new Grid
            {

                MinHeight = 44,
                VerticalAlignment = VerticalAlignment.Center,
                ColumnSpacing = 14,
            }

;

            settingRow.ColumnDefinitions.Add(new ColumnDefinition
            {

                Width = new GridLength(150)
            }

);

            settingRow.ColumnDefinitions.Add(new ColumnDefinition
            {

                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 120,
            }

);

            settingRow.ColumnDefinitions.Add(new ColumnDefinition
            {

                Width = GridLength.Auto
            }

);

            settingRow.ColumnDefinitions.Add(new ColumnDefinition
            {

                Width = GridLength.Auto
            }

);

            var title = new TextBlock
            {

                Text = feature.DisplayName,
                Style = appResources["PageSubtitleTextStyle"] as Style,
                VerticalAlignment = VerticalAlignment.Center,
            }

;

            Grid.SetColumn(title, 0);

            Grid.SetColumn(hotkeyBox, 1);

            Grid.SetColumn(toggle, 2);

            Grid.SetColumn(testButton, 3);

            settingRow.Children.Add(title);

            settingRow.Children.Add(hotkeyBox);

            settingRow.Children.Add(toggle);

            settingRow.Children.Add(testButton);

            var card = new Border
            {

                Style = cardStyle,
                Child = settingRow
            }

;

            QuickFeaturesList.Children.Add(card);

            _quickFeatureUi[feature.Id] = new QuickFeatureUi
            {

                Toggle = toggle,
                HotkeyBox = hotkeyBox
            }

;


        }


    }

    private string GetQuickHotkey(string key, string fallback)
    {

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        return _config.QuickSettingsHotkeys.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;


    }

    private void SyncQuickFeatureControlsFromConfig()
    {

        foreach (var feature in QuickSettingFeatures.All)
        {

            if (!_quickFeatureUi.TryGetValue(feature.Id, out var ui)) continue;

            ui.HotkeyBox.Text = GetQuickHotkey(feature.Id, feature.DefaultHotkey);

            ui.Toggle.IsOn = feature.GetEnabled(_config);


        }


    }

    private void SaveQuickFeatureHotkeysToConfig()
    {

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        foreach (var feature in QuickSettingFeatures.All)
        {

            if (!_quickFeatureUi.TryGetValue(feature.Id, out var ui)) continue;

            _config.QuickSettingsHotkeys[feature.Id] = ui.HotkeyBox.Text?.Trim() ?? "";


        }


    }

    internal void QuickSettings_TestPanel_Click(object sender, RoutedEventArgs e)
    {

        EnsureQuickPanel();

        _quickPanel!.ShowAtCenter();


    }

    private async void QuickFeatureTest_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not Button
            {

                Tag: string featureId
            }

) return;

        await ShowQuickFeatureTestResultAsync(featureId);


    }

    private async Task ShowQuickFeatureTestResultAsync(string featureId)
    {

        var feature = QuickSettingFeatures.Find(featureId);

        if (feature == null || MainNav.XamlRoot == null) return;

        var (ok, message) = await ExecuteQuickFeatureAsync(featureId);

        var dialog = new ContentDialog
        {

            Title = feature.DisplayName,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot,
        }

;

        await dialog.ShowAsync();


    }

    private static async Task<(bool Ok, string Message)> ExecuteQuickFeatureAsync(string featureId)
    {

        switch (featureId)
        {

            case "SwitchNetwork":
                {

                    var ok = await NetworkToggleService.SwitchWifiAndEthernetAsync();

                    return (ok, ok ? "已在宽带与 Wi-Fi 之间切换。" : "无法切换（请确认无线电权限，宽带切换或需管理员权限）。");


                }

            case "ToggleMute":
                {

                    var ok = AudioToggleService.ToggleMute();

                    return (ok, ok ? "已切换系统静音状态。" : "无法切换静音状态。");


                }

            default: return (false, "未知功能。");


        }


    }

    internal void QuickSettingsToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        _config.EnableQuickPanel = QuickSettingsToggle.IsOn;

        RegisterAllHotkeys();

        SyncQuickFeaturesEnabledState();

        UpdateNavStatusIndicators();


    }

    private void QuickFeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender is not ToggleSwitch
            {

                Tag: string featureId
            }

) return;

        var feature = QuickSettingFeatures.Find(featureId);

        if (feature == null) return;

        feature.SetEnabled(_config, ((ToggleSwitch)sender).IsOn);

        RegisterAllHotkeys();

        UpdateNavStatusIndicators();


    }

    internal void QuickSettings_Save_Click(object sender, RoutedEventArgs e)
    {

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        _config.QuickSettingsHotkeys["QuickPanel"] = HotkeyQuickPanelBox?.Text?.Trim() ?? "";

        SaveQuickFeatureHotkeysToConfig();

        ConfigService.Save(_config);

        RegisterAllHotkeys();

        if (MainNav.XamlRoot == null) return;

        var dialog = new ContentDialog
        {

            Title = "快捷设置",
            Content = "快捷键已保存，立即生效。",
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot,
        }

;

        _ = dialog.ShowAsync();


    }

    private void EnsureQuickPanel()
    {

        if (_quickPanel != null) return;

        _quickPanel = new QuickSettingsPopupWindow();

        ThemeService.Register(_quickPanel);

        UiStyleService.Register(_quickPanel);

        WindowRegistry.Register(_quickPanel);


    }

    private void ToggleQuickPanel()
    {

        EnsureQuickPanel();

        _quickPanel!.Toggle();


    }

    private DesktopCardManager EnsureDesktopCardManager()
    {

        if (_desktopCardManager != null) return _desktopCardManager;

        var registry = WindowRegistry;

        _desktopCardManager = new DesktopCardManager(w => registry.Register(w));
        // 卡片上拖动图标换分区后，设置页的分区列表要跟着刷新，否则下次保存会用旧副本覆盖。
        _desktopCardManager.ZonesChangedExternally += (_, _) =>
            DispatcherQueue.TryEnqueue(ReloadDesktopZonesFromConfig);

        return _desktopCardManager;


    }

    /// <summary>主窗口完成首帧后再导入旧数据并渐进创建桌面卡片。</summary>
    internal async void InitializeDesktopCardsAfterFirstFrame()
    {
        if (!_config.EnableDesktopCard) return;

        try
        {

            // 给主窗口一次自然呈现的机会，再开始创建多个独立 HWND。
            await Task.Delay(80);
            await EnsureDesktopCardManager().InitializeAsync();


        }

        catch (Exception ex)
        {

            ErrorReporter.Log("MainWindow.InitializeDesktopCardsAfterFirstFrame", ex);


        }


    }

    /// <summary>快捷键「呼出」：把所有分区卡片抬回最前面。</summary>
    private void RaiseDesktopCards() => EnsureDesktopCardManager().RaiseAll();
    /// <summary>获取桌面卡片当前生效的快捷键（供设置页读取 / 显示）。</summary>
    private string GetDesktopCardHotkey() => GetQuickHotkey(DesktopCardHotkeyKey, DesktopCardHotkeyDefault);
    internal void DesktopCard_ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {

        _config.QuickSettingsHotkeys ??= new Dictionary<string, string>();

        _config.QuickSettingsHotkeys[DesktopCardHotkeyKey] = HotkeyDesktopCardBox?.Text?.Trim() ?? "";

        ConfigService.Save(_config);

        RegisterAllHotkeys();


    }

    internal void DesktopCardToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        var on = ((ToggleSwitch)sender).IsOn;

        _config.EnableDesktopCard = on;

        ConfigService.Update(c => c.EnableDesktopCard = on);

        EnsureDesktopCardManager().SetEnabled(on);

        UpdateCollectStatus();

        UpdateNavStatusIndicators();


    }

    /// <summary>分区增删改后，若卡片已开启则同步。</summary>
    /// <summary>从配置重新载入分区列表（卡片侧拖放换区后调用），保留当前选中项。</summary>
    private void ReloadDesktopZonesFromConfig()
    {
        var selectedName = (ZonesListView.SelectedItem as DesktopZone)?.Name;
        _desktopZones.Clear();
        foreach (var zone in SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>())
            _desktopZones.Add(CloneZone(zone));
        _config.DesktopZones = _desktopZones.Select(CloneZone).ToList();
        UpdateZoneCount();
        if (selectedName != null)
        {
            ZonesListView.SelectedItem = _desktopZones
                .FirstOrDefault(z => string.Equals(z.Name, selectedName, StringComparison.Ordinal));
        }
    }

    private void SyncDesktopCardsIfEnabled()
    {
        if (_desktopCardManager?.IsEnabled == true) _desktopCardManager.Sync();


    }

    internal void DesktopCardMaxColumns_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        var v = (int)Math.Round(sender.Value);

        if (v < 1) v = 1;

        if (v > 5) v = 5;

        _config.DesktopCardMaxColumns = v;

        ConfigService.Update(c => c.DesktopCardMaxColumns = v);

        // 重排现有卡片宽度
        if (_desktopCardManager != null && _desktopCardManager.IsEnabled) _desktopCardManager.SyncContent();

    }

    internal void DesktopCardGap_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender == null || double.IsNaN(sender.Value)) return;

        var v = (int)Math.Round(sender.Value);

        // 2 个 NumberBox 共用此 handler：DesktopCardGapBox（间距）+ DesktopCardMarginBox（三边留白）
        if (sender == DesktopCardGapBox)
        {
            _config.DesktopCardGap = v;

            ConfigService.Update(c => c.DesktopCardGap = v);


        }

        else if (sender == DesktopCardMarginBox)
        {

            _config.DesktopCardMargin = v;

            ConfigService.Update(c => c.DesktopCardMargin = v);


        }

        _desktopCardManager?.Layout();


    }

    /// <summary>刷新「已收纳 N 项」提示。</summary>
    private void UpdateCollectStatus()
    {
        if (CollectStatusText == null) return;

        var n = DesktopCollectService.CollectedCount();

        CollectStatusText.Text = n > 0 ? $"已收纳 {n}项" : "";


    }

    internal async void DesktopRestore_Click(object sender, RoutedEventArgs e)
    {

        if (MainNav.XamlRoot == null) return;

        var pending = DesktopCollectService.CollectedCount();

        if (pending == 0)
        {

            await new ContentDialog
            {

                Title = "没有可还原的项",
                Content = "收纳日志为空，桌面文件都在原位。",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

.ShowAsync();

            return;


        }

        var confirm = new ContentDialog
        {

            Title = "确认还原",
            Content = $"将把 {pending}个已收纳的项目移回桌面原位置。",
            PrimaryButtonText = "全部还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = MainNav.XamlRoot,
        }

;

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // 先关闭自动收纳，否则文件回到桌面后会立即再次进入卡片。
        _config.EnableDesktopCard = false;
        ConfigService.Update(c => c.EnableDesktopCard = false);

        _desktopCardManager?.SetEnabled(false);

        if (DesktopCardToggle != null)
        {

            _isLoadingDragStashSettings = true;

            DesktopCardToggle.IsOn = false;

            _isLoadingDragStashSettings = false;


        }

        var restored = DesktopCollectService.RestoreAll(out var failed);

        UpdateCollectStatus();

        SyncDesktopCardsIfEnabled();

        await new ContentDialog
        {

            Title = "还原完成",
            Content = failed > 0 ? $"已还原 {restored}项，{failed}项失败（保留在日志里，可再试一次）。" : $"已把 {restored}项送回桌面。",
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot,
        }

.ShowAsync();


    }

    private void SyncMainToggle(ToggleSwitch? toggle, bool enabled)
    {

        if (toggle == null) return;

        _isLoadingDragStashSettings = true;

        toggle.IsOn = enabled;

        _isLoadingDragStashSettings = false;


    }

    #endregion
    #region 悬浮暂存
    internal void DragStashToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (DragStashToggle == null) return;

        _config.EnableDragStash = DragStashToggle.IsOn;

        (App.Current as App)?.SetDragStashEnabled(_config.EnableDragStash);

        UpdateNavStatusIndicators();


    }

    internal void DragStash_Show_Click(object sender, RoutedEventArgs e)
    {

        (App.Current as App)?.ShowDragStashWindow();


    }

    internal async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (AutoStartToggle == null) return;

        var enable = AutoStartToggle.IsOn;

        if (!AutostartService.SetEnabled(enable, out var error))
        {

            _isLoadingDragStashSettings = true;

            AutoStartToggle.IsOn = !enable;

            _isLoadingDragStashSettings = false;

            if (MainNav.XamlRoot != null)
            {

                var dialog = new ContentDialog
                {

                    Title = "开机自动启动",
                    Content = $"设置失败：{error}",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

;

                await dialog.ShowAsync();


            }

            return;


        }

        _config.AutoStart = enable;

        ConfigService.Update(c => c.AutoStart = enable);


    }

    internal async void ThreeFingerDragToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (ThreeFingerDragToggle == null) return;

        _config.EnableThreeFingerDrag = ThreeFingerDragToggle.IsOn;

        _touchpadInputHost.SetEnabled(_config.EnableThreeFingerDrag);

        ConfigService.Update(c => c.EnableThreeFingerDrag = _config.EnableThreeFingerDrag);

        if (_config.EnableThreeFingerDrag && !_touchpadInputHost.HasTouchpad && MainNav.XamlRoot != null)
        {

            var dialog = new ContentDialog
            {

                Title = "三指拖拽",
                Content = "未检测到 Windows 精确触控板，无法使用此功能。",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

;

            await dialog.ShowAsync();


        }

        UpdateNavStatusIndicators();


    }

    internal void PositionOffset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender == null) return;

        var value = (int)Math.Max(0, Math.Round(sender.Value));

        sender.Value = value;

        if (sender.Name == nameof(StashOffsetXBox))
        {

            _config.StashOffsetX = value;

            _config.ProgramOffsetX = value;


        }

        else if (sender.Name == nameof(StashOffsetYBox))
        {

            _config.StashOffsetY = value;

            _config.ProgramOffsetY = value;

            _config.WindowGap = value;

            // 垂直距离即两窗间距
        }


    }

    #endregion
    #region 程序关联（内嵌编辑）
    private void LoadProgramAssocFromConfig()
    {

        _isLoadingDragStashSettings = true;

        ProgramAssocToggle.IsOn = _config.EnableProgramAssoc;

        _isLoadingDragStashSettings = false;

        LoadAssocEntries();


    }

    internal void ProgramAssocToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        _config.EnableProgramAssoc = ProgramAssocToggle.IsOn;

        (App.Current as App)?.SetProgramAssociationEnabled(_config.EnableProgramAssoc);

        UpdateNavStatusIndicators();


    }

    /// <summary>加载程序关联列表到 UI。</summary>
    private void LoadAssocEntries()
    {
        _assocListInitialized = true;

        _assocEntries.Clear();

        foreach (var e in _config.Entries) _assocEntries.Add(new ConfigEntry
        {

            Ext = e.Ext,
            Name = e.Name,
            Path = e.Path
        }

);

        AssocListView.ItemsSource = _assocEntries;

        UpdateAssocCount();


    }

    private void UpdateAssocCount()
    {

        AssocCountText.Text = _assocEntries.Count > 0 ? $"{_assocEntries.Count}项" : "暂无关联";

        AssocEmptyHint.Visibility = _assocEntries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    /// <summary>保存当前列表回 Config 并持久化。</summary>
    private void SaveAssocEntries()
    {
        _config.Entries = _assocEntries.Select(e => new ConfigEntry
        {

            Ext = e.Ext,
            Name = e.Name,
            Path = e.Path
        }

).ToList();

        ConfigService.Save(_config);

        UpdateAssocCount();


    }

    private void ShowAssocEditPanel(string title, string ext, string name, string path)
    {

        AssocEditTitle.Text = title;

        AssocExtBox.Text = ext;

        AssocNameBox.Text = name;

        AssocPathBox.Text = path;

        AssocEditPanel.Visibility = Visibility.Visible;

        AssocEditColumn.Width = new GridLength(296);


    }

    private void HideAssocEditPanel()
    {

        AssocEditPanel.Visibility = Visibility.Collapsed;

        AssocEditColumn.Width = new GridLength(0);


    }

    internal void Assoc_New_Click(object sender, RoutedEventArgs e)
    {

        _assocEditingIndex = -1;

        ShowAssocEditPanel("新建关联", "", "", "");


    }

    internal void Assoc_Edit_Click(object sender, RoutedEventArgs e)
    {

        if (AssocListView.SelectedItem is not ConfigEntry entry) return;

        _assocEditingIndex = _assocEntries.IndexOf(entry);

        if (_assocEditingIndex < 0) return;

        ShowAssocEditPanel("编辑关联", entry.Ext, entry.Name, entry.Path);


    }

    internal void Assoc_Delete_Click(object sender, RoutedEventArgs e)
    {

        if (AssocListView.SelectedItem is not ConfigEntry entry) return;

        _assocEntries.Remove(entry);

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal async void Assoc_Browse_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileOpenPicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".exe");

        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        AssocPathBox.Text = file.Path;

        if (string.IsNullOrWhiteSpace(AssocNameBox.Text)) AssocNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(file.Name);


    }

    internal void Assoc_Confirm_Click(object sender, RoutedEventArgs e)
    {

        var ext = (AssocExtBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(ext)) return;

        if (!ext.StartsWith('.')) ext = "." + ext;

        var path = (AssocPathBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(path)) return;

        var name = (AssocNameBox.Text ?? "").Trim();

        var entry = new ConfigEntry
        {

            Ext = ext.ToLowerInvariant(),
            Name = name,
            Path = path
        };

        if (_assocEditingIndex < 0) _assocEntries.Add(entry);

        else _assocEntries[_assocEditingIndex] = entry;

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal void Assoc_Cancel_Click(object sender, RoutedEventArgs e) => HideAssocEditPanel();

    internal async void Assoc_Import_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileOpenPicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".json");

        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        Config? loaded;

        try
        {

            var json = await File.ReadAllTextAsync(file.Path);

            loaded = JsonSerializer.Deserialize<Config>(json);


        }

        catch (Exception ex)
        {

            if (MainNav.XamlRoot != null) await new ContentDialog
            {

                Title = "导入失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot
            }

.ShowAsync();

            return;


        }

        if (loaded == null) return;

        if (MainNav.XamlRoot != null)
        {

            var confirm = new ContentDialog
            {

                Title = "确认导入",
                Content = "导入将覆盖当前程序关联，是否继续？",
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                XamlRoot = MainNav.XamlRoot
            }

;

            if (await confirm.ShowAsync().AsTask() != ContentDialogResult.Primary) return;


        }

        _assocEntries.Clear();

        foreach (var entry in loaded.Entries) _assocEntries.Add(new ConfigEntry
        {

            Ext = entry.Ext,
            Name = entry.Name,
            Path = entry.Path
        }

);

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal async void Assoc_Export_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileSavePicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("JSON 配置", new[] {

 ".json"
}

);

        picker.SuggestedFileName = "WinTools_config.json";

        var file = await picker.PickSaveFileAsync().AsTask();

        if (file == null) return;

        // 先同步列表到 config 再导出完整配置
        SaveAssocEntries();
        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
        {

            WriteIndented = true
        }

);

        await File.WriteAllTextAsync(file.Path, json);

        if (MainNav.XamlRoot != null) await new ContentDialog
        {

            Title = "已导出",
            Content = $"配置已保存至：{file.Path}",
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot
        }

.ShowAsync();


    }

    #endregion
    #region 桌面分区
    private void LoadDesktopClickFromConfig()
    {

        _isLoadingDragStashSettings = true;

        DesktopClickToggle.IsOn = _config.EnableDesktopClickToShow;

        _isLoadingDragStashSettings = false;


    }

    internal void DesktopClickToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        var enabled = DesktopClickToggle.IsOn;

        _config.EnableDesktopClickToShow = enabled;

        ConfigService.Update(c => c.EnableDesktopClickToShow = enabled);

        (App.Current as App)?.SetDesktopClickEnabled(enabled);

        UpdateNavStatusIndicators();


    }

    private void LoadDesktopOrganizeFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (HotkeyDesktopCardBox != null) HotkeyDesktopCardBox.Text = GetDesktopCardHotkey();

        if (DesktopCardToggle != null) DesktopCardToggle.IsOn = _config.EnableDesktopCard;

        SetNumberBoxValue(DesktopCardGapBox, _config.DesktopCardGap);

        SetNumberBoxValue(DesktopCardMaxColumnsBox, _config.DesktopCardMaxColumns);

        SetNumberBoxValue(DesktopCardMarginBox, _config.DesktopCardMargin);

        _isLoadingDragStashSettings = false;

        UpdateCollectStatus();

        LoadDesktopZones();


    }

    private void LoadDesktopZones()
    {

        _config.DesktopZones ??= new List<DesktopZone>();

        // 首次使用（未配置）时填入默认分区，并写回配置
        if (_config.DesktopZones.Count == 0)
        {
            _config.DesktopZones = DesktopZone.CreateDefaults();

            ConfigService.Update(c => c.DesktopZones = _config.DesktopZones);


        }

        _desktopZones.Clear();

        foreach (var zone in _config.DesktopZones) _desktopZones.Add(CloneZone(zone));

        ZonesListView.ItemsSource = _desktopZones;

        UpdateZoneCount();


    }

    private static DesktopZone CloneZone(DesktopZone source) => new()
    {

        Name = source.Name,
        Keywords = new List<string>(source.Keywords ?? new List<string>()),
        Items = new List<string>(source.Items ?? new List<string>())
    }

;

    private void UpdateZoneCount()
    {

        ZoneCountText.Text = _desktopZones.Count > 0 ? $"{_desktopZones.Count}个" : "暂无分区";

        ZoneEmptyHint.Visibility = _desktopZones.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    /// <summary>把分区写回配置，并同步桌面卡片。</summary>
    private void SaveDesktopZones()
    {
        _config.DesktopZones = _desktopZones.Select(CloneZone).ToList();

        ConfigService.Update(c => c.DesktopZones = _config.DesktopZones);

        UpdateZoneCount();

        SyncDesktopCardsIfEnabled();


    }

    private void ShowZoneEditPanel(string title)
    {

        ZoneEditTitle.Text = title;

        ZoneEditPanel.Visibility = Visibility.Visible;

        ZoneEditColumn.Width = new GridLength(336);


    }

    private void HideZoneEditPanel()
    {

        ZoneEditPanel.Visibility = Visibility.Collapsed;

        ZoneEditColumn.Width = new GridLength(0);

        _zoneEditingIndex = -1;


    }

    internal void Zone_New_Click(object sender, RoutedEventArgs e)
    {

        _zoneEditingIndex = -1;

        ZoneNameBox.Text = "";

        ZoneKeywordsBox.Text = "";

        PopulateZoneFilesList(null);

        ShowZoneEditPanel("新建分区");


    }

    internal void Zones_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => EditSelectedZone();

    internal void Zone_Edit_Click(object sender, RoutedEventArgs e) => EditSelectedZone();

    /// <summary>「同步桌面」：按当前分区规则重新收纳桌面项目并刷新卡片。</summary>
    internal async void Zone_Reapply_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.EnableDesktopCard)
        {
            if (MainNav.XamlRoot != null)
            {
                var info = new ContentDialog
                {
                    Title = "桌面分区",
                    Content = "请先打开页面右上角的总开关，再同步桌面。",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                };
                await info.ShowAsync();
            }
            return;
        }

        EnsureDesktopCardManager().Sync();
        UpdateCollectStatus();
    }

    /// <summary>「恢复默认分区」：用内置的推荐分类替换当前分区列表。</summary>
    internal async void Zone_ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MainNav.XamlRoot != null)
        {
            var confirm = new ContentDialog
            {
                Title = "恢复默认分区？",
                Content = "当前分区列表会被内置的推荐分类替换，已收纳的文件会按新分区重新归类。",
                PrimaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = MainNav.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        _desktopZones.Clear();
        foreach (var zone in DesktopZone.CreateDefaults())
            _desktopZones.Add(zone);
        SaveDesktopZones();
        HideZoneEditPanel();
    }

    private void EditSelectedZone()
    {

        if (ZonesListView.SelectedItem is not DesktopZone zone) return;

        _zoneEditingIndex = _desktopZones.IndexOf(zone);

        if (_zoneEditingIndex < 0) return;

        ZoneNameBox.Text = zone.Name;

        ZoneKeywordsBox.Text = string.Join(Environment.NewLine, zone.Keywords ?? new List<string>());

        PopulateZoneFilesList(zone.Items);

        ShowZoneEditPanel("编辑分区");


    }

    /// <summary>拖动排序完成后保存新顺序（此时 _desktopZones 已按新顺序排列）。</summary>
    internal void Zones_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        SaveDesktopZones();


    }

    /// <summary>填充“指定文件”列表：列出当前桌面图标，并勾选已归入本分区的项。</summary>
    private void PopulateZoneFilesList(IEnumerable<string>? selectedNames)
    {
        var selected = new List<string>(selectedNames ?? Enumerable.Empty<string>());

        List<string> names;

        try
        {

            names = (App.Current as App)?.GetDesktopItemNames() ?? new List<string>();


        }

        catch
        {

            names = new List<string>();


        }

        // 已指定但当前桌面已不存在的名称也保留，便于编辑时仍可见
        foreach (var s in selected) if (!names.Any(n => NameMatches(n, s))) names.Add(s);
        _zoneFileAll = names;

        // 记录已勾选（统一为候选列表中的规范名称）
        _zoneSelectedNames.Clear();
        foreach (var s in selected)
        {

            var canonical = _zoneFileAll.FirstOrDefault(n => NameMatches(n, s)) ?? s;

            _zoneSelectedNames.Add(canonical);


        }

        if (ZoneFilesSearchBox != null) ZoneFilesSearchBox.Text = "";

        RefreshZoneFilesView("");


    }

    /// <summary>按搜索文本刷新可见候选，并恢复已勾选项的选中状态。</summary>
    private void RefreshZoneFilesView(string? filter)
    {
        var f = filter?.Trim() ?? "";

        _suppressZoneSelectionSync = true;

        _zoneFileItems.Clear();

        foreach (var name in _zoneFileAll) if (f.Length == 0 || name.Contains(f, StringComparison.OrdinalIgnoreCase)) _zoneFileItems.Add(name);

        if (ZoneFilesListView.ItemsSource != _zoneFileItems) ZoneFilesListView.ItemsSource = _zoneFileItems;

        ZoneFilesListView.SelectedItems.Clear();

        foreach (var name in _zoneFileItems) if (_zoneSelectedNames.Contains(name)) ZoneFilesListView.SelectedItems.Add(name);

        _suppressZoneSelectionSync = false;

        UpdateZoneFilesHint();


    }

    internal void ZoneFilesSearch_TextChanged(object sender, TextChangedEventArgs e)
    {

        RefreshZoneFilesView(ZoneFilesSearchBox?.Text);


    }

    internal void ZoneFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {

        if (_suppressZoneSelectionSync) return;

        foreach (var removed in e.RemovedItems.OfType<string>()) _zoneSelectedNames.Remove(removed);

        foreach (var added in e.AddedItems.OfType<string>()) _zoneSelectedNames.Add(added);

        UpdateZoneFilesHint();


    }

    private void UpdateZoneFilesHint()
    {

        if (ZoneFilesDropHint != null) ZoneFilesDropHint.Visibility = _zoneFileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ZoneFilesHint != null) ZoneFilesHint.Text = _zoneFileAll.Count == 0 ? "未读取到桌面图标，也可直接拖放到上方区域。" : $"{_zoneFileAll.Count}项 · 已选 {_zoneSelectedNames.Count}项";


    }

    /// <summary>名称匹配：容忍是否带扩展名。</summary>
    private static bool NameMatches(string? a, string? b)
    {
        var x = a?.Trim();

        var y = b?.Trim();

        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return false;

        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase)) return true;

        var xs = System.IO.Path.GetFileNameWithoutExtension(x);

        var ys = System.IO.Path.GetFileNameWithoutExtension(y);

        return string.Equals(xs, y, StringComparison.OrdinalIgnoreCase) || string.Equals(x, ys, StringComparison.OrdinalIgnoreCase) || string.Equals(xs, ys, StringComparison.OrdinalIgnoreCase);


    }

    internal void ZoneFiles_DragOver(object sender, DragEventArgs e)
    {

        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        else e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

        if (e.DragUIOverride != null)
        {

            e.DragUIOverride.Caption = "加入本分区";

            e.DragUIOverride.IsCaptionVisible = true;

            e.DragUIOverride.IsGlyphVisible = true;


        }

        e.Handled = true;


    }

    internal async void ZoneFiles_Drop(object sender, DragEventArgs e)
    {

        try
        {

            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {

                var items = await e.DataView.GetStorageItemsAsync();

                var names = items.Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

                AddNamesToSelection(names);


            }


        }

        catch
        {

            /* ignore */
        }


    }

    /// <summary>把拖入/选择的文件名加入“指定文件”并勾选；按名称（容忍扩展名）去重，已存在则直接勾选。</summary>
    private void AddNamesToSelection(IEnumerable<string> names)
    {
        foreach (var raw in names)
        {

            var name = raw?.Trim();

            if (string.IsNullOrEmpty(name)) continue;

            var canonical = _zoneFileAll.FirstOrDefault(x => NameMatches(x, name));

            if (canonical == null)
            {

                _zoneFileAll.Add(name);

                canonical = name;


            }

            _zoneSelectedNames.Add(canonical);


        }

        RefreshZoneFilesView(ZoneFilesSearchBox?.Text);


    }

    internal void Zone_Delete_Click(object sender, RoutedEventArgs e)
    {

        if (ZonesListView.SelectedItem is not DesktopZone zone) return;

        _desktopZones.Remove(zone);

        SaveDesktopZones();

        HideZoneEditPanel();


    }

    private void Zone_MoveUp_Click(object sender, RoutedEventArgs e)
    {

        if (ZonesListView.SelectedItem is not DesktopZone zone) return;

        var index = _desktopZones.IndexOf(zone);

        if (index <= 0) return;

        _desktopZones.Move(index, index - 1);

        ZonesListView.SelectedItem = zone;

        SaveDesktopZones();


    }

    private void Zone_MoveDown_Click(object sender, RoutedEventArgs e)
    {

        if (ZonesListView.SelectedItem is not DesktopZone zone) return;

        var index = _desktopZones.IndexOf(zone);

        if (index < 0 || index >= _desktopZones.Count - 1) return;

        _desktopZones.Move(index, index + 1);

        ZonesListView.SelectedItem = zone;

        SaveDesktopZones();


    }

    internal void Zone_Confirm_Click(object sender, RoutedEventArgs e)
    {

        var name = ZoneNameBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(name))
        {

            ZoneNameBox.Focus(FocusState.Programmatic);

            return;


        }

        var keywords = ParseKeywords(ZoneKeywordsBox.Text);

        var items = _zoneSelectedNames.ToList();

        var zone = new DesktopZone
        {

            Name = name,
            Keywords = keywords,
            Items = items
        }

;

        if (_zoneEditingIndex >= 0 && _zoneEditingIndex < _desktopZones.Count) _desktopZones[_zoneEditingIndex] = zone;

        else _desktopZones.Add(zone);

        SaveDesktopZones();

        HideZoneEditPanel();


    }

    internal void Zone_Cancel_Click(object sender, RoutedEventArgs e) => HideZoneEditPanel();

    /// <summary>把当前分区列表导出为独立的预设 JSON 文件，便于分享 / 备份。</summary>
    internal async void Zone_PresetExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("分区预设文件", new[] {

 ".json"
}

);

        picker.SuggestedFileName = "WinTools-DesktopZones.json";

        var file = await picker.PickSaveFileAsync().AsTask();

        if (file == null) return;

        // 预设文件只关心分区结构：name / keywords / items。其他字段（容器位置 / schemaVersion）
        // 也不导出，避免不同显示器/不同窗口位置硬塞给用户。直接序列化 _desktopZones 列表。
        var snapshot = _desktopZones.Select(CloneZone).ToList();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {

            WriteIndented = true
        }

);

        await File.WriteAllTextAsync(file.Path, json);

        if (MainNav.XamlRoot != null)
        {

            await new ContentDialog
            {

                Title = "已导出",
                Content = $"分区预设已保存到：\n{file.Path}",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

.ShowAsync();


        }


    }

    /// <summary>从外部预设 JSON 文件加载分区列表，替换当前分区。保留每个新分区的 keywords 和 items；用户已有的 items 与新分区按 name 匹配，名称相同的分区会保留用户拖动换位的 Items（keywords 仍按新预设走）。</summary>
    internal async void Zone_PresetImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".json");

        picker.FileTypeFilter.Add("*");

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        List<DesktopZone>? imported;

        try
        {

            var json = await File.ReadAllTextAsync(file.Path);

            imported = JsonSerializer.Deserialize<List<DesktopZone>>(json);


        }

        catch (Exception ex)
        {

            if (MainNav.XamlRoot != null)
            {

                await new ContentDialog
                {

                    Title = "导入失败",
                    Content = $"预设文件解析失败：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

.ShowAsync();


            }

            return;


        }

        if (imported == null || imported.Count == 0)
        {

            if (MainNav.XamlRoot != null)
            {

                await new ContentDialog
                {

                    Title = "预设为空",
                    Content = "所选文件不包含任何分区。",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

.ShowAsync();


            }

            return;


        }

        if (MainNav.XamlRoot != null)
        {

            var confirm = new ContentDialog
            {

                Title = "从预设文件恢复？",
                Content = $"将用预设文件里的 {imported.Count} 个分区替换当前分区。\n\n" + "已分配到当前分区的桌面图标会按新分区结构重新分类（按文件名字段匹配）。",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainNav.XamlRoot,
            }

;

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;


        }

        // 把所有已分配的 Items 收集起来：按分区 name → items 列表。        // 然后用 DesktopZoneMatcher 按新分区规则重新分配。
        var allItems = _desktopZones.SelectMany(zone => (zone.Items ?? new List<string>()).Select(name => (Zone: zone.Name, Name: name))).ToList();
        _desktopZones.Clear();

        foreach (var zone in imported) _desktopZones.Add(zone);

        // 重新分配 items：让每个旧 item 找它在哪个新分区
        foreach (var (oldZone, name) in allItems)
        {
            // 用旧分区名作为 path hint + 名字作为 hint，先看新分区里有没有 name 完全匹配的
            var matchIdx = DesktopZoneMatcher.Match(name, name, false, _desktopZones.Select(z => z).ToList());
            if (matchIdx >= 0 && matchIdx < _desktopZones.Count)
            {

                if (_desktopZones[matchIdx].Items == null) _desktopZones[matchIdx].Items = new List<string>();

                if (!_desktopZones[matchIdx].Items.Contains(name, StringComparer.OrdinalIgnoreCase)) _desktopZones[matchIdx].Items.Add(name);


            }


        }

        HideZoneEditPanel();
        SaveDesktopZones();


    }

    private static List<string> ParseKeywords(string? text)
    {

        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return text.Split(new[] {

 '\r', '\n', ',', '，', ' ', '\t', '、'
}

, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();


    }

    #endregion
    #region 输入法切换
    private void LoadPerAppImeFromConfig()
    {

        _isLoadingDragStashSettings = true;

        PerAppImeToggle.IsOn = _config.EnablePerAppIme;

        _isLoadingDragStashSettings = false;

        LoadImeRules();


    }

    internal void PerAppImeToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        _config.EnablePerAppIme = PerAppImeToggle.IsOn;

        (App.Current as App)?.SetPerAppImeEnabled(_config.EnablePerAppIme);

        UpdateNavStatusIndicators();


    }

    private void LoadImeRules()
    {

        _imeRulesListInitialized = true;

        _imeRules.Clear();

        _config.PerAppImeRules ??= new List<ImeRuleEntry>();

        foreach (var rule in _config.PerAppImeRules)
        {

            _imeRules.Add(new ImeRuleEntry
            {

                ProcessName = rule.ProcessName,
                DisplayName = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.ProcessName : rule.DisplayName,
                UseChinese = rule.UseChinese
            }

);


        }

        ImeRulesListView.ItemsSource = _imeRules;

        UpdateImeCount();


    }

    private void UpdateImeCount()
    {

        ImeCountText.Text = _imeRules.Count > 0 ? $"{_imeRules.Count}项" : "暂无规则";

        ImeEmptyHint.Visibility = _imeRules.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    private void SaveImeRules()
    {

        _config.PerAppImeRules = _imeRules.Select(r => new ImeRuleEntry
        {

            ProcessName = r.ProcessName,
            DisplayName = r.DisplayName,
            UseChinese = r.UseChinese
        }

).ToList();

        ConfigService.Save(_config);

        (App.Current as App)?.UpdatePerAppImeRules(_config.PerAppImeRules);

        UpdateImeCount();


    }

    private void ShowImeEditPanel()
    {

        ImeEditPanel.Visibility = Visibility.Visible;

        ImeEditColumn.Width = new GridLength(316);

        RefreshRunningAppsComboBox();


    }

    private void HideImeEditPanel()
    {

        ImeEditPanel.Visibility = Visibility.Collapsed;

        ImeEditColumn.Width = new GridLength(0);


    }

    private void RefreshRunningAppsComboBox()
    {

        var apps = RunningProcessHelper.GetRunningApps();

        ImeAppComboBox.ItemsSource = apps;

        if (apps.Count > 0) ImeAppComboBox.SelectedIndex = 0;


    }

    internal void Ime_Add_Click(object sender, RoutedEventArgs e) => ShowImeEditPanel();

    internal void Ime_RefreshApps_Click(object sender, RoutedEventArgs e) => RefreshRunningAppsComboBox();

    internal void Ime_RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {

        var rules = _imeRules.ToList();

        ConfigService.MergeRecommendedImeRules(rules);

        _imeRules.Clear();

        foreach (var rule in rules) _imeRules.Add(rule);

        _config.ImeCategoryDefaultsInitialized = true;

        SaveImeRules();


    }

    internal void Ime_Delete_Click(object sender, RoutedEventArgs e)
    {

        if (ImeRulesListView.SelectedItem is not ImeRuleEntry entry) return;

        _imeRules.Remove(entry);

        SaveImeRules();

        HideImeEditPanel();


    }

    internal void ImeModeChip_Click(object sender, RoutedEventArgs e)
    {

        if (sender is not Button
            {

                Tag: ImeRuleEntry entry
            }

) return;

        var index = _imeRules.IndexOf(entry);

        if (index < 0) return;

        _imeRules[index] = new ImeRuleEntry
        {

            ProcessName = entry.ProcessName,
            DisplayName = entry.DisplayName,
            UseChinese = !entry.UseChinese
        };

        SaveImeRules();


    }

    internal void Ime_Confirm_Click(object sender, RoutedEventArgs e)
    {

        if (ImeAppComboBox.SelectedItem is not RunningAppInfo app) return;

        var useChinese = ImeModeComboBox.SelectedIndex == 0;

        var existing = _imeRules.FirstOrDefault(r => string.Equals(r.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {

            existing.UseChinese = useChinese;

            existing.DisplayName = app.DisplayName;

            var index = _imeRules.IndexOf(existing);

            _imeRules[index] = new ImeRuleEntry
            {

                ProcessName = existing.ProcessName,
                DisplayName = existing.DisplayName,
                UseChinese = existing.UseChinese
            }

;


        }

        else
        {

            _imeRules.Add(new ImeRuleEntry
            {

                ProcessName = app.ProcessName,
                DisplayName = app.DisplayName,
                UseChinese = useChinese
            }

);


        }

        ImeRulesListView.ItemsSource = null;

        ImeRulesListView.ItemsSource = _imeRules;

        SaveImeRules();

        HideImeEditPanel();


    }

    internal void Ime_Cancel_Click(object sender, RoutedEventArgs e) => HideImeEditPanel();

    #endregion
}

