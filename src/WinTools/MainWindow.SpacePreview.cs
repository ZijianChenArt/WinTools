using Microsoft.UI.Xaml;
using WinTools.Services;

namespace WinTools;

/// <remarks>MainWindow 的分部实现：「空格预览」设置页（资源管理器 / 桌面分区卡片两个开关）。</remarks>
public sealed partial class MainWindow
{
    private bool _isLoadingSpacePreviewSettings;

    private void LoadSpacePreviewFromConfig()
    {
        _isLoadingSpacePreviewSettings = true;
        ExplorerPreviewToggle.IsOn = _config.EnableExplorerPreview;
        CardPreviewToggle.IsOn = _config.EnableCardPreview;
        _isLoadingSpacePreviewSettings = false;
    }

    internal void ExplorerPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSpacePreviewSettings) return;

        var enabled = ExplorerPreviewToggle.IsOn;
        _config.EnableExplorerPreview = enabled;
        ConfigService.Update(c => c.EnableExplorerPreview = enabled);
        ApplySpacePreviewSettings();
    }

    internal void CardPreviewToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSpacePreviewSettings) return;

        var enabled = CardPreviewToggle.IsOn;
        _config.EnableCardPreview = enabled;
        ConfigService.Update(c => c.EnableCardPreview = enabled);
        ApplySpacePreviewSettings();
    }

    /// <summary>把两个开关同步给运行中的服务（钩子与卡片按键会立即按新设置生效），并刷新导航圆点。</summary>
    private void ApplySpacePreviewSettings()
    {
        (App.Current as App)?.SetSpacePreviewEnabled(_config.EnableExplorerPreview, _config.EnableCardPreview);
        UpdateNavStatusIndicators();
    }
}
