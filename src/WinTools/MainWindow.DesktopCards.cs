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

/// <summary>桌面分区页面：分区列表、卡片布局、预设与同步。</summary>
/// <remarks>MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。</remarks>
public sealed partial class MainWindow
{
    #region 桌面分区
    private DesktopCardManager EnsureDesktopCardManager()
    {

        if (_desktopCardManager != null) return _desktopCardManager;

        var registry = WindowRegistry;

        _desktopCardManager = new DesktopCardManager(w => registry.Register(w));
        // 卡片上拖动图标换分区后，设置页的分区列表要跟着刷新，否则下次保存会用旧副本覆盖。
        _desktopCardManager.ZonesChangedExternally += (_, _) =>
            DispatcherQueue.TryEnqueue(ReloadDesktopZonesFromConfig);

        return _desktopCardManager;


    }

    /// <summary>主窗口完成首帧后再导入旧数据并渐进创建桌面卡片。</summary>
    internal async void InitializeDesktopCardsAfterFirstFrame()
    {
        if (!_config.EnableDesktopCard) return;

        try
        {

            // 给主窗口一次自然呈现的机会，再开始创建多个独立 HWND。
            await Task.Delay(80);
            await EnsureDesktopCardManager().InitializeAsync();


        }

        catch (Exception ex)
        {

            ErrorReporter.Log("MainWindow.InitializeDesktopCardsAfterFirstFrame", ex);


        }


    }

    /// <summary>快捷键「呼出」：把所有分区卡片抬回最前面。</summary>
    private void RaiseDesktopCards() => EnsureDesktopCardManager().RaiseAll();
    /// <summary>获取桌面卡片当前生效的快捷键（供设置页读取 / 显示）。</summary>
    private string GetDesktopCardHotkey() => _config.Hotkeys.TryGetValue(DesktopCardHotkeyKey, out var value) && !string.IsNullOrWhiteSpace(value) ? value : DesktopCardHotkeyDefault;
    internal void DesktopCard_ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {

        _config.Hotkeys ??= new Dictionary<string, string>();

        _config.Hotkeys[DesktopCardHotkeyKey] = HotkeyDesktopCardBox?.Text?.Trim() ?? "";

        ConfigService.Save(_config);

        RegisterAllHotkeys();


    }

    internal void DesktopCardToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        var on = ((ToggleSwitch)sender).IsOn;

        _config.EnableDesktopCard = on;

        ConfigService.Update(c => c.EnableDesktopCard = on);

        EnsureDesktopCardManager().SetEnabled(on);

        UpdateCollectStatus();

