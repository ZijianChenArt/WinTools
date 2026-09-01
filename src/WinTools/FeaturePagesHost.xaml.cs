using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace WinTools;

/// <summary>承载全部功能页的独立视图；主窗口只负责应用壳层与导航。</summary>
public sealed partial class FeaturePagesHost : UserControl
{
    internal MainWindow? Owner { get; set; }

    public FeaturePagesHost() => InitializeComponent();

    /// <summary>内容列的最大宽度；超出部分平均分到左右两侧，使内容居中。</summary>
    private const double MaxContentWidth = 1000;

    /// <summary>窄于此宽度时收紧留白、命令栏只留图标，避免元素互相挤压。</summary>
    private const double CompactWidth = 760;

    private static readonly Thickness RegularPagePadding = new(40, 24, 40, 32);
    private static readonly Thickness CompactPagePadding = new(20, 16, 20, 20);

    private bool? _compact;

    /// <summary>
    /// 窗口宽度变化时做两件事：
    /// 1) 把多余宽度平分到内容列两侧 —— 窗口很宽时内容居中，而不是贴在左边；
    /// 2) 窄窗口切换到紧凑排版（留白收紧、命令栏收成图标），否则标题、开关、命令栏会互相挤。
    /// 这些必须用代码算：WinUI 的 Stretch + MaxWidth 会按内容宽度算居中偏移，每页都不一样。
    /// </summary>
    private void PageColumns_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var leading = Math.Max(0, (width - MaxContentWidth) / 2);
        if (Math.Abs(ContentLeadingColumn.Width.Value - leading) > 0.5)
            ContentLeadingColumn.Width = new GridLength(leading);

