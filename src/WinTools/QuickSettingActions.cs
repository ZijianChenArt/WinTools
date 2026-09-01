using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinTools;

/// <summary>快捷设置面板按钮：读取当前状态标签并执行切换操作。</summary>
public static class QuickSettingActions
{
    /// <summary>底部状态是否激活（网络=WiFi 开，声音=静音）；无法识别时返回 null。</summary>
    public static async Task<bool?> QueryBottomStateActiveAsync(string featureId) => featureId switch
    {
        "SwitchNetwork" => await NetworkToggleService.IsWifiModeActiveAsync(),
        "ToggleMute" => AudioToggleService.TryGetIsMuted(),
        _ => null,
    };

    public static async Task<UIElement> GetPanelLabelAsync(string featureId) =>
        BuildPanelLabel(featureId, await QueryBottomStateActiveAsync(featureId));

    public static UIElement BuildPanelLabel(string featureId, bool? bottomIsActive, double fontSize = 15) =>
        featureId switch
        {
            "SwitchNetwork" => BuildDualStateLabel("宽带", "Wi-Fi", bottomIsActive, fontSize),
            "ToggleMute" => BuildDualStateLabel("声音", "静音", bottomIsActive, fontSize),
            _ => new TextBlock { Text = "未知", TextAlignment = TextAlignment.Center },
        };

    public static Task<bool> ExecuteAsync(string featureId) => featureId switch
    {
        "SwitchNetwork" => NetworkToggleService.SwitchWifiAndEthernetAsync(),
        "ToggleMute" => Task.FromResult(AudioToggleService.ToggleMute()),
        _ => Task.FromResult(false),
    };

    /// <summary>双态标签：上下排列，中间为左对齐小横线；非当前状态降低不透明度。
    /// bottomIsActive 为 null（无法识别）时两态同等显示，不做差异高亮。</summary>
    private static UIElement BuildDualStateLabel(string top, string bottom, bool? bottomIsActive, double fontSize = 15)
    {
        var textBrush = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush;

        var panel = new StackPanel
        {
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var unknown = bottomIsActive is null;
        var topActive = unknown || bottomIsActive == false;
        var bottomActive = unknown || bottomIsActive == true;

        panel.Children.Add(CreateStateText(top, topActive, textBrush, fontSize));
        panel.Children.Add(new Border
        {
            Height = 1,
            Width = 22,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = textBrush,
            Opacity = 0.35,
            Margin = new Thickness(0, 1, 0, 1),
        });
        panel.Children.Add(CreateStateText(bottom, bottomActive, textBrush, fontSize));

        return panel;
    }

    private static TextBlock CreateStateText(string text, bool isActive, Brush? brush, double fontSize) =>
        new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush,
            Opacity = isActive ? 1.0 : 0.45,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
}
