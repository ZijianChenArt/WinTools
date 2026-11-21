using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTools.Services;

namespace WinTools;

public sealed partial class MainWindow
{
    private bool _loadingClipboardHistory;

    private void LoadClipboardHistory()
    {
        if (App.Current is not App app) return;
        _loadingClipboardHistory = true;
        try
        {
            FeaturePages.GetControl<ToggleSwitch>("ClipboardRecordingToggle").IsOn = _config.EnableClipboardHistory;
            FeaturePages.GetControl<ClipboardHistoryView>("ClipboardHistoryPanel").Bind(app.ClipboardHistory);
        }
        finally { _loadingClipboardHistory = false; }
    }

    internal void ClipboardRecording_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingClipboardHistory || sender is not ToggleSwitch toggle || App.Current is not App app) return;
        app.ClipboardHistory.SetRecording(toggle.IsOn);
        _config.EnableClipboardHistory = toggle.IsOn;
        SettingsService.Instance.FlushNow();
        UpdateNavStatusIndicators();
    }
}
