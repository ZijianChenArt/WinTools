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

/// <summary>程序关联页面（内嵌编辑）。</summary>
/// <remarks>MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。</remarks>
public sealed partial class MainWindow
{
    #region 程序关联（内嵌编辑）
    private void LoadProgramAssocFromConfig()
    {

        _isLoadingDragStashSettings = true;

        ProgramAssocToggle.IsOn = _config.EnableProgramAssoc;

        _isLoadingDragStashSettings = false;

        LoadAssocEntries();


    }

    internal void ProgramAssocToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        _config.EnableProgramAssoc = ProgramAssocToggle.IsOn;

        (App.Current as App)?.SetProgramAssociationEnabled(_config.EnableProgramAssoc);

        UpdateNavStatusIndicators();


    }

    /// <summary>加载程序关联列表到 UI。</summary>
    private void LoadAssocEntries()
    {
        _assocListInitialized = true;

        _assocEntries.Clear();

        foreach (var e in _config.Entries) _assocEntries.Add(new ConfigEntry
        {

            Ext = e.Ext,
            Name = e.Name,
            Path = e.Path
        }

);

        AssocListView.ItemsSource = _assocEntries;

        UpdateAssocCount();


    }

    private void UpdateAssocCount()
    {

        AssocCountText.Text = _assocEntries.Count > 0 ? $"{_assocEntries.Count} 项" : "暂无关联";

        AssocEmptyHint.Visibility = _assocEntries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    /// <summary>保存当前列表回 Config 并持久化。</summary>
    private void SaveAssocEntries()
    {
        _config.Entries = _assocEntries.Select(e => new ConfigEntry
        {

            Ext = e.Ext,
            Name = e.Name,
            Path = e.Path
        }

).ToList();

        ConfigService.Save(_config);

        UpdateAssocCount();


    }

    private void ShowAssocEditPanel(string title, string ext, string name, string path)
    {

        AssocEditTitle.Text = title;

        AssocExtBox.Text = ext;

        AssocNameBox.Text = name;

        AssocPathBox.Text = path;

        AssocEditPanel.Visibility = Visibility.Visible;

        AssocEditColumn.Width = new GridLength(296);


    }

    private void HideAssocEditPanel()
    {

        AssocEditPanel.Visibility = Visibility.Collapsed;

        AssocEditColumn.Width = new GridLength(0);


    }

    internal void Assoc_New_Click(object sender, RoutedEventArgs e)
    {

        _assocEditingIndex = -1;

        ShowAssocEditPanel("新建关联", "", "", "");


    }

    internal void Assoc_Edit_Click(object sender, RoutedEventArgs e)
    {

        if (AssocListView.SelectedItem is not ConfigEntry entry) return;

        _assocEditingIndex = _assocEntries.IndexOf(entry);

        if (_assocEditingIndex < 0) return;

        ShowAssocEditPanel("编辑关联", entry.Ext, entry.Name, entry.Path);


    }

    internal void Assoc_Delete_Click(object sender, RoutedEventArgs e)
    {

        if (AssocListView.SelectedItem is not ConfigEntry entry) return;

        _assocEntries.Remove(entry);

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal async void Assoc_Browse_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileOpenPicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".exe");

        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        AssocPathBox.Text = file.Path;

        if (string.IsNullOrWhiteSpace(AssocNameBox.Text)) AssocNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(file.Name);


    }

    internal void Assoc_Confirm_Click(object sender, RoutedEventArgs e)
    {

        var ext = (AssocExtBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(ext)) return;

        if (!ext.StartsWith('.')) ext = "." + ext;

        var path = (AssocPathBox.Text ?? "").Trim();

        if (string.IsNullOrEmpty(path)) return;

        var name = (AssocNameBox.Text ?? "").Trim();

        var entry = new ConfigEntry
        {

            Ext = ext.ToLowerInvariant(),
            Name = name,
            Path = path
        };

        if (_assocEditingIndex < 0) _assocEntries.Add(entry);

        else _assocEntries[_assocEditingIndex] = entry;

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal void Assoc_Cancel_Click(object sender, RoutedEventArgs e) => HideAssocEditPanel();

    internal async void Assoc_Import_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileOpenPicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".json");

        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        Config? loaded;

        try
        {

            var json = await File.ReadAllTextAsync(file.Path);

            loaded = JsonSerializer.Deserialize<Config>(json);


        }

        catch (Exception ex)
        {

            if (MainNav.XamlRoot != null) await new ContentDialog
            {

                Title = "导入失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot
            }

.ShowAsync();

            return;


        }

        if (loaded == null) return;

        if (MainNav.XamlRoot != null)
        {

            var confirm = new ContentDialog
            {

                Title = "导入程序关联？",
                Content = "当前的程序关联会被文件里的内容替换。",
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                XamlRoot = MainNav.XamlRoot
            }

;

            if (await confirm.ShowAsync().AsTask() != ContentDialogResult.Primary) return;


        }

        _assocEntries.Clear();

        foreach (var entry in loaded.Entries) _assocEntries.Add(new ConfigEntry
        {

            Ext = entry.Ext,
            Name = entry.Name,
            Path = entry.Path
        }

);

        SaveAssocEntries();

        HideAssocEditPanel();


    }

    internal async void Assoc_Export_Click(object sender, RoutedEventArgs e)
    {

        var picker = new FileSavePicker();

        var hwnd = WindowNative.GetWindowHandle(this);

        InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("JSON 配置", new[] {

 ".json"
}

);

        picker.SuggestedFileName = "WinTools_config.json";

        var file = await picker.PickSaveFileAsync().AsTask();

        if (file == null) return;

        // 先同步列表到 config 再导出完整配置
        SaveAssocEntries();
        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
        {

            WriteIndented = true
        }

);

        await File.WriteAllTextAsync(file.Path, json);

        if (MainNav.XamlRoot != null) await new ContentDialog
        {

            Title = "已导出",
            Content = $"配置已保存到：\n{file.Path}",
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot
        }

.ShowAsync();


    }

    #endregion
}