        UpdateNavStatusIndicators();


    }

    /// <summary>分区增删改后，若卡片已开启则同步。</summary>
    /// <summary>从配置重新载入分区列表（卡片侧拖放换区后调用），保留当前选中项。</summary>
    private void ReloadDesktopZonesFromConfig()
    {
        var selectedName = (ZonesListView.SelectedItem as DesktopZone)?.Name;
        _desktopZones.Clear();
        foreach (var zone in SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>())
            _desktopZones.Add(CloneZone(zone));
        _config.DesktopZones = _desktopZones.Select(CloneZone).ToList();
        UpdateZoneCount();
        if (selectedName != null)
        {
            ZonesListView.SelectedItem = _desktopZones
                .FirstOrDefault(z => string.Equals(z.Name, selectedName, StringComparison.Ordinal));
        }
    }

    private void SyncDesktopCardsIfEnabled()
    {
        if (_desktopCardManager?.IsEnabled == true) _desktopCardManager.Sync();


    }

    internal void DesktopCardMaxColumns_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        var v = (int)Math.Round(sender.Value);

        if (v < 1) v = 1;

        if (v > 5) v = 5;

        _config.DesktopCardMaxColumns = v;

        ConfigService.Update(c => c.DesktopCardMaxColumns = v);

        // 重排现有卡片宽度
        if (_desktopCardManager != null && _desktopCardManager.IsEnabled) _desktopCardManager.SyncContent();

    }

    internal void DesktopCardGap_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender == null || double.IsNaN(sender.Value)) return;

        var v = (int)Math.Round(sender.Value);

        // 2 个 NumberBox 共用此 handler：DesktopCardGapBox（间距）+ DesktopCardMarginBox（三边留白）
        if (sender == DesktopCardGapBox)
        {
            _config.DesktopCardGap = v;

            ConfigService.Update(c => c.DesktopCardGap = v);


        }

        else if (sender == DesktopCardMarginBox)
        {

            _config.DesktopCardMargin = v;

            ConfigService.Update(c => c.DesktopCardMargin = v);


        }

        _desktopCardManager?.Layout();


    }

    /// <summary>刷新「已收纳 N 项」提示。</summary>
    private void UpdateCollectStatus()
    {
        if (CollectStatusText == null) return;

        var n = DesktopCollectService.CollectedCount();

        CollectStatusText.Text = n > 0 ? $"已收纳 {n}项" : "";


    }

    internal async void DesktopRestore_Click(object sender, RoutedEventArgs e)
    {

        if (MainNav.XamlRoot == null) return;

        var pending = DesktopCollectService.CollectedCount();

        if (pending == 0)
        {

            await new ContentDialog
            {

                Title = "没有可还原的项",
                Content = "收纳日志为空，桌面文件都在原位。",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

.ShowAsync();

            return;


        }

        var confirm = new ContentDialog
        {

            Title = "确认还原",
            Content = $"将把 {pending}个已收纳的项目移回桌面原位置。",
            PrimaryButtonText = "全部还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = MainNav.XamlRoot,
        }

;

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // 先关闭自动收纳，否则文件回到桌面后会立即再次进入卡片。
        _config.EnableDesktopCard = false;
        ConfigService.Update(c => c.EnableDesktopCard = false);

        _desktopCardManager?.SetEnabled(false);

        if (DesktopCardToggle != null)
        {

            _isLoadingDragStashSettings = true;

            DesktopCardToggle.IsOn = false;

            _isLoadingDragStashSettings = false;


        }

        var restored = DesktopCollectService.RestoreAll(out var failed);

        UpdateCollectStatus();

        SyncDesktopCardsIfEnabled();

        await new ContentDialog
        {

            Title = "还原完成",
            Content = failed > 0 ? $"已还原 {restored}项，{failed}项失败（保留在日志里，可再试一次）。" : $"已把 {restored}项送回桌面。",
            CloseButtonText = "确定",
            XamlRoot = MainNav.XamlRoot,
        }

.ShowAsync();


    }

    private void SyncMainToggle(ToggleSwitch? toggle, bool enabled)
    {

        if (toggle == null) return;

        _isLoadingDragStashSettings = true;

        toggle.IsOn = enabled;

        _isLoadingDragStashSettings = false;


    }

    #endregion

    #region 桌面分区
    private void LoadDesktopClickFromConfig()
    {

        _isLoadingDragStashSettings = true;

        DesktopClickToggle.IsOn = _config.EnableDesktopClickToShow;

        _isLoadingDragStashSettings = false;


    }

    internal void DesktopClickToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        var enabled = DesktopClickToggle.IsOn;

        _config.EnableDesktopClickToShow = enabled;

        ConfigService.Update(c => c.EnableDesktopClickToShow = enabled);

        (App.Current as App)?.SetDesktopClickEnabled(enabled);

        UpdateNavStatusIndicators();


    }

    private void LoadDesktopOrganizeFromConfig()
    {

        _isLoadingDragStashSettings = true;

        if (HotkeyDesktopCardBox != null) HotkeyDesktopCardBox.Text = GetDesktopCardHotkey();

        if (DesktopCardToggle != null) DesktopCardToggle.IsOn = _config.EnableDesktopCard;

        SetNumberBoxValue(DesktopCardGapBox, _config.DesktopCardGap);

        SetNumberBoxValue(DesktopCardMaxColumnsBox, _config.DesktopCardMaxColumns);

        SetNumberBoxValue(DesktopCardMarginBox, _config.DesktopCardMargin);

        _isLoadingDragStashSettings = false;

        UpdateCollectStatus();

        LoadDesktopZones();


    }

    private void LoadDesktopZones()
    {

        _config.DesktopZones ??= new List<DesktopZone>();

        // 首次使用（未配置）时填入默认分区，并写回配置
        if (_config.DesktopZones.Count == 0)
        {
            _config.DesktopZones = DesktopZone.CreateDefaults();

            ConfigService.Update(c => c.DesktopZones = _config.DesktopZones);


        }

        _desktopZones.Clear();

        foreach (var zone in _config.DesktopZones) _desktopZones.Add(CloneZone(zone));

        ZonesListView.ItemsSource = _desktopZones;

        UpdateZoneCount();


    }

    private static DesktopZone CloneZone(DesktopZone source) => new()
    {

        Name = source.Name,
        Keywords = new List<string>(source.Keywords ?? new List<string>()),
        Items = new List<string>(source.Items ?? new List<string>())
    }

