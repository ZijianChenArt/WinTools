using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace WinTools;

/// <summary>承载全部功能页的独立视图；主窗口只负责应用壳层与导航。</summary>
public sealed partial class FeaturePagesHost : UserControl
{
    internal MainWindow? Owner { get; set; }
    private void TaskbarAlignment_Changed(object sender, SelectionChangedEventArgs e) => Owner?.TaskbarAlignment_Changed(sender, e);
    private void QuotaConnect_Click(object sender, RoutedEventArgs e) => Owner?.QuotaConnect_Click(sender, e);
    private void QuotaCheck_Click(object sender, RoutedEventArgs e) => Owner?.QuotaCheck_Click(sender, e);
    private void QuotaCancel_Click(object sender, RoutedEventArgs e) => Owner?.QuotaCancel_Click(sender, e);
    private void ClaudeComplete_Click(object sender, RoutedEventArgs e) => Owner?.ClaudeComplete_Click(sender, e);
    private void ClaudeDisconnect_Click(object sender, RoutedEventArgs e) => Owner?.ClaudeDisconnect_Click(sender, e);
    private void TaskbarEntry_Toggled(object sender, RoutedEventArgs e) => Owner?.TaskbarEntry_Toggled(sender, e);
    private void TaskbarInfoToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.TaskbarInfoToggle_Toggled(sender, e);

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

