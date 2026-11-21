using System;
using System.Linq;
using WinTools.Services;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTools;

public sealed partial class MainWindow
{
    private bool _loadingTaskbarInfo;
    private T TaskbarControl<T>(string name) where T : DependencyObject => FeaturePages.GetControl<T>(name);

    private void LoadTaskbarInfoSettings()
    {
        _loadingTaskbarInfo = true;
        try
        {
            TaskbarControl<ToggleSwitch>("TaskbarInfoToggle").IsOn = _config.EnableTaskbarInfo;
            TaskbarControl<ToggleSwitch>("TaskbarShortcutsToggle").IsOn = _config.ShowTaskbarShortcuts;
            TaskbarControl<ComboBox>("TaskbarAlignmentBox").SelectedIndex = _config.TaskbarInfoAlignment == "center" ? 1 : 0;
            TaskbarControl<ToggleSwitch>("TaskbarFloatingToggle").IsOn = _config.TaskbarInfoFloating;
            SetNumberBoxValue(TaskbarControl<NumberBox>("TaskbarFloatGapBox"), _config.TaskbarFloatGap);
            TaskbarControl<SettingsCard>("TaskbarFloatGapCard").IsEnabled = _config.TaskbarInfoFloating;
            LoadTaskbarEntries();
            UpdateTaskbarInfoStatus();
        }
        finally { _loadingTaskbarInfo = false; }
    }

    private void LoadTaskbarEntries()
    {
        SetTaskbarEntry("Library", _config.TaskbarShowLibrary, "点击展开或收起桌面分区",
            _config.EnableDesktopCard ? null : "需先启用桌面分区");
        SetTaskbarEntry("Stash", _config.TaskbarShowStash, "点击打开悬浮暂存",
            _config.EnableDragStash ? null : "需先在悬浮暂存页开启拖文件时自动弹出");
        var voiceUnavailable = _config.EnableVoiceInput ? null : "需先启用语音输入";
        SetTaskbarEntry("Voice", _config.TaskbarShowVoice, "点击麦克风发送听写快捷键", voiceUnavailable);
        SetShortcutPreference("VoiceShortcut", _config.TaskbarShowVoice, "随快捷栏显示，可在两页调整", voiceUnavailable);
        SetTaskbarEntry("Clipboard", _config.TaskbarShowClipboard, "点击打开剪贴板历史");
        SetTaskbarEntry("Audio", _config.TaskbarShowAudio, "点击切换扬声器和麦克风");
        ApplyTaskbarShortcutOrder(_config.TaskbarShortcutOrder);
    }

    private void ApplyTaskbarShortcutOrder(string[] order)
    {
        var shortcuts = TaskbarControl<ListView>("TaskbarShortcutList").Items;
        var entries = shortcuts.Cast<SettingsCard>();
        if (entries.Select(card => (string)card.Tag).SequenceEqual(order)) return;
        var cards = entries.ToDictionary(card => (string)card.Tag);
        shortcuts.Clear();
        foreach (var key in order) shortcuts.Add(cards[key]);
    }

    private void SetTaskbarEntry(string key, bool shown, string description, string? unavailable = null)
        => SetShortcutPreference($"Taskbar{key}", shown, description, unavailable);

    private void SetShortcutPreference(string controlPrefix, bool shown, string description, string? unavailable)
    {
        var toggle = TaskbarControl<ToggleSwitch>($"{controlPrefix}Toggle");
        var card = TaskbarControl<SettingsCard>($"{controlPrefix}Card");
        var previous = _loadingTaskbarInfo;
        _loadingTaskbarInfo = true;
        try
        {
            toggle.IsOn = shown;
            toggle.IsEnabled = unavailable == null;
            card.Description = unavailable ?? (shown && !_config.ShowTaskbarShortcuts ? "已启用 · 快捷栏已隐藏" : description);
        }
        finally { _loadingTaskbarInfo = previous; }
    }

    /// <summary>功能开关变化后调用：刷新设置页与任务栏上实际显示的入口。</summary>
    private void SyncTaskbarInfoEntries()
    {
        try
        {
            LoadTaskbarEntries();
            (App.Current as App)?.TaskbarInfo?.Apply(_config);
        }
        catch (Exception ex) { ErrorReporter.Log("TaskbarInfo.Sync", ex); }
    }