;

    private void UpdateZoneCount()
    {

        ZoneCountText.Text = _desktopZones.Count > 0 ? $"{_desktopZones.Count}个" : "暂无分区";

        ZoneEmptyHint.Visibility = _desktopZones.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    /// <summary>把分区写回配置，并同步桌面卡片。</summary>
    private void SaveDesktopZones()
    {
        _config.DesktopZones = _desktopZones.Select(CloneZone).ToList();

        ConfigService.Update(c => c.DesktopZones = _config.DesktopZones);

        UpdateZoneCount();

        SyncDesktopCardsIfEnabled();


    }

    internal async void Zone_New_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptForZoneNameAsync("新建分区", "", "新建");
        if (name == null) return;
        _desktopZones.Add(new DesktopZone { Name = name });
        SaveDesktopZones();
    }

    internal async void Zone_Rename_Click(object sender, RoutedEventArgs e)
    {
        var zone = (sender as FrameworkElement)?.Tag as DesktopZone
            ?? ZonesListView.SelectedItem as DesktopZone;
        if (zone == null) return;

        var name = await PromptForZoneNameAsync("重命名分区", zone.Name, "重命名");
        if (name == null || string.Equals(name, zone.Name, StringComparison.Ordinal)) return;
        if (!DesktopCollectService.RenameZone(zone.Name, name)) return;

        zone.Name = name;
        SaveDesktopZones();
    }

    private async Task<string?> PromptForZoneNameAsync(string title, string currentName, string actionText)
    {
        if (MainNav.XamlRoot == null) return null;
        var nameBox = new TextBox
        {
            Text = currentName,
            PlaceholderText = "例如 办公、素材、游戏",
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = nameBox,
            PrimaryButtonText = actionText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = MainNav.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var name = nameBox.Text?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>「同步桌面」：按当前分区规则重新收纳桌面项目并刷新卡片。</summary>
    internal async void Zone_Reapply_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.EnableDesktopCard)
        {
            if (MainNav.XamlRoot != null)
            {
                var info = new ContentDialog
                {
                    Title = "桌面分区",
                    Content = "请先打开页面右上角的总开关，再同步桌面。",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                };
                await info.ShowAsync();
            }
            return;
        }

        EnsureDesktopCardManager().Sync();
        UpdateCollectStatus();
    }

    /// <summary>「恢复默认分区」：用内置的推荐分类替换当前分区列表。</summary>
    internal async void Zone_ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MainNav.XamlRoot != null)
        {
            var confirm = new ContentDialog
            {
                Title = "恢复默认分区？",
                Content = "当前分区列表会被内置的推荐分类替换，已收纳的文件会按新分区重新归类。",
                PrimaryButtonText = "恢复默认",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = MainNav.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        _desktopZones.Clear();
        foreach (var zone in DesktopZone.CreateDefaults())
            _desktopZones.Add(zone);
        SaveDesktopZones();
    }

    /// <summary>拖动排序完成后保存新顺序（此时 _desktopZones 已按新顺序排列）。</summary>
    internal void Zones_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        SaveDesktopZones();


    }

    internal void Zone_Delete_Click(object sender, RoutedEventArgs e)
    {
        var zone = (sender as FrameworkElement)?.Tag as DesktopZone
            ?? ZonesListView.SelectedItem as DesktopZone;
        if (zone == null) return;

        _desktopZones.Remove(zone);

        SaveDesktopZones();
    }

    internal void Zones_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        DependencyObject? current = source;
        while (current != null && current is not ListViewItem)
            current = VisualTreeHelper.GetParent(current);
        if (current is ListViewItem item) ZonesListView.SelectedItem = item.Content;
    }

    internal void Zones_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var availableWidth = e.NewSize.Width;
        if (availableWidth <= 0) return;

        // 为滚动条、边框和像素取整预留安全宽度。若把槽位恰好均分整个容器，
        // 调整窗口时系统临时多占几像素就会让最后一项掉到下一行，看起来闪成一列。
        const double layoutReserve = 32;
        var usableWidth = Math.Max(320, availableWidth - layoutReserve);
        var columns = Math.Clamp((int)(usableWidth / 240), 2, 4);
        var itemWidth = Math.Floor(usableWidth / columns);

        // SizeChanged 连续触发时直接更新当前面板，不排队执行已经过期的旧宽度。
        var panel = FindVisualDescendant<ItemsWrapGrid>(ZonesListView);
        if (panel != null) panel.ItemWidth = itemWidth;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    /// <summary>把当前分区列表导出为独立的预设 JSON 文件，便于分享 / 备份。</summary>
    internal async void Zone_PresetExport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeChoices.Add("分区预设文件", new[] {

 ".json"
}

);

        picker.SuggestedFileName = "WinTools-DesktopZones.json";

        var file = await picker.PickSaveFileAsync().AsTask();

        if (file == null) return;

        // 预设文件只关心分区结构：name / keywords / items。其他字段（容器位置 / schemaVersion）
        // 也不导出，避免不同显示器/不同窗口位置硬塞给用户。直接序列化 _desktopZones 列表。
        var snapshot = _desktopZones.Select(CloneZone).ToList();
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {

            WriteIndented = true
        }

);

        await File.WriteAllTextAsync(file.Path, json);

        if (MainNav.XamlRoot != null)
        {

            await new ContentDialog
            {

                Title = "已导出",
                Content = $"分区预设已保存到：\n{file.Path}",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

.ShowAsync();


        }


    }

    /// <summary>从外部预设 JSON 文件加载分区列表，替换当前分区。保留每个新分区的 keywords 和 items；用户已有的 items 与新分区按 name 匹配，名称相同的分区会保留用户拖动换位的 Items（keywords 仍按新预设走）。</summary>
    internal async void Zone_PresetImport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.FileTypeFilter.Add(".json");

        picker.FileTypeFilter.Add("*");

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync().AsTask();

        if (file == null) return;

        List<DesktopZone>? imported;

        try
        {

            var json = await File.ReadAllTextAsync(file.Path);

            imported = JsonSerializer.Deserialize<List<DesktopZone>>(json);


        }

        catch (Exception ex)
        {

            if (MainNav.XamlRoot != null)
            {

                await new ContentDialog
                {

                    Title = "导入失败",
                    Content = $"预设文件解析失败：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

.ShowAsync();


            }

            return;


        }

        if (imported == null || imported.Count == 0)
        {

            if (MainNav.XamlRoot != null)
            {

                await new ContentDialog
                {

                    Title = "预设为空",
                    Content = "所选文件不包含任何分区。",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

.ShowAsync();


            }

            return;


        }

        if (MainNav.XamlRoot != null)
        {

            var confirm = new ContentDialog
            {

                Title = "从预设文件恢复？",
                Content = $"将用预设文件里的 {imported.Count} 个分区替换当前分区。\n\n" + "已分配到当前分区的桌面图标会按新分区结构重新分类（按文件名字段匹配）。",
                PrimaryButtonText = "恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = MainNav.XamlRoot,
            }

;

            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;


        }

        // 把所有已分配的 Items 收集起来：按分区 name → items 列表。        // 然后用 DesktopZoneMatcher 按新分区规则重新分配。
        var allItems = _desktopZones.SelectMany(zone => (zone.Items ?? new List<string>()).Select(name => (Zone: zone.Name, Name: name))).ToList();
        _desktopZones.Clear();

        foreach (var zone in imported) _desktopZones.Add(zone);

        // 重新分配 items：让每个旧 item 找它在哪个新分区
        foreach (var (oldZone, name) in allItems)
        {
            // 用旧分区名作为 path hint + 名字作为 hint，先看新分区里有没有 name 完全匹配的
            var matchIdx = DesktopZoneMatcher.Match(name, name, false, _desktopZones.Select(z => z).ToList());
            if (matchIdx >= 0 && matchIdx < _desktopZones.Count)
            {

                if (_desktopZones[matchIdx].Items == null) _desktopZones[matchIdx].Items = new List<string>();

                if (!_desktopZones[matchIdx].Items.Contains(name, StringComparer.OrdinalIgnoreCase)) _desktopZones[matchIdx].Items.Add(name);


            }


        }

        SaveDesktopZones();


    }

    #endregion
}
