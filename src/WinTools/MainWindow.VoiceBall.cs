using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTools.Services;

namespace WinTools;

/// <summary>语音小球：设置页读写。</summary>
/// <remarks>MainWindow 的分部实现；光标跟踪与小球本体见 <see cref="VoiceBallService"/>。</remarks>
public sealed partial class MainWindow
{
    #region 语音小球

    /// <summary>语音快捷键在 <see cref="Config.Hotkeys"/> 里的键名。</summary>
    internal const string VoiceBallHotkeyKey = "VoiceBall";

    // 设置页文案与其他页一致：一句话、不带句号、不写实现细节（像素、缩放比例等）。
    private const string VoiceBallHotkeyDescription = "点击小球时发送的快捷键，默认 Win+H 为 Windows 语音输入";
    private const string VoiceBallHotkeyInvalidDescription = "无法识别此快捷键，请使用 Ctrl+Shift+V 这样的格式";
    private const string VoiceBallPositionDefaultDescription = "默认显示在输入光标下方，拖动小球可调整位置";
    private const string VoiceBallPositionCustomDescription = "已使用自定义位置，拖动小球可重新调整";

    /// <summary>当前绑定的语音快捷键；未设置时用系统自带的语音输入 Win+H。</summary>
    private string GetVoiceBallHotkey() =>
        _config.Hotkeys.TryGetValue(VoiceBallHotkeyKey, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : VoiceBallService.DefaultHotkey;

    internal static (double X, double Y)? GetVoiceBallOffset(Config config) =>
        config.VoiceBallCustomOffset ? (config.VoiceBallOffsetX, config.VoiceBallOffsetY) : null;

    private void LoadVoiceBallFromConfig()
    {
        _isLoadingDragStashSettings = true;

        if (VoiceBallToggle != null) VoiceBallToggle.IsOn = _config.EnableVoiceBall;
        if (HotkeyVoiceBallBox != null) HotkeyVoiceBallBox.Text = GetVoiceBallHotkey();
        UpdateVoiceBallHotkeyStatus(GetVoiceBallHotkey());
        UpdateVoiceBallPositionCard();

        _isLoadingDragStashSettings = false;
    }

    internal void VoiceBallToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDragStashSettings) return;

        var enabled = ((ToggleSwitch)sender).IsOn;
        _config.EnableVoiceBall = enabled;
        ConfigService.Update(c => c.EnableVoiceBall = enabled);

        (App.Current as App)?.SetVoiceBallEnabled(enabled, GetVoiceBallHotkey(), GetVoiceBallOffset(_config));
        UpdateNavStatusIndicators();
    }

    internal void VoiceBall_ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        var text = HotkeyVoiceBallBox?.Text?.Trim() ?? "";
        if (HotkeyHelper.ParseForSend(text) == null)
        {
            UpdateVoiceBallHotkeyStatus(text);
            return;
        }

        _config.Hotkeys ??= new Dictionary<string, string>();
        _config.Hotkeys[VoiceBallHotkeyKey] = text;
        ConfigService.Save(_config);
        (App.Current as App)?.SetVoiceBallHotkey(text);
        UpdateVoiceBallHotkeyStatus(text);
    }

    /// <summary>用户在桌面上拖动小球松手（已由 App 切回 UI 线程）：记住偏移并刷新设置页示意图。</summary>
    internal void OnVoiceBallOffsetDragged(double offsetX, double offsetY)
    {
        _config.VoiceBallOffsetX = offsetX;
        _config.VoiceBallOffsetY = offsetY;
        _config.VoiceBallCustomOffset = true;
        ConfigService.Update(c =>
        {
            c.VoiceBallOffsetX = offsetX;
            c.VoiceBallOffsetY = offsetY;
            c.VoiceBallCustomOffset = true;
        });
        UpdateVoiceBallPositionCard();
    }

    internal void VoiceBall_ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _config.VoiceBallCustomOffset = false;
        _config.VoiceBallOffsetX = 0;
        _config.VoiceBallOffsetY = 0;
        ConfigService.Update(c =>
        {
            c.VoiceBallCustomOffset = false;
            c.VoiceBallOffsetX = 0;
            c.VoiceBallOffsetY = 0;
        });
        (App.Current as App)?.SetVoiceBallOffset(null);
        UpdateVoiceBallPositionCard();
    }

    /// <summary>快捷键卡片的说明行兼作校验反馈：填错时点「应用」能立刻看到原因。</summary>
    private void UpdateVoiceBallHotkeyStatus(string hotkey)
    {
        if (VoiceBallHotkeyCard == null) return;
        VoiceBallHotkeyCard.Description = HotkeyHelper.ParseForSend(hotkey) == null
            ? VoiceBallHotkeyInvalidDescription
            : VoiceBallHotkeyDescription;
    }

    /// <summary>位置卡片：说明文字区分默认 / 自定义位置，未自定义时「重置」置灰。</summary>
    private void UpdateVoiceBallPositionCard()
    {
        var custom = GetVoiceBallOffset(_config) != null;

        if (VoiceBallPositionCard != null)
        {
            VoiceBallPositionCard.Description = custom
                ? VoiceBallPositionCustomDescription
                : VoiceBallPositionDefaultDescription;
        }
        if (VoiceBallResetPositionButton != null) VoiceBallResetPositionButton.IsEnabled = custom;
    }

    #endregion
}
