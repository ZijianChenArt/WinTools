using System;
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
            TaskbarControl<ComboBox>("TaskbarAlignmentBox").SelectedIndex = _config.TaskbarInfoAlignment == "center" ? 1 : 0;
            LoadTaskbarEntries();
            UpdateTaskbarInfoStatus();
        }
        finally { _loadingTaskbarInfo = false; }
    }

    /// <summary>入口开关：悬浮暂存、语音小球的任务栏入口独立于功能本身（功能关着也能单独放在任务栏）；桌面分区入口仍需先开启桌面分区。</summary>
    private void LoadTaskbarEntries()
    {
        SetTaskbarEntry("Library", _config.TaskbarShowLibrary, _config.EnableDesktopCard, "桌面分区", "在任务栏显示入口，点击展开或收起");
        SetTaskbarEntry("Voice", _config.TaskbarShowVoice, true, "语音小球", "在任务栏显示入口和听写状态");
        SetTaskbarEntry("Audio", _config.TaskbarShowAudio, true, "", "在任务栏显示当前输出设备，点击切换");
    }

    private void SetTaskbarEntry(string key, bool shown, bool featureOn, string feature, string description)
    {
        var toggle = TaskbarControl<ToggleSwitch>($"Taskbar{key}Toggle");
        var card = TaskbarControl<SettingsCard>($"Taskbar{key}Card");
        var previous = _loadingTaskbarInfo;
        _loadingTaskbarInfo = true;
        try
        {
            toggle.IsOn = shown && featureOn;
            toggle.IsEnabled = featureOn;
            card.Description = featureOn ? description : $"需先开启「{feature}」功能";
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
        if (_loadingTaskbarInfo) return;
        var toggle = (ToggleSwitch)sender;
        switch (toggle.Tag as string)
        {
            case "library": _config.TaskbarShowLibrary = toggle.IsOn; break;
            case "voice": _config.TaskbarShowVoice = toggle.IsOn; break;
            case "audio": _config.TaskbarShowAudio = toggle.IsOn; break;
            default: return;
        }
        SaveTaskbarInfoSettings();
    }

    internal void UpdateTaskbarInfoStatus()
    {
        if (_codexConnect != null || _checkingCodex) return;
        var status = TaskbarControl<SettingsCard>("CodexConnectionCard");
        var info = (App.Current as App)?.TaskbarInfo;
        status.Description = !_config.EnableTaskbarInfo ? "任务栏显示已关闭" : info?.CodexDisplayText ?? "正在读取额度…";
        ToolTipService.SetToolTip(status, info?.Status);
    }

    private void SaveTaskbarInfoSettings()
    {
        ConfigService.Update(c =>
        {
            c.EnableTaskbarInfo = _config.EnableTaskbarInfo;
            c.TaskbarInfoAlignment = _config.TaskbarInfoAlignment;
            c.TaskbarInfoActions = _config.TaskbarInfoActions;
            c.TaskbarShowLibrary = _config.TaskbarShowLibrary;
            c.TaskbarShowVoice = _config.TaskbarShowVoice;
            c.TaskbarShowAudio = _config.TaskbarShowAudio;
        });
        (App.Current as App)?.ApplyTaskbarInfo(_config);
        UpdateTaskbarInfoStatus();
        UpdateNavStatusIndicators();
    }

    internal void TaskbarInfoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.EnableTaskbarInfo = ((ToggleSwitch)sender).IsOn;
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarAlignment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.TaskbarInfoAlignment = ((ComboBox)sender).SelectedIndex == 1 ? "center" : "left";
        SaveTaskbarInfoSettings();
    }





}
