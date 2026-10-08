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

using Microsoft.UI.Xaml.Controls.Primitives;


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

    /// <summary>分辨率 / 色深 / 显示器拔插后广播到所有顶层窗口。</summary>
    private const uint WM_DISPLAYCHANGE = 0x007E;

    /// <summary>缩放比例（DPI）变化。manifest 声明了 PerMonitorV2，本窗口会收到。</summary>
    private const uint WM_DPICHANGED = 0x02E0;

    /// <summary>系统参数变化；只关心 SPI_SETWORKAREA（任务栏尺寸 / 位置改变）。</summary>
    private const uint WM_SETTINGCHANGE = 0x001A;

    private const int SPI_SETWORKAREA = 0x002F;

    /// <summary>系统询问能否结束会话（注销 / 关机）。此时会话仍可能被取消。</summary>
    private const uint WM_QUERYENDSESSION = 0x0011;

    /// <summary>会话结束的最终通知：wParam 非 0 表示真的要结束，必须尽快自行退出。</summary>
    private const uint WM_ENDSESSION = 0x0016;


    private const int GWLP_WNDPROC = -4;


    /// <summary>桌面卡片悬浮窗全局快捷键 ID。</summary>
    private const int HotkeyIdDesktopCard = 10;
    /// <summary>桌面卡片快捷键在 <see cref = "Config.Hotkeys"/> 里的键名。</summary>
    private const string DesktopCardHotkeyKey = "DesktopCard";
    private const string DesktopCardHotkeyDefault = "Ctrl+Alt+Shift+F12";

    /// <summary>悬浮搜索全局快捷键 ID。</summary>
    private const int HotkeyIdSpotlight = 11;
    /// <summary>悬浮搜索快捷键在 <see cref = "Config.Hotkeys"/> 里的键名。</summary>
    private const string SpotlightHotkeyKey = "Spotlight";
    private const string SpotlightHotkeyDefault = "Alt+Space";

    /// <summary>手动声明 SetWindowLongPtrW（CsWin32 不生成此宏对应函数）。</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr NativeSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    /// <summary>侧边栏导航项标签（与 XAML 中 NavigationViewItem.Tag 一致）。</summary>
    private static class NavTags
    {
        public const string ThreeFingerDrag = "ThreeFingerDrag";

        public const string DragStash = "DragStash";

        public const string ProgramAssociation = "ProgramAssociation";

        public const string PerAppIme = "PerAppIme";

        public const string DesktopOrganize = "DesktopOrganize";

        public const string DesktopClick = "DesktopClick";

        public const string SpacePreview = "SpacePreview";

        public const string Spotlight = "Spotlight";

        public const string VoiceBall = "VoiceBall";
        public const string TaskbarInfo = "TaskbarInfo";

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

    private DesktopCardManager? _desktopCardManager;

    /// <summary>显示配置变化后的重排防抖计时器，首次用到时才创建。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _displayChangeTimer;

    // 程序关联内嵌编辑
    private readonly ObservableCollection<ConfigEntry> _assocEntries = new();
    private int _assocEditingIndex = -1;

    // -1 = 新建 // 输入法切换规则
    private readonly ObservableCollection<ImeRuleEntry> _imeRules = new();
    private bool _assocListInitialized;

    private bool _imeRulesListInitialized;

    // 桌面分区
    private readonly ObservableCollection<DesktopZone> _desktopZones = new();

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
        InitializeQuotaConnections();
        HotkeyRecorder.Attach(HotkeySpotlightBox, allowModifierOnly: false);
        HotkeyRecorder.Attach(HotkeyDesktopCardBox, allowModifierOnly: false);
        HotkeyRecorder.Attach(HotkeyVoiceBallBox, allowModifierOnly: true);

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

        MainNav.MenuItems.Add(NavItemSpacePreview);

        MainNav.MenuItems.Add(NavItemSpotlight);

        MainNav.MenuItems.Add(NavItemVoiceBall);
        MainNav.MenuItems.Add(NavItemTaskbarInfo);

        MainNav.MenuItems.Add(NavItemDragStash);

        MainNav.MenuItems.Add(NavItemPerAppIme);

        MainNav.MenuItems.Add(NavItemProgramAssoc);

        MainNav.MenuItems.Add(NavItemThreeFingerDrag);


    }

    /// <summary>建立导航标签与内容面板的映射，统一切换可见页面。</summary>
    private void InitContentPanels()
    {
        _contentPanels[NavTags.ThreeFingerDrag] = ContentThreeFingerDrag;
        _contentPanels[NavTags.DragStash] = ContentDragStash;
        _contentPanels[NavTags.ProgramAssociation] = ContentProgramAssociation;
        _contentPanels[NavTags.PerAppIme] = ContentPerAppIme;
        _contentPanels[NavTags.DesktopOrganize] = ContentDesktopOrganize;
        _contentPanels[NavTags.DesktopClick] = ContentDesktopClick;
        _contentPanels[NavTags.SpacePreview] = ContentSpacePreview;
        _contentPanels[NavTags.Spotlight] = ContentSpotlight;
        _contentPanels[NavTags.VoiceBall] = ContentVoiceBall;
        _contentPanels[NavTags.TaskbarInfo] = FeaturePages.GetControl<Grid>("ContentTaskbarInfo");
        _contentPanels[NavTags.AppSettings] = ContentAppSettings;


    }

    /// <summary>配置与侧栏、内容画布连成一体的深色自定义标题栏。</summary>
    private void InitTitleBar()
    {
        WindowHelper.ConfigureTransparentTitleBar(this, AppTitleBar);

        // syncHeight: false —— 标题栏高度固定 40dip（对齐资源管理器标签栏），不跟随系统的
        // 32dip 标准高度；这里只同步左右两侧的系统按钮占位。
        WindowHelper.HookTitleBarPadding(this, AppTitleBar, LeftPaddingColumn, RightPaddingColumn);

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
                // 进程正在退出（托盘「退出」/ 注销 / 关机）时放行，不再缩回托盘。
                if (App.IsShuttingDown) return;

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

            case NavTags.SpacePreview:
                LoadSpacePreviewFromConfig();

                break;

            case NavTags.Spotlight:
                LoadSpotlightFromConfig();

                break;

            case NavTags.VoiceBall:
                LoadVoiceBallFromConfig();

                break;

            case NavTags.AppSettings:
                LoadAppSettingsFromConfig();

                break;

            case NavTags.TaskbarInfo:
                LoadTaskbarInfoSettings();
                break;


        }

        _isLoadingDragStashSettings = false;

        UpdateNavStatusIndicators();


    }

    /// <summary>把主窗口的导航切到"应用设置"内嵌页。供托盘菜单、全局快捷键和     /// <see cref = "App.OpenSettingsWindow"/> 调用。     /// <para>设置 UI 已经统一在主窗口 <c>MainNav.NavItemAppSettings</c> 下面，不再使用独立的设置窗口（2026-08-31 删除了上一版     /// <c>SettingsWindow</c> 方案及 <c>_diff_backup\Settings\</c> 备份）。</para>     </summary>
    internal void NavigateToSettings()
    {
        // 只切页面。窗口前置由调用方 App.OpenSettingsWindow 负责；以前这里又回调
        // App.OpenSettingsWindow，两边互相调用，托盘菜单点「设置」会无限递归直到栈溢出崩溃。
        MainNav.SelectedItem = NavItemAppSettings;
    }

    /// <summary>切到桌面分区页。窗口前置由调用方 <see cref="App.OpenDesktopZoneSettings"/> 负责。</summary>
    internal void NavigateToDesktopOrganize() => MainNav.SelectedItem = NavItemDesktopOrganize;

    /// <summary>
    /// 启动时把 config.json 同步到所有控件，避免关闭时把 XAML 默认值写回配置。
    /// 试过给页面加 x:Load="False" 延迟实例化，再让这里只同步已实例化的页面：
    /// 实测启动时间（主窗口可见 760~870ms、九张卡片就绪 1.7~1.8s）和之前完全一致，
    /// 瓶颈在 WinUI 框架自身初始化，不在页面 XAML 构建，所以按实测结果回退了。
    /// </summary>
    private void SyncAllControlsFromConfig()
    {
        LoadTaskbarInfoSettings();
        if (DragStashToggle != null) DragStashToggle.IsOn = _config.EnableDragStash;
        if (ThreeFingerDragToggle != null) ThreeFingerDragToggle.IsOn = _config.EnableThreeFingerDrag;
        if (ProgramAssocToggle != null) ProgramAssocToggle.IsOn = _config.EnableProgramAssoc;
        if (PerAppImeToggle != null) PerAppImeToggle.IsOn = _config.EnablePerAppIme;
        if (DesktopClickToggle != null) DesktopClickToggle.IsOn = _config.EnableDesktopClickToShow;
        if (ExplorerPreviewToggle != null) ExplorerPreviewToggle.IsOn = _config.EnableExplorerPreview;
        if (CardPreviewToggle != null) CardPreviewToggle.IsOn = _config.EnableCardPreview;
        if (SpotlightToggle != null) SpotlightToggle.IsOn = _config.EnableSpotlight;
        if (HotkeySpotlightBox != null) HotkeySpotlightBox.Text = GetSpotlightHotkey();
        if (VoiceBallToggle != null) VoiceBallToggle.IsOn = _config.EnableVoiceBall;
        if (HotkeyVoiceBallBox != null) HotkeyVoiceBallBox.Text = GetVoiceBallHotkey();
        if (AutoStartToggle != null) AutoStartToggle.IsOn = AutostartService.IsEnabled();
        LoadUpdateSettings();
        SyncThemeRadioFromConfig();
    }

    /// <summary>将当前 UI 状态同步到配置并写入 config.json（关闭窗口或退出时调用）。</summary>
    internal void FlushAndSaveConfig()
    {
        if (_isLoadingDragStashSettings) return;

        // 开关与滑块在操作时已写入 _config；此处仅补充可能未点「保存」的文本框内容
        _config.Hotkeys ??= new Dictionary<string, string>();
        if (HotkeyDesktopCardBox != null) _config.Hotkeys[DesktopCardHotkeyKey] = HotkeyDesktopCardBox.Text?.Trim() ?? "";
        if (HotkeySpotlightBox != null) _config.Hotkeys[SpotlightHotkeyKey] = HotkeySpotlightBox.Text?.Trim() ?? "";
        if (HotkeyVoiceBallBox?.Text?.Trim() is { } voiceHotkey && HotkeyHelper.ParseForSend(voiceHotkey) != null)
            _config.Hotkeys[VoiceBallHotkeyKey] = voiceHotkey;

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
                UseChinese = r.UseChinese,
                ExePath = r.ExePath
            }

).ToList();


        }

        ConfigService.Save(_config);


    }

    /// <summary>显示指定标签的内容面板，隐藏已经创建的其他页面。</summary>
    private void SwitchContentPanel(string activeTag)
    {
        foreach (var (tag, panel) in _contentPanels)
            panel.Visibility = tag == activeTag ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion
    #region 设置加载与外观
    private void LoadDragStashFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (DragStashToggle != null) DragStashToggle.IsOn = _config.EnableDragStash;

        _isLoadingDragStashSettings = false;


    }

    private void LoadThreeFingerDragFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (ThreeFingerDragToggle != null) ThreeFingerDragToggle.IsOn = _config.EnableThreeFingerDrag;

        if (ThreeFingerCalibrationStatusText != null) ThreeFingerCalibrationStatusText.Text = string.IsNullOrWhiteSpace(_config.ThreeFingerCalibrationUtc) ? "尚未校准，正在使用默认速度" : "已根据此设备完成校准";

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
            Content = "将依次校准慢速、正常和快速三档。每一档请用一根手指从触摸板最左侧滑到最右侧后抬起，完成后会自动进入下一档，无需按下触摸板。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainNav.XamlRoot
        }

