using System;
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
            TaskbarControl<ComboBox>("TaskbarInfoPlacementBox").SelectedIndex = _config.TaskbarInfoPlacement == "tray" ? 1 : 0;
            TaskbarControl<ToggleSwitch>("TaskbarInfoBackgroundToggle").IsOn = _config.TaskbarInfoBackground;
            TaskbarControl<ComboBox>("TaskbarInfoModeBox").SelectedIndex = _config.TaskbarInfoMode == "text" ? 1 : 0;
            TaskbarControl<TextBox>("TaskbarInfoTextBox").Text = _config.TaskbarInfoText ?? "";
            TaskbarControl<NumberBox>("TaskbarInfoOffsetBox").Value = Math.Clamp(_config.TaskbarInfoOffset, 0, 1200);
            UpdateTaskbarInfoStatus();
        }
        finally { _loadingTaskbarInfo = false; }
    }

    internal void UpdateTaskbarInfoStatus()
    {
        var custom = _config.TaskbarInfoMode == "text";
        var left = _config.TaskbarInfoPlacement != "tray";
        TaskbarControl<SettingsCard>("TaskbarBackgroundCard").Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        TaskbarControl<SettingsCard>("TaskbarOffsetCard").Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        TaskbarControl<SettingsCard>("TaskbarCustomTextCard").Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var status = TaskbarControl<SettingsCard>("TaskbarQuotaStatusCard");
        status.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        status.IsEnabled = _config.EnableTaskbarInfo;
        status.Description = !_config.EnableTaskbarInfo ? "开启左下角信息后读取额度" : (App.Current as App)?.TaskbarInfo?.Status ?? "正在读取 Codex 额度";
    }

    private void SaveTaskbarInfoSettings()
    {
        ConfigService.Update(c =>
        {
            c.EnableTaskbarInfo = _config.EnableTaskbarInfo;
            c.TaskbarInfoPlacement = _config.TaskbarInfoPlacement;
            c.TaskbarInfoBackground = _config.TaskbarInfoBackground;
            c.TaskbarInfoActions = _config.TaskbarInfoActions;
            c.TaskbarInfoMode = _config.TaskbarInfoMode;
            c.TaskbarInfoText = _config.TaskbarInfoText;
            c.TaskbarInfoOffset = _config.TaskbarInfoOffset;
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

    internal void TaskbarInfoAppearance_Changed()
    {
        if (_loadingTaskbarInfo) return;
        _config.TaskbarInfoPlacement = TaskbarControl<ComboBox>("TaskbarInfoPlacementBox").SelectedIndex == 1 ? "tray" : "left";
        _config.TaskbarInfoBackground = TaskbarControl<ToggleSwitch>("TaskbarInfoBackgroundToggle").IsOn;
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarInfoMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTaskbarInfo) return;
        _config.TaskbarInfoMode = ((ComboBox)sender).SelectedIndex == 1 ? "text" : "codex";
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarInfoApply_Click(object sender, RoutedEventArgs e)
    {
        _config.TaskbarInfoText = TaskbarControl<TextBox>("TaskbarInfoTextBox").Text.Trim();
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarInfoOffset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loadingTaskbarInfo || !double.IsFinite(e.NewValue)) return;
        _config.TaskbarInfoOffset = (int)Math.Clamp(e.NewValue, 0, 1200);
        SaveTaskbarInfoSettings();
    }

    internal void TaskbarInfoRefresh_Click(object sender, RoutedEventArgs e) => (App.Current as App)?.TaskbarInfo?.Refresh();
}
