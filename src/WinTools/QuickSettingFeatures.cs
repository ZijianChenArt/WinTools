using System;
using System.Collections.Generic;
using System.Linq;

namespace WinTools;

/// <summary>快捷设置中的一项功能（设置页管理启用与快捷键，面板提供直接操作按钮）。</summary>
public sealed class QuickSettingFeature
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string DefaultHotkey { get; init; }
    public required int HotkeyId { get; init; }
    public required Func<Config, bool> GetEnabled { get; init; }
    public required Action<Config, bool> SetEnabled { get; init; }
}

/// <summary>快捷设置功能注册表。新增功能时在此追加，设置页与弹出面板会自动显示。</summary>
public static class QuickSettingFeatures
{
    public const int HotkeyIdQuickPanel = 2;

    private static readonly QuickSettingFeature[] AllFeatures =
    [
        new QuickSettingFeature
        {
            Id = "SwitchNetwork",
            DisplayName = "网络切换",
            Description = "在宽带与 Wi-Fi 之间切换。",
            DefaultHotkey = "Ctrl+Win+N",
            HotkeyId = 1,
            GetEnabled = c => c.EnableQuickSettings,
            SetEnabled = (c, v) => c.EnableQuickSettings = v,
        },
        new QuickSettingFeature
        {
            Id = "ToggleMute",
            DisplayName = "声音控制",
            Description = "切换系统静音状态。",
            DefaultHotkey = "Ctrl+Win+M",
            HotkeyId = 3,
            GetEnabled = c => c.EnableQuickMute,
            SetEnabled = (c, v) => c.EnableQuickMute = v,
        },
    ];

    public static IReadOnlyList<QuickSettingFeature> All => AllFeatures;

    public static QuickSettingFeature? Find(string id) =>
        All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    public static QuickSettingFeature? FindByHotkeyId(int hotkeyId) =>
        All.FirstOrDefault(f => f.HotkeyId == hotkeyId);
}