    internal void TaskbarEntry_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo || _isLoadingDragStashSettings) return;
        var toggle = (ToggleSwitch)sender;
        switch (toggle.Tag as string)
        {
            case "library": _config.TaskbarShowLibrary = toggle.IsOn; break;
            case "stash": _config.TaskbarShowStash = toggle.IsOn; break;
            case "voice": _config.TaskbarShowVoice = toggle.IsOn; break;
            case "clipboard": _config.TaskbarShowClipboard = toggle.IsOn; break;
            case "audio": _config.TaskbarShowAudio = toggle.IsOn; break;
            default: return;
        }
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarShortcuts_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_loadingTaskbarInfo) return;
        var order = sender.Items.Cast<SettingsCard>().Select(card => (string)card.Tag).ToArray();
        if (order.SequenceEqual(_config.TaskbarShortcutOrder)) return;
        _config.TaskbarShortcutOrder = order;
        SaveTaskbarInfoSettings();
    }

    internal void UpdateTaskbarInfoStatus()
    {
        if (_codexConnect != null || _checkingCodex) return;
        var status = TaskbarControl<SettingsCard>("CodexConnectionCard");
        var info = (App.Current as App)?.TaskbarInfo;
        if (info?.CodexDisplayText is { } quota)
        {
            status.Description = quota;
            ToolTipService.SetToolTip(status, info.Status);
        }
        else if (_config.EnableTaskbarInfo)
        {
            status.Description = "正在读取额度…";
            ToolTipService.SetToolTip(status, info?.Status);
        }
        else if (status.Description is "正在读取额度…")
        {
            status.Description = "连接账户以查看剩余额度";
            ToolTipService.SetToolTip(status, null);
        }
    }

    private void SaveTaskbarInfoSettings()
    {
        ConfigService.Update(c =>
        {
            c.EnableTaskbarInfo = _config.EnableTaskbarInfo;
            c.ShowTaskbarShortcuts = _config.ShowTaskbarShortcuts;
            c.TaskbarInfoAlignment = _config.TaskbarInfoAlignment;
            c.TaskbarInfoFloating = _config.TaskbarInfoFloating;
            c.TaskbarFloatGap = _config.TaskbarFloatGap;
            c.TaskbarShowLibrary = _config.TaskbarShowLibrary;
            c.TaskbarShowStash = _config.TaskbarShowStash;
            c.TaskbarShowVoice = _config.TaskbarShowVoice;
            c.TaskbarShowClipboard = _config.TaskbarShowClipboard;
            c.TaskbarShowAudio = _config.TaskbarShowAudio;
            c.TaskbarShortcutOrder = _config.TaskbarShortcutOrder;
        });
        if (App.Current is App { TaskbarInfo: null } app) app.ApplyTaskbarInfo(_config);
        UpdateTaskbarInfoStatus();
        UpdateNavStatusIndicators();
    }

    internal void TaskbarInfoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.EnableTaskbarInfo = ((ToggleSwitch)sender).IsOn;
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarShortcutsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.ShowTaskbarShortcuts = ((ToggleSwitch)sender).IsOn;
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarAlignment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.TaskbarInfoAlignment = ((ComboBox)sender).SelectedIndex == 1 ? "center" : "left";
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarFloating_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.TaskbarInfoFloating = ((ToggleSwitch)sender).IsOn;
        TaskbarControl<SettingsCard>("TaskbarFloatGapCard").IsEnabled = _config.TaskbarInfoFloating;
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarFloatPositionReset_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        ResetTaskbarFloatingPosition();
        SaveTaskbarInfoSettings();
    }

    private void ResetTaskbarFloatingPosition()
    {
        _config.TaskbarFloatCenterX = null;
        _config.TaskbarFloatBottomY = null;
        // ConfigService.Update reads a separate snapshot; update just these fields when explicitly resetting.
        ConfigService.Update(c => { c.TaskbarFloatCenterX = null; c.TaskbarFloatBottomY = null; });
    }

    internal void TaskbarFloatGap_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loadingTaskbarInfo) return;
        // 清空输入框时恢复为当前值，不写入配置。
        if (double.IsNaN(sender.Value)) { SetNumberBoxValue(sender, _config.TaskbarFloatGap); return; }
        _config.TaskbarFloatGap = (int)Math.Round(sender.Value);
        ResetTaskbarFloatingPosition();
        SaveTaskbarInfoSettings();
    }





}
