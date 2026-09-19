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

/// <summary>按程序切换输入法页面。</summary>
/// <remarks>MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。</remarks>
public sealed partial class MainWindow
{
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

        ImeCountText.Text = _imeRules.Count > 0 ? $"{_imeRules.Count} 条规则" : "";

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
