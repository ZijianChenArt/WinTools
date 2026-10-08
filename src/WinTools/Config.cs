using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Windows.Storage;

namespace WinTools;

internal static class ThreeFingerCalibrationDefaults
{
    public const double LowSpeedGain = 0.25;
    public const double HighSpeedGain = 0.82;
    public const double AccelerationStart = 6.0;
    public const double AccelerationEnd = 50.0;
    public const double MaximumAdjustmentRatio = 0.20;
}

/// <summary>单条程序关联配置：扩展名 + 显示名称 + 程序路径。</summary>
public class ConfigEntry
{
    [JsonPropertyName("ext")]
    public string Ext { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

/// <summary>单条 per-app 输入法规则：进程名 + 中/英文模式。</summary>
public class ImeRuleEntry
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("useChinese")]
    public bool UseChinese { get; set; }

    /// <summary>程序完整路径，只用来显示图标；应用没在运行时靠它取图标。可为空。</summary>
    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonIgnore]
    public string ModeLabel => UseChinese ? "中文" : "英文";
}

/// <summary>桌面分区：分区名称 + 匹配关键词列表。</summary>
public class DesktopZone
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// 匹配关键词：以「.」开头视为按扩展名匹配（如 .docx）；
    /// 「文件夹」/「目录」匹配所有文件夹；「steam://」匹配 Steam 游戏链接；
    /// 其余视为对图标名称的包含匹配。
    /// </summary>
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new();

    /// <summary>明确指定归入本分区的桌面图标名称（精确匹配，优先于关键词）。</summary>
    [JsonPropertyName("items")]
    public List<string> Items { get; set; } = new();

    /// <summary>分区容器窗口几何（DIP）。宽/高 &lt;= 0 表示尚未设置，由程序自动摆放。</summary>
    [JsonPropertyName("containerX")]
    public int ContainerX { get; set; }

    [JsonPropertyName("containerY")]
    public int ContainerY { get; set; }

    [JsonPropertyName("containerWidth")]
    public int ContainerWidth { get; set; }

    [JsonPropertyName("containerHeight")]
    public int ContainerHeight { get; set; }

    /// <summary>规则摘要（用于界面显示）。</summary>
    [JsonIgnore]
    public string KeywordsSummary
    {
        get
        {
            // 兜底分区：没有具体关键词、名字也叫"其他/杂项"这类时才显示"其余全部"。
            // v6 起"其他"分区承接了原"系统工具类"的关键词，这里要展示具体关键词而不是"其余全部"。
            var hasKeywords = Keywords is { Count: > 0 };
            if (!hasKeywords &&
                (string.Equals(Name, "其他", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Name, "常用程序与杂项", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Name, "常用与杂项", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Name, "杂项", StringComparison.OrdinalIgnoreCase)))
                return "其余全部";

            var parts = new List<string>();
            if (Items is { Count: > 0 })
                parts.Add($"指定 {Items.Count} 个文件");
            if (hasKeywords)
                parts.Add(string.Join("、", Keywords));
            return parts.Count > 0 ? string.Join("；", parts) : "（未设置规则）";
        }
    }

    /// <summary>默认桌面分区（首次使用或未配置时）。</summary>
    /// <remarks>
    /// 历史变更：
    /// v5: 10 个分区（含独立的"系统工具类"和"其他"两个分区）。
    /// v6: "系统工具类"并入"其他"（"系统工具类"分类下的内容实际为杂项，不配单独成类）；
    ///     "功能与娱乐" 改名为 "影音与娱乐"（关键词是浏览器/网盘/音乐/媒体文件，影音更精确）；
    ///     "网络" 改名为 "网络与远程"（主要内容是 VPN 和远程桌面）。
    /// v8: "开发与效率"改名为"开发与 AI"，并补齐常见 AI 软件关键词。
    /// 9 个分区。
    /// </remarks>
    public static List<DesktopZone> CreateDefaults() => new()
    {
        new DesktopZone { Name = "文件夹与文件", Keywords = new() { "此电脑", "这台电脑", "我的电脑", "计算机", "回收站", "网络", "控制面板", "This PC", "Computer", "Recycle Bin", "Network", "Control Panel", "文件夹", "DeskBox Files", "编程", "Programming", "Projects", "源码", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md", ".rtf", ".csv", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".svg", ".ico", ".heic", ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".cab" } },
        new DesktopZone { Name = "设计与创作", Keywords = new() { "Adobe", "Premiere", "After Effects", "Photoshop", "Illustrator", "InDesign", "Lightroom", "Media Encoder", "Audition", "Animate", "Acrobat", "DaVinci Resolve", "达芬奇", "剪映", "CapCut", "Final Cut", "VEGAS", "Avid Media Composer", "Nuke", "Fusion", "OBS Studio", "Topaz Video", "Video2X", "Particle Illusion", "格式工厂", "小丸工具箱", "Figma", "CorelDRAW", "Affinity", "Eagle" } },
        new DesktopZone { Name = "三维与引擎", Keywords = new() { "Blender", "铁锅炖启动器", "Cinema 4D", "C4D", "3ds Max", "Maya", "Houdini", "ZBrush", "Substance 3D", "Unreal Engine", "Unity", "Unity Hub", "Rhino", "RizomUV", "KeyShot", "Marvelous Designer", "SketchUp", "TouchDesigner" } },
        new DesktopZone { Name = "办公与沟通", Keywords = new() { "WPS Office", "Microsoft 365", "Microsoft Office", "Word", "Excel", "PowerPoint", "Outlook", "OneNote", "Access", "Publisher", "钉钉", "腾讯会议", "飞书", "微信", "企业微信", "WeChat", "Teams", "Zoom" } },
        new DesktopZone { Name = "开发与 AI", Keywords = new() { "Cursor", "cursor-pool", "MiniMax Code", "Visual Studio", "Visual Studio Installer", "Git Bash", "Git CMD", "Git GUI", "Python", "IDLE", "DeskBox", "Snipaste", "ChatGPT", "OpenAI", "Claude", "Gemini", "Copilot", "Microsoft Copilot", "GitHub Copilot", "Perplexity", "Poe", "DeepSeek", "Kimi", "豆包", "通义", "Qwen", "腾讯元宝", "文心一言", "Ollama", "LM Studio", "AnythingLLM", "Cherry Studio", "Chatbox", "Monica", "Windsurf", "Trae", "CodeBuddy", "ACE Studio" } },
        new DesktopZone { Name = "影音与娱乐", Keywords = new() { "Google Chrome", "Microsoft Edge", "Chrome", "Edge", "夸克", "网易云音乐", "CloudMusic", "Windows Media Player", "百度网盘", "OneDrive", "PikPak", "极空间", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" } },
        new DesktopZone { Name = "网络与远程", Keywords = new() { "星链云", "LetsVPN", "VPN", "ToDesk", "向日葵", "UU远程", "RayLink", "Relink", "Sunshine", "AnyDesk", "TeamViewer", "SDKDNS", "SDK DNS", "DNSPPN", "WinSCP", "root@" } },
        // "其他" 同时承担兜底：原"系统工具类"分区（杂项：火绒、PotPlayer、WinRAR、外设驱动、银行U盾等）
        // 在 v6 起并入此分区，避免出现一个只有 1~2 个图标的小分类。
        new DesktopZone { Name = "其他", Keywords = new() { "火绒", "PICO Connect", "PICO 互联", "uTools", "Logi Options", "罗技", "QMK Toolbox", "MasterHUB", "PotPlayer", "WinRAR", "Razer", "NVIDIA App", "Nsight", "杭州银行", "浙商", "USBKey", "UKey", "证书工具", "配置工具" } },
        // "游戏"分区按用户偏好放在最末位（"其他"在它之前作为倒数第二）。
        // ConfigureDesktopCatchAllAndGameOrder 也会强制把"游戏"挤到最后。
        new DesktopZone { Name = "游戏", Keywords = new() { "Steam", "steam://", "SteamVR", "Ubisoft Connect", "Ubisoft", "育碧", "Epic Games Launcher", "Hades", "哈迪斯", "NBA 2K", "Wallpaper Engine", "壁纸引擎", "地平线", "孩子们", "雷神加速器" } },
    };
}

/// <summary>应用程序完整配置，序列化为 config.json；实现 INotifyPropertyChanged 以便 UI 双向绑定。</summary>
public class Config : ObservableObject
{
    private bool _enableTaskbarInfo = true;
    private string _taskbarInfoAlignment = "left";
    [JsonPropertyName("taskbarInfoAlignment")]
    public string TaskbarInfoAlignment { get => _taskbarInfoAlignment; set => SetProperty(ref _taskbarInfoAlignment, value == "center" ? "center" : "left"); }
    private bool _taskbarInfoActions = true;
    [JsonPropertyName("taskbarInfoActions")]
    public bool TaskbarInfoActions { get => _taskbarInfoActions; set => SetProperty(ref _taskbarInfoActions, value); }
    // 任务栏快捷入口：每个入口单独开关；实际显示还要求对应功能本身已开启（音频设备除外）。
    private bool _taskbarShowLibrary = true, _taskbarShowStash = true, _taskbarShowVoice = true, _taskbarShowAudio = true;
    [JsonPropertyName("taskbarShowLibrary")]
    public bool TaskbarShowLibrary { get => _taskbarShowLibrary; set => SetProperty(ref _taskbarShowLibrary, value); }
    [JsonPropertyName("taskbarShowStash")]
    public bool TaskbarShowStash { get => _taskbarShowStash; set => SetProperty(ref _taskbarShowStash, value); }
    [JsonPropertyName("taskbarShowVoice")]
    public bool TaskbarShowVoice { get => _taskbarShowVoice; set => SetProperty(ref _taskbarShowVoice, value); }
    [JsonPropertyName("taskbarShowAudio")]
    public bool TaskbarShowAudio { get => _taskbarShowAudio; set => SetProperty(ref _taskbarShowAudio, value); }
    [JsonPropertyName("enableTaskbarInfo")]
    public bool EnableTaskbarInfo { get => _enableTaskbarInfo; set => SetProperty(ref _enableTaskbarInfo, value); }




    private List<ConfigEntry> _entries = new();
    [JsonPropertyName("entries")]
    public List<ConfigEntry> Entries { get => _entries; set => SetProperty(ref _entries, value); }

    private Dictionary<string, string> _hotkeys = new();
    [JsonPropertyName("hotkeys")]
    public Dictionary<string, string> Hotkeys { get => _hotkeys; set => SetProperty(ref _hotkeys, value); }

    private bool _enableDragStash;
    [JsonPropertyName("enableDragStash")]
    public bool EnableDragStash { get => _enableDragStash; set => SetProperty(ref _enableDragStash, value); }

    private bool _enableProgramAssoc;
    [JsonPropertyName("enableProgramAssoc")]
    public bool EnableProgramAssoc { get => _enableProgramAssoc; set => SetProperty(ref _enableProgramAssoc, value); }

    private bool _enableThreeFingerDrag;
    [JsonPropertyName("enableThreeFingerDrag")]
    public bool EnableThreeFingerDrag { get => _enableThreeFingerDrag; set => SetProperty(ref _enableThreeFingerDrag, value); }

    private double _threeFingerLowSpeedGain = ThreeFingerCalibrationDefaults.LowSpeedGain;
    [JsonPropertyName("threeFingerLowSpeedGain")]
    public double ThreeFingerLowSpeedGain { get => _threeFingerLowSpeedGain; set => SetProperty(ref _threeFingerLowSpeedGain, value); }

    private double _threeFingerHighSpeedGain = ThreeFingerCalibrationDefaults.HighSpeedGain;
    [JsonPropertyName("threeFingerHighSpeedGain")]
    public double ThreeFingerHighSpeedGain { get => _threeFingerHighSpeedGain; set => SetProperty(ref _threeFingerHighSpeedGain, value); }

    private double _threeFingerAccelerationStart = ThreeFingerCalibrationDefaults.AccelerationStart;
    [JsonPropertyName("threeFingerAccelerationStart")]
    public double ThreeFingerAccelerationStart { get => _threeFingerAccelerationStart; set => SetProperty(ref _threeFingerAccelerationStart, value); }

    private double _threeFingerAccelerationEnd = ThreeFingerCalibrationDefaults.AccelerationEnd;
    [JsonPropertyName("threeFingerAccelerationEnd")]
    public double ThreeFingerAccelerationEnd { get => _threeFingerAccelerationEnd; set => SetProperty(ref _threeFingerAccelerationEnd, value); }

    private string _threeFingerCalibrationUtc = "";
    [JsonPropertyName("threeFingerCalibrationUtc")]
    public string ThreeFingerCalibrationUtc { get => _threeFingerCalibrationUtc; set => SetProperty(ref _threeFingerCalibrationUtc, value); }

    private int _stashWindowWidth;
    [JsonPropertyName("stashWindowWidth")]
    public int StashWindowWidth { get => _stashWindowWidth; set => SetProperty(ref _stashWindowWidth, value); }

    private int _stashWindowHeight;
    [JsonPropertyName("stashWindowHeight")]
    public int StashWindowHeight { get => _stashWindowHeight; set => SetProperty(ref _stashWindowHeight, value); }

    private bool _autoStart;
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get => _autoStart; set => SetProperty(ref _autoStart, value); }

    private string _appTheme = ThemePreference.System;
    [JsonPropertyName("appTheme")]
    public string AppTheme { get => _appTheme; set => SetProperty(ref _appTheme, value); }

    private string _appUiStyle = UiStylePreference.Mica;
    [JsonPropertyName("appUiStyle")]
    public string AppUiStyle { get => _appUiStyle; set => SetProperty(ref _appUiStyle, value); }

    private int _programOffsetX = 150;
    [JsonPropertyName("programOffsetX")]
    public int ProgramOffsetX { get => _programOffsetX; set => SetProperty(ref _programOffsetX, value); }

    private int _programOffsetY = 40;
    [JsonPropertyName("programOffsetY")]
    public int ProgramOffsetY { get => _programOffsetY; set => SetProperty(ref _programOffsetY, value); }

    private int _windowGap;
    [JsonPropertyName("windowGap")]
    public int WindowGap { get => _windowGap; set => SetProperty(ref _windowGap, value); }

    private bool _enablePerAppIme;
    [JsonPropertyName("enablePerAppIme")]
    public bool EnablePerAppIme { get => _enablePerAppIme; set => SetProperty(ref _enablePerAppIme, value); }

    private bool _enableDesktopClickToShow;
    [JsonPropertyName("enableDesktopClickToShow")]
    public bool EnableDesktopClickToShow { get => _enableDesktopClickToShow; set => SetProperty(ref _enableDesktopClickToShow, value); }

    // 空格预览（仿 QuickLook）：资源管理器与桌面分区卡片分别开关，默认都开。
    private bool _enableExplorerPreview = true;
    [JsonPropertyName("enableExplorerPreview")]
    public bool EnableExplorerPreview { get => _enableExplorerPreview; set => SetProperty(ref _enableExplorerPreview, value); }

    private bool _enableCardPreview = true;
    [JsonPropertyName("enableCardPreview")]
    public bool EnableCardPreview { get => _enableCardPreview; set => SetProperty(ref _enableCardPreview, value); }

    // 记住在音频设备弹窗里选过的扬声器 / 麦克风（端点 ID）。系统经常在插拔、睡眠唤醒后把默认设备改掉，
    // 开启锁定后后台会把它切回来；设备不在线时不动。
    private string _audioPreferredOutputId = "";
    [JsonPropertyName("audioPreferredOutputId")]
    public string AudioPreferredOutputId { get => _audioPreferredOutputId; set => SetProperty(ref _audioPreferredOutputId, value ?? ""); }

    private string _audioPreferredInputId = "";
    [JsonPropertyName("audioPreferredInputId")]
    public string AudioPreferredInputId { get => _audioPreferredInputId; set => SetProperty(ref _audioPreferredInputId, value ?? ""); }

    private bool _audioLockDevices = true;
    [JsonPropertyName("audioLockDevices")]
    public bool AudioLockDevices { get => _audioLockDevices; set => SetProperty(ref _audioLockDevices, value); }

    // 默认开启：启动时静默查一次 GitHub Release，只在设置页提示，从不自动安装。
    private bool _enableAutoUpdateCheck = true;
    [JsonPropertyName("enableAutoUpdateCheck")]
    public bool EnableAutoUpdateCheck { get => _enableAutoUpdateCheck; set => SetProperty(ref _enableAutoUpdateCheck, value); }

    // 默认开启：悬浮搜索是纯按需呼出的功能，不注册快捷键就完全用不了，
    // 而它本身不常驻任何 hook / 定时器，默认打开不会带来额外开销。
    private bool _enableSpotlight = true;
    [JsonPropertyName("enableSpotlight")]
    public bool EnableSpotlight { get => _enableSpotlight; set => SetProperty(ref _enableSpotlight, value); }

    // 默认关闭：语音小球要常驻一个全局焦点钩子，并对前台程序做 UIA 查询，只给主动开启的用户用。
    private bool _enableVoiceBall;
    [JsonPropertyName("enableVoiceBall")]
    public bool EnableVoiceBall { get => _enableVoiceBall; set => SetProperty(ref _enableVoiceBall, value); }

    // 用户拖出来的小球位置：小球中心相对输入光标底端的偏移（DIP）。未拖过时用默认的光标正下方。
    private bool _voiceBallCustomOffset;
    [JsonPropertyName("voiceBallCustomOffset")]
    public bool VoiceBallCustomOffset { get => _voiceBallCustomOffset; set => SetProperty(ref _voiceBallCustomOffset, value); }

    private double _voiceBallOffsetX;
    [JsonPropertyName("voiceBallOffsetX")]
    public double VoiceBallOffsetX { get => _voiceBallOffsetX; set => SetProperty(ref _voiceBallOffsetX, value); }

    private double _voiceBallOffsetY;
    [JsonPropertyName("voiceBallOffsetY")]
    public double VoiceBallOffsetY { get => _voiceBallOffsetY; set => SetProperty(ref _voiceBallOffsetY, value); }

    // 字段缺失时（首次启动、config.json 被外部工具简化、跨版本迁移）回退到默认 7 个分区。
    // 显式写 "desktopZones": [] 仍会被反序列化为空 list（尊重用户清空操作）。
    private List<DesktopZone> _desktopZones = DesktopZone.CreateDefaults();
    [JsonPropertyName("desktopZones")]
    public List<DesktopZone> DesktopZones { get => _desktopZones; set => SetProperty(ref _desktopZones, value); }

    private int _desktopZoneSchemaVersion;
    [JsonPropertyName("desktopZoneSchemaVersion")]
    public int DesktopZoneSchemaVersion { get => _desktopZoneSchemaVersion; set => SetProperty(ref _desktopZoneSchemaVersion, value); }

    private bool _enableDesktopCard = true;
    /// <summary>桌面卡片悬浮窗：常驻桌面、可拖动，按全局快捷键呼出/收起。</summary>
    [JsonPropertyName("enableDesktopCard")]
    public bool EnableDesktopCard { get => _enableDesktopCard; set => SetProperty(ref _enableDesktopCard, value); }

    private int _desktopCardGap = 16;
    /// <summary>桌面卡片之间的水平 / 垂直统一间距（DIP）。水平垂直用同一数值，
    /// 避免不必要的字段。卡片按此间距自动排布，不支持随意拖动。
    /// 字段缺失时回退到 16（兼容老 config.json 没这个字段）。</summary>
    [JsonPropertyName("desktopCardGap")]
    public int DesktopCardGap { get => _desktopCardGap; set => SetProperty(ref _desktopCardGap, Math.Max(0, value)); }

    private int _desktopCardMaxColumns = 3;
    /// <summary>桌面卡片每行最多显示几个图标（1-5）。决定卡片固定宽度。
    /// 字段缺失时回退到 3，避免字段迁移时老 config.json 报错。</summary>
    [JsonPropertyName("desktopCardMaxColumns")]
    public int DesktopCardMaxColumns
    {
        get => _desktopCardMaxColumns;
        set => SetProperty(ref _desktopCardMaxColumns, Math.Clamp(value, 1, 5));
    }

    private int _desktopCardMargin = 16;
    /// <summary>卡片区距屏幕左 / 右 / 顶部的统一留白（DIP）。三个方向用同一数值，
    /// 用户无需在设置里分别调 3 个 margin。字段缺失时回退到 16。</summary>
    [JsonPropertyName("desktopCardMargin")]
    public int DesktopCardMargin { get => _desktopCardMargin; set => SetProperty(ref _desktopCardMargin, Math.Max(0, value)); }

    private string _desktopCardAlignment = "right";
    /// <summary>桌面卡片靠屏幕哪一侧排布："right"（默认）或 "left"。
    /// 只影响常驻桌面的卡片；从任务栏打开的分区库始终在左侧。</summary>
    [JsonPropertyName("desktopCardAlignment")]
    public string DesktopCardAlignment { get => _desktopCardAlignment; set => SetProperty(ref _desktopCardAlignment, value == "left" ? "left" : "right"); }

    private List<ImeRuleEntry> _perAppImeRules = new();
    [JsonPropertyName("perAppImeRules")]
    public List<ImeRuleEntry> PerAppImeRules { get => _perAppImeRules; set => SetProperty(ref _perAppImeRules, value); }

    private bool _imeCategoryDefaultsInitialized;
    [JsonPropertyName("imeCategoryDefaultsInitialized")]
    public bool ImeCategoryDefaultsInitialized { get => _imeCategoryDefaultsInitialized; set => SetProperty(ref _imeCategoryDefaultsInitialized, value); }
}

/// <summary>
/// 配置文件读写服务：管理 config.json 的加载与保存。
/// MSIX 使用应用数据目录；框架依赖 exe 使用 exe 同目录下的 config.json。
/// </summary>
public static class ConfigService
{
    private const int ErrorNoPackage = 15700;
    private static readonly object SaveLock = new();
    private static string? _configPath;
    private static bool _migrationAttempted;

    /// <summary>当前实际使用的配置文件完整路径。</summary>
    public static string ConfigFilePath => ConfigPath;

    private static string ConfigPath
    {
        get
        {
            if (_configPath != null) return _configPath;

            if (IsPackagedApp())
            {
                _configPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "config.json");
                return _configPath;
            }

            // Debug、Release 与不同框架版本不能各自在 exe 目录维护一份配置，
            // 否则用户从不同入口启动时会看到完全不同的页面设置与桌面分区。
            // 非打包版本统一使用稳定的用户级目录，升级或切换构建不会丢设置。
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinTools",
                "config.json");

            if (!_migrationAttempted)
            {
                _migrationAttempted = true;
                MigrateLegacyConfig(_configPath);
            }

            return _configPath;
        }
    }

    /// <summary>仅当进程真正以 MSIX 包运行时返回 true（避免误判框架依赖 exe）。</summary>
    private static bool IsPackagedApp()
    {
        uint length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != ErrorNoPackage;
    }

    /// <summary>首次运行时，将旧版或当前 exe 目录中的配置迁移到统一用户目录。</summary>
    private static void MigrateLegacyConfig(string targetPath)
    {
        if (File.Exists(targetPath)) return;

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        foreach (var legacy in EnumerateLegacyConfigPaths())
        {
            if (!File.Exists(legacy)) continue;
            try
            {
                File.Copy(legacy, targetPath, overwrite: false);
                return;
            }
            catch
            {
                // 尝试下一个候选路径
            }
        }
    }

    private static IEnumerable<string> EnumerateLegacyConfigPaths()
    {
        var baseDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory
            ?? Environment.CurrentDirectory;
        var paths = new List<string> { Path.Combine(baseDir, "config.json") };

        try
        {
            paths.Add(Path.Combine(ApplicationData.Current.LocalFolder.Path, "config.json"));
        }
        catch
        {
            // ignore
        }

        return paths;
    }

    #region 加载 / 保存

    /// <summary>加载配置（文件不存在或解析失败时返回默认配置）。</summary>
    public static Config Load()
    {
        lock (SaveLock)
            return LoadCore();
    }

    /// <summary>保存配置到 config.json。</summary>
    public static void Save(Config config)
    {
        lock (SaveLock)
            SaveCore(config);
    }

    /// <summary>读取最新配置后执行局部更新，避免其他模块保存时覆盖用户设置。</summary>
    public static void Update(Action<Config> mutate)
    {
        lock (SaveLock)
        {
            var config = LoadCore();
            var before = JsonSerializer.Serialize(config);
            mutate(config);
            if (string.Equals(before, JsonSerializer.Serialize(config), StringComparison.Ordinal))
                return;
            SaveCore(config);
        }
    }

    private static Config LoadCore()
    {
        if (!File.Exists(ConfigPath))
            return GetDefault();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<Config>(json) ?? GetDefault();
            var before = JsonSerializer.Serialize(config);
            config = NormalizeConfig(config);
            // 分类架构或其它兼容字段发生迁移时立即落盘，避免每次 Load 都重复迁移。
            if (!string.Equals(before, JsonSerializer.Serialize(config), StringComparison.Ordinal))
                SaveCore(config);
            return config;
        }
        catch (Exception ex)
        {
            // 解析失败通常意味着文件损坏。直接返回默认值的话，下一次 Save 就会把默认配置
            // 覆盖上去，用户再也拿不回原来的分区与规则；所以先把坏文件留一份副本。
            BackupCorruptConfig(ex);
            return GetDefault();
        }
    }

    private static void BackupCorruptConfig(Exception ex)
    {
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
                File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch { /* 备份失败不能再抛 */ }
        Services.ErrorReporter.Log("ConfigService.Load", ex);
    }

    // 每次 Save 都 new 一个 JsonSerializerOptions 会让 System.Text.Json 的类型元数据缓存
    // 失效（缓存是挂在 options 实例上的），等于每次保存都重新构建一遍序列化器。
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static void SaveCore(Config config)
    {
        config = NormalizeConfig(config);
        var path = ConfigPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(config, WriteOptions);

        // 直接 WriteAllText 不是原子操作：写到一半断电 / 被杀进程就会留下半截 JSON，
        // 下次 Load 解析失败直接退回默认配置，用户的分区和规则全没了。
        // 先写临时文件再替换，和 card-layout-cache 的做法保持一致。
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    private static Config NormalizeConfig(Config config)
    {
        config.Entries ??= new List<ConfigEntry>();
        config.Hotkeys ??= new Dictionary<string, string>();
        // 旧版桌面分区默认键容易与其它软件冲突；只迁移旧默认值，不覆盖用户自定义值。
        if (config.Hotkeys.TryGetValue("DesktopCard", out var desktopCardHotkey) &&
            string.Equals(desktopCardHotkey, "Ctrl+Alt+D", StringComparison.OrdinalIgnoreCase))
            config.Hotkeys["DesktopCard"] = "Ctrl+Alt+Shift+F12";
        config.PerAppImeRules ??= new List<ImeRuleEntry>();
        if (!config.ImeCategoryDefaultsInitialized)
        {
            MergeRecommendedImeRules(config.PerAppImeRules);
            config.ImeCategoryDefaultsInitialized = true;
        }
        config.DesktopZones ??= new List<DesktopZone>();
        UpgradeDesktopZoneTaxonomy(config);
        MergeDesktopFileZones(config.DesktopZones);
        MergeDesktopProgramAndMiscZones(config.DesktopZones);
        EnsureDesktopGameRules(config.DesktopZones);
        EnsureDesktopNetworkRules(config.DesktopZones);
        ConfigureDesktopCatchAllAndGameOrder(config.DesktopZones);
        config.AppTheme = ThemePreference.Normalize(config.AppTheme);
        config.AppUiStyle = UiStylePreference.Normalize(config.AppUiStyle);
        config.ThreeFingerLowSpeedGain = ClampCalibrationAdjustment(
            config.ThreeFingerLowSpeedGain, ThreeFingerCalibrationDefaults.LowSpeedGain);
        config.ThreeFingerHighSpeedGain = ClampCalibrationAdjustment(
            config.ThreeFingerHighSpeedGain, ThreeFingerCalibrationDefaults.HighSpeedGain);
        config.ThreeFingerAccelerationStart = ClampCalibrationAdjustment(
            config.ThreeFingerAccelerationStart, ThreeFingerCalibrationDefaults.AccelerationStart);
        config.ThreeFingerAccelerationEnd = ClampCalibrationAdjustment(
            config.ThreeFingerAccelerationEnd, ThreeFingerCalibrationDefaults.AccelerationEnd);
        return config;
    }

    /// <summary>
    /// 将旧版结构一次性升级为当前推荐的大类。
    /// 只迁移已知旧结构；用户自行新建的分类不会被覆盖。
    /// 历史：
    ///   v3: 8 类。
    ///   v4: 9 类。
    ///   v5: 10 类（含独立的"系统工具类"和"其他"两个分区）。
    ///   v6: 9 类——"系统工具类"并入"其他"；"功能与娱乐" 改名为 "影音与娱乐"；
    ///        "网络" 改名为 "网络与远程"。但当时 `ConfigureDesktopCatchAllAndGameOrder`
    ///        仍会清空"其他"分区的 keywords，导致 v5 直接升 v6 的用户"其他"分区
    ///        keywords 残留为空。
    ///   v7: 修复——不再清空"其他"分区的 keywords；为 v6 升 v7 的"其他"分区
    ///        补上 v6 默认 keywords（火绒、PotPlayer、WinRAR、外设驱动、银行U盾等）。
    ///   v8: "开发与效率"改名为"开发与 AI"，并统一补齐 AI 软件分类规则。
    /// </summary>
    private static void UpgradeDesktopZoneTaxonomy(Config config)
    {
        const int currentSchema = 8;
        if (config.DesktopZoneSchemaVersion >= currentSchema) return;

        if (config.DesktopZoneSchemaVersion < 8)
        {
            var oldDevelopmentZone = config.DesktopZones.FirstOrDefault(zone =>
                string.Equals(zone.Name, "开发与效率", StringComparison.OrdinalIgnoreCase));
            if (oldDevelopmentZone != null)
            {
                // 同步托管目录与 collected.json，避免改名后原分区卡片突然变空。
                DesktopCollectService.RenameZone("开发与效率", "开发与 AI");
                oldDevelopmentZone.Name = "开发与 AI";
            }
        }

        // 包含 v5 及更早的全部历史名字；用于识别"用户没改过分区名"的情况并走整盘重置。
        var legacyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "系统文件", "系统与文件夹", "剪辑软件", "三维软件", "办公与沟通",
            "网络与远程", "网络与云服务", "常用程序与杂项", "常用与杂项", "杂项", "游戏",
            "文件与资料", "文件夹与文件", "设计与创作", "三维与引擎", "开发与效率", "开发与 AI",
            "系统与设备", "系统工具类", "影音与娱乐", "功能与娱乐", "远程与连接", "网络", "其他"
        };
        var looksLikeLegacyLayout = config.DesktopZones.Count > 0
            && config.DesktopZones.All(zone => legacyNames.Contains(zone.Name));

        if (looksLikeLegacyLayout)
        {
            // 收集所有用户显式指定的图标名，重建后按当前默认规则重新分配。
            // 这样"系统工具类"+"其他"合并后，原"系统工具类"分区的 Items 也会被重新按 v6 规则落位。
            var explicitItems = config.DesktopZones
                .SelectMany(zone => zone.Items ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            config.DesktopZones = DesktopZone.CreateDefaults();
            foreach (var item in explicitItems)
            {
                var zoneIndex = DesktopZoneMatcher.Match(item, item, false, config.DesktopZones);
                if (zoneIndex >= 0 && zoneIndex < config.DesktopZones.Count)
                    config.DesktopZones[zoneIndex].Items.Add(item);
            }
        }
        else if (config.DesktopZoneSchemaVersion < currentSchema)
        {
            // 2 -> 3 及更新：保留用户显式指定文件，只刷新内置软件关键词。
            foreach (var recommended in DesktopZone.CreateDefaults())
            {
                var existing = config.DesktopZones.FirstOrDefault(zone =>
                    string.Equals(zone.Name, recommended.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // v6 升 v7：单独处理"其他"分区——v5 直接升 v6 时 keywords 会被
                    // ConfigureDesktopCatchAllAndGameOrder 清空，这里补回 v6 默认值。
                    // 注意：只在分区 keywords 为空时补，避免覆盖用户后续手动加的关键词。
                    if (config.DesktopZoneSchemaVersion < 7 &&
                        string.Equals(existing.Name, "其他", StringComparison.OrdinalIgnoreCase) &&
                        (existing.Keywords == null || existing.Keywords.Count == 0))
                    {
                        existing.Keywords = new List<string>(recommended.Keywords);
                    }
                    else
                    {
                        existing.Keywords = new List<string>(recommended.Keywords);
                    }
                }
            }
        }

        config.DesktopZoneSchemaVersion = currentSchema;
    }

    private static double ClampCalibrationAdjustment(double value, double baseline)
    {
        var ratio = ThreeFingerCalibrationDefaults.MaximumAdjustmentRatio;
        return Math.Clamp(value, baseline * (1.0 - ratio), baseline * (1.0 + ratio));
    }

    /// <summary>将旧版按类型拆分的文件分区合并到第一列"系统文件"。</summary>
    private static void MergeDesktopFileZones(List<DesktopZone> zones)
    {
        if (zones.Count == 0) return;

        var mergeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "系统与文件夹", "系统文件", "办公文档", "图片", "视频", "音频", "音乐", "压缩包"
        };
        var candidates = zones.Where(z => mergeNames.Contains(z.Name)).ToList();
        if (candidates.Count == 0) return;

        var target = candidates.FirstOrDefault(z =>
            string.Equals(z.Name, "系统文件", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(z.Name, "系统与文件夹", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];

        target.Name = "系统文件";
        target.Keywords ??= new List<string>();
        target.Items ??= new List<string>();
        foreach (var source in candidates)
        {
            foreach (var keyword in source.Keywords ?? Enumerable.Empty<string>())
                if (!target.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                    target.Keywords.Add(keyword);
            foreach (var item in source.Items ?? Enumerable.Empty<string>())
                if (!target.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                    target.Items.Add(item);
        }

        zones.RemoveAll(z => !ReferenceEquals(z, target) && candidates.Contains(z));
        zones.Remove(target);
        zones.Insert(0, target);
    }

    /// <summary>将旧版分开的"常用与杂项"和"程序"合并为同一个大类。</summary>
    private static void MergeDesktopProgramAndMiscZones(List<DesktopZone> zones)
    {
        if (zones.Count == 0) return;

        var misc = zones.FirstOrDefault(z =>
            string.Equals(z.Name, "常用与杂项", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(z.Name, "杂项", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(z.Name, "常用程序与杂项", StringComparison.OrdinalIgnoreCase));
        var program = zones.FirstOrDefault(z =>
            string.Equals(z.Name, "程序", StringComparison.OrdinalIgnoreCase));

        if (misc == null || program == null || ReferenceEquals(misc, program)) return;

        misc.Name = "常用程序与杂项";
        misc.Keywords ??= new List<string>();
        misc.Items ??= new List<string>();
        foreach (var keyword in program.Keywords ?? Enumerable.Empty<string>())
            if (!misc.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                misc.Keywords.Add(keyword);
        foreach (var item in program.Items ?? Enumerable.Empty<string>())
            if (!misc.Items.Contains(item, StringComparer.OrdinalIgnoreCase))
                misc.Items.Add(item);

        zones.Remove(program);
    }

    /// <summary>为旧配置补齐游戏链接和育碧启动器识别规则。</summary>
    private static void EnsureDesktopGameRules(List<DesktopZone> zones)
    {
        var game = zones.FirstOrDefault(z =>
            string.Equals(z.Name, "游戏", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(z.Name, "Games", StringComparison.OrdinalIgnoreCase));
        if (game == null) return;

        game.Keywords ??= new List<string>();
        foreach (var keyword in new[] { "Steam", "steam://", "Ubisoft Connect", "Ubisoft", "育碧" })
            if (!game.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
                game.Keywords.Add(keyword);
    }

    /// <summary>为旧配置补齐 Eagle 等网络与远程识别规则。</summary>
    private static void EnsureDesktopNetworkRules(List<DesktopZone> zones)
    {
        var network = zones.FirstOrDefault(z =>
            string.Equals(z.Name, "网络与远程", StringComparison.OrdinalIgnoreCase));
        if (network == null) return;

        network.Keywords ??= new List<string>();
        if (!network.Keywords.Contains("Eagle", StringComparer.OrdinalIgnoreCase))
            network.Keywords.Add("Eagle");
    }

    /// <summary>兜底分区兼容处理：仅重命名历史别名为「其他」，不再强制改顺序。</summary>
    /// <remarks>
    /// v6 起"其他"分区同时承担"杂项+兜底"双重角色：原"系统工具类"分区的关键词
    /// 已并入"其他"（火绒、PotPlayer、WinRAR、外设驱动、银行U盾等），因此这里不再
    /// 强制清空"其他"的 keywords。
    /// v7 起按用户偏好把"游戏"分区排到最末位、"其他"成为倒数第二。
    /// v7.1 修复：<b>不再强制重排</b>——<c>NormalizeConfig</c> 会在每次
    /// <c>SaveCore</c> 之前调到这里，如果它把"其他"和"游戏"重排到末尾，
    /// 会覆盖用户在设置页刚刚拖动换位的结果，导致"在设置里调换了'其他'和'游戏'，
    /// 桌面上却没换"。现在改为：尊重用户的拖动顺序，只在必要时重命名历史别名。
    /// 兜底识别（<c>DesktopZoneMatcher</c>）遍历找名字为"其他"的分区，不依赖
    /// 末尾位置，因此即使"其他"在中间也能正确兜底。
    /// </remarks>
    private static void ConfigureDesktopCatchAllAndGameOrder(List<DesktopZone> zones)
    {
        // 仅重命名历史兜底别名 → "其他"，不改顺序。
        foreach (var zone in zones)
        {
            if (string.Equals(zone.Name, "其他", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zone.Name, "常用程序与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zone.Name, "常用与杂项", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(zone.Name, "杂项", StringComparison.OrdinalIgnoreCase))
            {
                zone.Name = "其他";
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);

    #endregion

    #region 默认配置与程序检测

    /// <summary>获取默认配置（含自动检测已安装程序）。</summary>
    public static Config GetDefault()
    {
        var config = new Config
        {
            Entries = DetectInstalledApps(),
            ImeCategoryDefaultsInitialized = true
        };
        MergeRecommendedImeRules(config.PerAppImeRules);
        return config;
    }

    /// <summary>办公软件默认中文；三维、剪辑与 Adobe 创作软件默认英文。</summary>
    public static List<ImeRuleEntry> GetRecommendedImeRules() => new()
    {
        new() { ProcessName = "WINWORD", DisplayName = "Microsoft Word", UseChinese = true },
        new() { ProcessName = "EXCEL", DisplayName = "Microsoft Excel", UseChinese = true },
        new() { ProcessName = "POWERPNT", DisplayName = "Microsoft PowerPoint", UseChinese = true },
        new() { ProcessName = "OUTLOOK", DisplayName = "Microsoft Outlook", UseChinese = true },
        new() { ProcessName = "wps", DisplayName = "WPS 文字", UseChinese = true },
        new() { ProcessName = "et", DisplayName = "WPS 表格", UseChinese = true },
        new() { ProcessName = "wpp", DisplayName = "WPS 演示", UseChinese = true },
        new() { ProcessName = "DingTalk", DisplayName = "钉钉", UseChinese = true },
        new() { ProcessName = "wemeetapp", DisplayName = "腾讯会议", UseChinese = true },
        new() { ProcessName = "Feishu", DisplayName = "飞书", UseChinese = true },
        new() { ProcessName = "WeChat", DisplayName = "微信", UseChinese = true },
        new() { ProcessName = "WXWork", DisplayName = "企业微信", UseChinese = true },
        new() { ProcessName = "ms-teams", DisplayName = "Microsoft Teams", UseChinese = true },
        new() { ProcessName = "Teams", DisplayName = "Microsoft Teams（经典版）", UseChinese = true },
        new() { ProcessName = "Zoom", DisplayName = "Zoom", UseChinese = true },

        new() { ProcessName = "Adobe Premiere Pro", DisplayName = "Adobe Premiere Pro", UseChinese = false },
        new() { ProcessName = "AfterFX", DisplayName = "Adobe After Effects", UseChinese = false },
        new() { ProcessName = "Photoshop", DisplayName = "Adobe Photoshop", UseChinese = false },
        new() { ProcessName = "Illustrator", DisplayName = "Adobe Illustrator", UseChinese = false },
        new() { ProcessName = "InDesign", DisplayName = "Adobe InDesign", UseChinese = false },
        new() { ProcessName = "Lightroom", DisplayName = "Adobe Lightroom", UseChinese = false },
        new() { ProcessName = "Adobe Media Encoder", DisplayName = "Adobe Media Encoder", UseChinese = false },
        new() { ProcessName = "Audition", DisplayName = "Adobe Audition", UseChinese = false },
        new() { ProcessName = "JianyingPro", DisplayName = "剪映专业版", UseChinese = false },
        new() { ProcessName = "CapCut", DisplayName = "CapCut", UseChinese = false },
        new() { ProcessName = "Resolve", DisplayName = "DaVinci Resolve", UseChinese = false },
        new() { ProcessName = "blender", DisplayName = "Blender", UseChinese = false },
        new() { ProcessName = "Rhino", DisplayName = "Rhino", UseChinese = false },
        new() { ProcessName = "Rhino8", DisplayName = "Rhino 8", UseChinese = false },
        new() { ProcessName = "3dsmax", DisplayName = "Autodesk 3ds Max", UseChinese = false },
        new() { ProcessName = "maya", DisplayName = "Autodesk Maya", UseChinese = false },
        new() { ProcessName = "houdini", DisplayName = "Houdini", UseChinese = false },
        new() { ProcessName = "ZBrush", DisplayName = "ZBrush", UseChinese = false },
        new() { ProcessName = "Cinema 4D", DisplayName = "Cinema 4D", UseChinese = false },
        new() { ProcessName = "UnrealEditor", DisplayName = "Unreal Engine", UseChinese = false },
        new() { ProcessName = "EpicGamesLauncher", DisplayName = "Epic Games Launcher", UseChinese = false },
        new() { ProcessName = "Unity", DisplayName = "Unity Editor", UseChinese = false },
        new() { ProcessName = "UnityHub", DisplayName = "Unity Hub", UseChinese = false },
        new() { ProcessName = "SketchUp", DisplayName = "SketchUp", UseChinese = false },
        new() { ProcessName = "TouchDesigner", DisplayName = "TouchDesigner", UseChinese = false }
    };

    public static void MergeRecommendedImeRules(List<ImeRuleEntry> target)
    {
        foreach (var preset in GetRecommendedImeRules())
        {
            if (target.Any(r => string.Equals(r.ProcessName, preset.ProcessName, StringComparison.OrdinalIgnoreCase)))
                continue;
            target.Add(preset);
        }
    }

    /// <summary>自动检测本机已安装的 Cinema 4D、Blender、After Effects，并按版本命名。</summary>
    public static List<ConfigEntry> DetectInstalledApps()
    {
        var list = new List<ConfigEntry>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        DetectCinema4D(list, programFiles);
        DetectBlender(list, programFiles);
        DetectAfterEffects(list, programFiles);
        return list;
    }

    /// <summary>检测 Cinema 4D：C:\Program Files\Maxon Cinema 4D 20XX\Cinema 4D.exe</summary>
    private static void DetectCinema4D(List<ConfigEntry> list, string programFiles)
    {
        try
        {
            var maxYear = DateTime.Now.Year + 1;
            for (var year = maxYear; year >= 2019; year--)
            {
                var exe = Path.Combine(programFiles, $"Maxon Cinema 4D {year}", "Cinema 4D.exe");
                if (File.Exists(exe))
                    list.Add(new ConfigEntry { Ext = ".c4d", Name = $"Cinema 4D {year}", Path = exe });
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>检测 Blender：C:\Program Files\Blender Foundation\Blender X.X\blender.exe</summary>
    private static void DetectBlender(List<ConfigEntry> list, string programFiles)
    {
        try
        {
            var foundation = Path.Combine(programFiles, "Blender Foundation");
            if (!Directory.Exists(foundation)) return;

            foreach (var dir in Directory.EnumerateDirectories(foundation, "Blender *").OrderByDescending(d => d))
            {
                var exe = Path.Combine(dir, "blender.exe");
                if (!File.Exists(exe)) continue;

                var folderName = Path.GetFileName(dir);
                var version = GetVersionFromExe(exe) ?? folderName.Replace("Blender ", "");
                var name = string.IsNullOrEmpty(version) ? folderName : $"Blender {version}";
                list.Add(new ConfigEntry { Ext = ".blend", Name = name, Path = exe });
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>检测 After Effects：C:\Program Files\Adobe\Adobe After Effects 20XX\Support Files\AfterFX.exe</summary>
    private static void DetectAfterEffects(List<ConfigEntry> list, string programFiles)
    {
        try
        {
            var adobe = Path.Combine(programFiles, "Adobe");
            if (!Directory.Exists(adobe)) return;

            foreach (var dir in Directory.EnumerateDirectories(adobe, "Adobe After Effects *").OrderByDescending(d => d))
            {
                var exe = Path.Combine(dir, "Support Files", "AfterFX.exe");
                if (!File.Exists(exe)) continue;

                var folderName = Path.GetFileName(dir);
                var yearMatch = Regex.Match(folderName, @"(\d{4})");
                var year = yearMatch.Success ? yearMatch.Groups[1].Value : "";
                var name = string.IsNullOrEmpty(year) ? folderName : $"After Effects {year}";
                list.Add(new ConfigEntry { Ext = ".aep", Name = name, Path = exe });
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>从 exe 中读取文件版本信息（取主版本号，如 "4.2"）。</summary>
    private static string? GetVersionFromExe(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var v = info.FileVersion ?? info.ProductVersion;
            if (string.IsNullOrEmpty(v)) return null;
            var parts = v.Split('.');
            return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
        }
        catch { return null; }
    }

    #endregion
}