;

        if (await instructions.ShowAsync() != ContentDialogResult.Primary) return;

        ThreeFingerCalibrateButton.IsEnabled = false;

        ThreeFingerCalibrationProgress.Visibility = Visibility.Visible;

        ThreeFingerCalibrationProgress.Value = 0;

        ThreeFingerCalibrationStatusText.Text = "正在校准，请依次完成慢速、正常和快速三次滑动…";

        var result = await RunGuidedThreeFingerCalibrationAsync();

        ThreeFingerCalibrationProgress.Visibility = Visibility.Collapsed;

        ThreeFingerCalibrateButton.IsEnabled = true;

        if (result == null)
        {

            ThreeFingerCalibrationStatusText.Text = "已取消校准，速度设置未更改";

            return;


        }

        if (!result.Value.Success || result.Value.FitQuality < 0.25)
        {

            ThreeFingerCalibrationStatusText.Text = "有效滑动数据不足，速度设置未更改";

            var failed = new ContentDialog
            {

                Title = "校准未完成",
                Content = "未采集到足够的慢速和快速滑动数据。请重试，并避免光标碰到屏幕边缘。",
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
            "已根据此设备完成校准";

        var completed = new ContentDialog
        {

            Title = "校准完成",
            Content = "已根据此设备调整三指拖拽速度，更改已生效。",
            CloseButtonText = "确定",
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



    }

    private void UpdateLocalBrush(string key, Color color)
    {

        if (ShellRoot.Resources[key] is SolidColorBrush brush) brush.Color = color;


    }

    #region 侧边栏状态

    /// <summary>同步功能菜单项右侧状态圆点：强调色表示开启，弱化色表示关闭。</summary>
    private void UpdateNavStatusIndicators()
    {
        var onBrush = Application.Current.Resources["AccentFillColorDefaultBrush"] as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));

        var offBrush = Application.Current.Resources["TextFillColorDisabledBrush"] as SolidColorBrush ?? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

        SetNavDot(NavDotThreeFingerDrag, _config.EnableThreeFingerDrag, onBrush, offBrush);

        SetNavDot(NavDotDragStash, _config.EnableDragStash, onBrush, offBrush);

        SetNavDot(NavDotProgramAssoc, _config.EnableProgramAssoc, onBrush, offBrush);

        SetNavDot(NavDotPerAppIme, _config.EnablePerAppIme, onBrush, offBrush);

        SetNavDot(NavDotDesktopOrganize, _config.EnableDesktopCard, onBrush, offBrush);

        SetNavDot(NavDotDesktopClick, _config.EnableDesktopClickToShow, onBrush, offBrush);

        SetNavDot(NavDotSpacePreview, _config.EnableExplorerPreview || _config.EnableCardPreview, onBrush, offBrush);

        SetNavDot(NavDotSpotlight, _config.EnableSpotlight, onBrush, offBrush);

        SetNavDot(NavDotVoiceBall, _config.EnableVoiceBall, onBrush, offBrush);
        SetNavDot(NavDotTaskbarInfo, _config.EnableTaskbarInfo, onBrush, offBrush);
        SyncTaskbarInfoEntries();


    }

    private static void SetNavDot(Microsoft.UI.Xaml.Shapes.Ellipse? dot, bool enabled, SolidColorBrush onBrush, SolidColorBrush offBrush)
    {

        if (dot == null) return;

        dot.Fill = enabled ? onBrush : offBrush;


    }

    #endregion
}