        var compact = width < CompactWidth;
        if (_compact == compact) return;
        _compact = compact;
        ApplyDensity(compact);
    }

    private void ApplyDensity(bool compact)
    {
        var padding = compact ? CompactPagePadding : RegularPagePadding;
        foreach (var child in PageColumns.Children)
        {
            if (child is Grid page)
                page.Padding = padding;
        }

        foreach (var bar in FindCommandBars(PageColumns))
        {
            bar.DefaultLabelPosition = compact
                ? CommandBarDefaultLabelPosition.Collapsed
                : CommandBarDefaultLabelPosition.Right;
        }
    }

    private static System.Collections.Generic.IEnumerable<CommandBar> FindCommandBars(DependencyObject node)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            if (child is CommandBar bar)
            {
                yield return bar;
                continue;
            }

            foreach (var nested in FindCommandBars(child))
                yield return nested;
        }
    }

    internal T GetControl<T>(string name) where T : DependencyObject =>
        FindName(name) as T ?? throw new InvalidOperationException($"找不到页面控件：{name}");

    private void Assoc_Browse_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Browse_Click(sender, e);
    private void Assoc_Cancel_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Cancel_Click(sender, e);
    private void Assoc_Confirm_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Confirm_Click(sender, e);
    private void Assoc_Delete_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Delete_Click(sender, e);
    private void Assoc_Edit_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Edit_Click(sender, e);
    private void Assoc_Export_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Export_Click(sender, e);
    private void Assoc_Import_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_Import_Click(sender, e);
    private void Assoc_New_Click(object sender, RoutedEventArgs e) => Owner?.Assoc_New_Click(sender, e);
    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.AutoStartToggle_Toggled(sender, e);
    private void DesktopCard_ApplyHotkey_Click(object sender, RoutedEventArgs e) => Owner?.DesktopCard_ApplyHotkey_Click(sender, e);
    private void DesktopCardToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DesktopCardToggle_Toggled(sender, e);
    private void DesktopCardGap_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.DesktopCardGap_ValueChanged(sender, args);
    private void DesktopCardMaxColumns_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.DesktopCardMaxColumns_ValueChanged(sender, args);
    private void DesktopRestore_Click(object sender, RoutedEventArgs e) => Owner?.DesktopRestore_Click(sender, e);
    private void DesktopClickToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DesktopClickToggle_Toggled(sender, e);
    private void DragStash_Show_Click(object sender, RoutedEventArgs e) => Owner?.DragStash_Show_Click(sender, e);
    private void DragStashToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DragStashToggle_Toggled(sender, e);
    private void Ime_Add_Click(object sender, RoutedEventArgs e) => Owner?.Ime_Add_Click(sender, e);
    private void Ime_Cancel_Click(object sender, RoutedEventArgs e) => Owner?.Ime_Cancel_Click(sender, e);
    private void Ime_Confirm_Click(object sender, RoutedEventArgs e) => Owner?.Ime_Confirm_Click(sender, e);
    private void Ime_Delete_Click(object sender, RoutedEventArgs e) => Owner?.Ime_Delete_Click(sender, e);
    private void Ime_RefreshApps_Click(object sender, RoutedEventArgs e) => Owner?.Ime_RefreshApps_Click(sender, e);
    private void Ime_RestoreDefaults_Click(object sender, RoutedEventArgs e) => Owner?.Ime_RestoreDefaults_Click(sender, e);
    private void ImeModeChip_Click(object sender, RoutedEventArgs e) => Owner?.ImeModeChip_Click(sender, e);
    private void OpenTouchpadSettings_Click(object sender, RoutedEventArgs e) => Owner?.OpenTouchpadSettings_Click(sender, e);
    private void PerAppImeToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.PerAppImeToggle_Toggled(sender, e);
    private void PositionOffset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.PositionOffset_ValueChanged(sender, args);
    private void ProgramAssocToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.ProgramAssocToggle_Toggled(sender, e);
    private void QuickPanelPadding_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.QuickPanelPadding_ValueChanged(sender, args);
    private void QuickSettings_Save_Click(object sender, RoutedEventArgs e) => Owner?.QuickSettings_Save_Click(sender, e);
    private void QuickSettings_TestPanel_Click(object sender, RoutedEventArgs e) => Owner?.QuickSettings_TestPanel_Click(sender, e);
    private void QuickSettingsToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.QuickSettingsToggle_Toggled(sender, e);
    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.ThemeRadioButtons_SelectionChanged(sender, e);
    private void ThreeFingerCalibrate_Click(object sender, RoutedEventArgs e) => Owner?.ThreeFingerCalibrate_Click(sender, e);
    private void ThreeFingerDragToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.ThreeFingerDragToggle_Toggled(sender, e);
    private void Zone_Cancel_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Cancel_Click(sender, e);
    private void Zone_Confirm_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Confirm_Click(sender, e);
    private void Zone_Delete_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Delete_Click(sender, e);
    private void Zone_New_Click(object sender, RoutedEventArgs e) => Owner?.Zone_New_Click(sender, e);
    private void Zone_Edit_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Edit_Click(sender, e);
    private void Zone_Reapply_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Reapply_Click(sender, e);
    private void Zone_ResetDefaults_Click(object sender, RoutedEventArgs e) => Owner?.Zone_ResetDefaults_Click(sender, e);
    private void Zone_PresetExport_Click(object sender, RoutedEventArgs e) => Owner?.Zone_PresetExport_Click(sender, e);
    private void Zone_PresetImport_Click(object sender, RoutedEventArgs e) => Owner?.Zone_PresetImport_Click(sender, e);
    private void ZoneFiles_DragOver(object sender, DragEventArgs e) => Owner?.ZoneFiles_DragOver(sender, e);
    private void ZoneFiles_Drop(object sender, DragEventArgs e) => Owner?.ZoneFiles_Drop(sender, e);
    private void ZoneFiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.ZoneFiles_SelectionChanged(sender, e);
    private void ZoneFilesSearch_TextChanged(object sender, TextChangedEventArgs e) => Owner?.ZoneFilesSearch_TextChanged(sender, e);
    private void Zones_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e) => Owner?.Zones_DoubleTapped(sender, e);
    private void Zones_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Owner?.Zones_DragItemsCompleted(sender, args);
}
