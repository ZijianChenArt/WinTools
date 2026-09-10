using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTools.Services;

namespace WinTools;

/// <summary>悬浮搜索：设置页读写与快捷键入口。</summary>
/// <remarks>MainWindow 的分部实现；窗口本体见 <see cref="SpotlightWindow"/>。</remarks>
public sealed partial class MainWindow
{
    #region 悬浮搜索

    /// <summary>当前生效的悬浮搜索快捷键（供设置页显示与注册使用）。</summary>
    private string GetSpotlightHotkey() =>
        _config.Hotkeys.TryGetValue(SpotlightHotkeyKey, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : SpotlightHotkeyDefault;

    /// <summary>快捷键回调：呼出 / 收起悬浮搜索窗口。</summary>
    private void ToggleSpotlight() => (App.Current as App)?.ToggleSpotlight();

    private void LoadSpotlightFromConfig()
    {
        _isLoadingDragStashSettings = true;

        if (SpotlightToggle != null) SpotlightToggle.IsOn = _config.EnableSpotlight;
        if (HotkeySpotlightBox != null) HotkeySpotlightBox.Text = GetSpotlightHotkey();
        UpdateSpotlightStatus();

        _isLoadingDragStashSettings = false;
    }

    internal void SpotlightToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDragStashSettings) return;

        var enabled = ((ToggleSwitch)sender).IsOn;
        _config.EnableSpotlight = enabled;
        ConfigService.Update(c => c.EnableSpotlight = enabled);

        // 开关直接决定快捷键是否占用，立即重注册而不是等下次启动。
        RegisterAllHotkeys();
        if (enabled) _ = AppSearchIndex.GetAsync();
        UpdateNavStatusIndicators();
    }

    internal void Spotlight_ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        _config.Hotkeys ??= new Dictionary<string, string>();
        _config.Hotkeys[SpotlightHotkeyKey] = HotkeySpotlightBox?.Text?.Trim() ?? "";
        ConfigService.Save(_config);
        RegisterAllHotkeys();
    }

    /// <summary>装 / 卸载程序后手动重建索引（索引平时按 10 分钟有效期自动过期）。</summary>
    internal async void Spotlight_RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        if (SpotlightStatusText != null) SpotlightStatusText.Text = "正在重建索引…";
        try
        {
            var entries = await AppSearchIndex.GetAsync(forceRefresh: true);
            if (SpotlightStatusText != null)
                SpotlightStatusText.Text = $"已索引 {entries.Count} 个应用程序。";
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("MainWindow.Spotlight_RebuildIndex_Click", ex);
            if (SpotlightStatusText != null) SpotlightStatusText.Text = "索引重建失败，详见错误日志。";
        }
    }

    private void UpdateSpotlightStatus()
    {
        if (SpotlightStatusText == null) return;
        var count = AppSearchIndex.Snapshot.Count;
        SpotlightStatusText.Text = count > 0
            ? $"已索引 {count} 个应用程序。"
            : "索引尚未建立，首次呼出时会自动扫描。";
    }

    #endregion
}