        // 列表工具条：窄窗口只留图标，文字靠 ToolTip 补足（按钮结构见 PageStyles 的 PageToolbarButtonStyle 注释）。
        var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var label in FindToolbarLabels(PageColumns))
            label.Visibility = labelVisibility;
    }

    /// <summary>
    /// 找出所有 PageToolbarButtonStyle 按钮里的文字。只走 Panel.Children / Button.Content 这类逻辑子元素，
    /// 不依赖模板是否已应用，所以当前隐藏的页面也能一起切换。
    /// </summary>
    private static System.Collections.Generic.IEnumerable<TextBlock> FindToolbarLabels(DependencyObject node)
    {
        if (!Application.Current.Resources.TryGetValue("PageToolbarButtonStyle", out var styleObj)
            || styleObj is not Style toolbarButtonStyle)
            yield break;

        var stack = new System.Collections.Generic.Stack<DependencyObject>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is Button button)
            {
                if (button.Style == toolbarButtonStyle && button.Content is Panel content)
                {
                    foreach (var child in content.Children)
                        if (child is TextBlock text)
                            yield return text;
                }
                continue;
            }

            if (current is Panel panel)
            {
                foreach (var child in panel.Children)
                    stack.Push(child);
            }
            else if (current is Border border && border.Child != null)
            {
                stack.Push(border.Child);
            }
            else if (current is ScrollViewer viewer && viewer.Content is DependencyObject viewerContent)
            {
                stack.Push(viewerContent);
            }
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
    private void Update_Check_Click(object sender, RoutedEventArgs e) => Owner?.Update_Check_Click(sender, e);
    private void Update_Install_Click(object sender, RoutedEventArgs e) => Owner?.Update_Install_Click(sender, e);
    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.AutoUpdateToggle_Toggled(sender, e);
    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.AutoStartToggle_Toggled(sender, e);
    private void DesktopCard_ApplyHotkey_Click(object sender, RoutedEventArgs e) => Owner?.DesktopCard_ApplyHotkey_Click(sender, e);
    private void DesktopCardToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DesktopCardToggle_Toggled(sender, e);
    private void DesktopCardGap_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.DesktopCardGap_ValueChanged(sender, args);
    private void DesktopCardMaxColumns_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Owner?.DesktopCardMaxColumns_ValueChanged(sender, args);
    private void DesktopCardAlignment_Changed(object sender, SelectionChangedEventArgs e) => Owner?.DesktopCardAlignment_Changed(sender, e);
    private void DesktopRestore_Click(object sender, RoutedEventArgs e) => Owner?.DesktopRestore_Click(sender, e);
    private void DesktopCleanupBroken_Click(object sender, RoutedEventArgs e) => Owner?.DesktopCleanupBroken_Click(sender, e);
    private void DesktopClickToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DesktopClickToggle_Toggled(sender, e);
    private void DragStash_Show_Click(object sender, RoutedEventArgs e) => Owner?.DragStash_Show_Click(sender, e);
    private void DragStashToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.DragStashToggle_Toggled(sender, e);
    private void ImeGrid_ItemClick(object sender, ItemClickEventArgs e) => Owner?.ImeGrid_ItemClick(sender, e);
    private void ImeGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e) => Owner?.ImeGrid_DragItemsStarting(sender, e);
    private void ImeGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) => Owner?.ImeGrid_DragItemsCompleted(sender, e);
    private void ImePanel_DragOver(object sender, DragEventArgs e) => Owner?.ImePanel_DragOver(sender, e);
    private void ImePanel_Drop(object sender, DragEventArgs e) => Owner?.ImePanel_Drop(sender, e);
    private void Ime_RefreshApps_Click(object sender, RoutedEventArgs e) => Owner?.Ime_RefreshApps_Click(sender, e);
    private void Ime_RestoreDefaults_Click(object sender, RoutedEventArgs e) => Owner?.Ime_RestoreDefaults_Click(sender, e);
    private void OpenTouchpadSettings_Click(object sender, RoutedEventArgs e) => Owner?.OpenTouchpadSettings_Click(sender, e);
    private void PerAppImeToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.PerAppImeToggle_Toggled(sender, e);
    private void ProgramAssocToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.ProgramAssocToggle_Toggled(sender, e);
    private void SpotlightToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.SpotlightToggle_Toggled(sender, e);
    private void Spotlight_ApplyHotkey_Click(object sender, RoutedEventArgs e) => Owner?.Spotlight_ApplyHotkey_Click(sender, e);
    private void Spotlight_RebuildIndex_Click(object sender, RoutedEventArgs e) => Owner?.Spotlight_RebuildIndex_Click(sender, e);
    private void VoiceBallToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.VoiceBallToggle_Toggled(sender, e);
    private void VoiceBall_ApplyHotkey_Click(object sender, RoutedEventArgs e) => Owner?.VoiceBall_ApplyHotkey_Click(sender, e);
    private void VoiceBall_ResetPosition_Click(object sender, RoutedEventArgs e) => Owner?.VoiceBall_ResetPosition_Click(sender, e);
    private void ThemeRadioButtons_SelectionChanged(object sender, SelectionChangedEventArgs e) => Owner?.ThemeRadioButtons_SelectionChanged(sender, e);
    private void ThreeFingerCalibrate_Click(object sender, RoutedEventArgs e) => Owner?.ThreeFingerCalibrate_Click(sender, e);
    private void ThreeFingerDragToggle_Toggled(object sender, RoutedEventArgs e) => Owner?.ThreeFingerDragToggle_Toggled(sender, e);
    private void Zone_Delete_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Delete_Click(sender, e);
    private void Zone_New_Click(object sender, RoutedEventArgs e) => Owner?.Zone_New_Click(sender, e);
    private void Zone_Rename_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Rename_Click(sender, e);
    private void Zone_Reapply_Click(object sender, RoutedEventArgs e) => Owner?.Zone_Reapply_Click(sender, e);
    private void Zone_ResetDefaults_Click(object sender, RoutedEventArgs e) => Owner?.Zone_ResetDefaults_Click(sender, e);
    private void Zone_PresetExport_Click(object sender, RoutedEventArgs e) => Owner?.Zone_PresetExport_Click(sender, e);
    private void Zone_PresetImport_Click(object sender, RoutedEventArgs e) => Owner?.Zone_PresetImport_Click(sender, e);
    private void Zones_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Owner?.Zones_DragItemsCompleted(sender, args);
    private void Zones_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e) => Owner?.Zones_RightTapped(sender, e);
    private void Zones_SizeChanged(object sender, SizeChangedEventArgs e) => Owner?.Zones_SizeChanged(sender, e);
}
