using System;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace WinTools.Services;

/// <summary>
/// 设置中心服务：单例持有 <see cref="Config"/>，监听属性变化后防抖写盘。
/// 新的设置 UI（SettingsWindow）应通过 <see cref="Instance"/>.<see cref="Current"/> 读写配置；
/// 旧代码仍可继续使用 <c>ConfigService.Load/Save</c>，二者写入同一份 config.json。
/// </summary>
public sealed class SettingsService
{
    private static readonly object _initLock = new();
    private static SettingsService? _instance;

    private readonly DispatcherQueueTimer _saveTimer;
    // 自上次 FlushNow 以来是否有未写盘的变更。OnCurrentPropertyChanged 把它置 true，
    // 防抖 Tick 回调里置回 false；FlushNow 在它为 false 时直接返回，避免无变更写盘。
    private bool _pendingSave;

    private SettingsService(Config loaded, DispatcherQueue dispatcherQueue)
    {
        Current = loaded;
        _saveTimer = dispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        // 直接调 FlushNow：由它自己检查并清 _pendingSave。
        // 曾经在这里先把 _pendingSave 置 false 再调 FlushNow，结果 FlushNow 一进门就
        // 因为"没有待保存变更"返回——防抖保存**一次都没真正写过盘**，配置只能靠退出
        // 路径上的 ConfigService.Save 兜底，进程被强杀就全丢了。
        _saveTimer.Tick += (_, _) => FlushNow();
        Current.PropertyChanged += OnCurrentPropertyChanged;
    }

    /// <summary>当前生效的 <see cref="Config"/> 实例，所有 UI 都通过它读写设置。</summary>
    public Config Current { get; }

    /// <summary>获取或初始化单例。首次调用时从 config.json 加载。</summary>
    public static SettingsService Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_initLock)
            {
                if (_instance != null) return _instance;
                var dispatcher = DispatcherQueue.GetForCurrentThread()
                    ?? throw new InvalidOperationException("SettingsService 必须在 UI 线程初始化。");
                var loaded = ConfigService.Load();
                _instance = new SettingsService(loaded, dispatcher);
            }
            return _instance;
        }
    }

    /// <summary>在 App 启动时一次性初始化，避免首屏自动绑定晚于服务创建。</summary>
    public static void Initialize() => _ = Instance;

    /// <summary>立即把当前配置写盘（一般用于退出或显式保存动作）。</summary>
    /// <remarks>无未保存变更时直接返回，不做 I/O。</remarks>
    public void FlushNow()
    {
        if (!_pendingSave) return;
        _pendingSave = false;
        try
        {
            ConfigService.Save(Current);
        }
        catch
        {
            // 静默失败，UI 层如有需要可订阅 SavingFailed。
        }
    }

    /// <summary>把配置还原为默认值。不会自动保存，调用方在适当时机再 <see cref="FlushNow"/>。</summary>
    public void ResetToDefaults()
    {
        var defaults = ConfigService.GetDefault();
        Current.Entries = defaults.Entries;
        Current.Hotkeys = defaults.Hotkeys;
        Current.EnableDragStash = defaults.EnableDragStash;
        Current.EnableProgramAssoc = defaults.EnableProgramAssoc;
        Current.EnableThreeFingerDrag = defaults.EnableThreeFingerDrag;
        Current.ThreeFingerLowSpeedGain = defaults.ThreeFingerLowSpeedGain;
        Current.ThreeFingerHighSpeedGain = defaults.ThreeFingerHighSpeedGain;
        Current.ThreeFingerAccelerationStart = defaults.ThreeFingerAccelerationStart;
        Current.ThreeFingerAccelerationEnd = defaults.ThreeFingerAccelerationEnd;
        Current.ThreeFingerCalibrationUtc = defaults.ThreeFingerCalibrationUtc;
        Current.StashWindowWidth = defaults.StashWindowWidth;
        Current.StashWindowHeight = defaults.StashWindowHeight;
        Current.AutoStart = defaults.AutoStart;
        Current.AppTheme = defaults.AppTheme;
        Current.AppUiStyle = defaults.AppUiStyle;
        Current.StashOffsetX = defaults.StashOffsetX;
        Current.StashOffsetY = defaults.StashOffsetY;
        Current.ProgramOffsetX = defaults.ProgramOffsetX;
        Current.ProgramOffsetY = defaults.ProgramOffsetY;
        Current.WindowGap = defaults.WindowGap;
        Current.EnablePerAppIme = defaults.EnablePerAppIme;
        Current.EnableDesktopClickToShow = defaults.EnableDesktopClickToShow;
        Current.DesktopZones = defaults.DesktopZones;
        Current.DesktopZoneSchemaVersion = defaults.DesktopZoneSchemaVersion;
        Current.EnableDesktopCard = defaults.EnableDesktopCard;
        Current.DesktopCardGap = defaults.DesktopCardGap;
        Current.DesktopCardMargin = defaults.DesktopCardMargin;
        Current.DesktopCardMaxColumns = defaults.DesktopCardMaxColumns;
        Current.PerAppImeRules = defaults.PerAppImeRules;
        Current.ImeCategoryDefaultsInitialized = defaults.ImeCategoryDefaultsInitialized;
    }

    private void OnCurrentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 防抖：400ms 内多次变化合并为一次写盘。
        _pendingSave = true;
        _saveTimer.Start();
    }
}
