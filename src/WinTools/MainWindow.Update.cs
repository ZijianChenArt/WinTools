using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinTools.Services;

namespace WinTools;

/// <summary>设置页「更新」：检查、下载并安装 GitHub Release 上的新版本。</summary>
/// <remarks>MainWindow 的分部实现；网络与校验逻辑在 <see cref="UpdateService"/>。</remarks>
public sealed partial class MainWindow
{
    private UpdateService.UpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private bool _autoUpdateCheckScheduled;

    private void LoadUpdateSettings()
    {
        var previous = _isLoadingDragStashSettings;
        _isLoadingDragStashSettings = true;
        try
        {
            AutoUpdateToggle.IsOn = _config.EnableAutoUpdateCheck;
            if (_pendingUpdate == null) UpdateCard.Description = $"当前版本 {UpdateService.CurrentVersionText}";
        }
        finally { _isLoadingDragStashSettings = previous; }
        ScheduleAutoUpdateCheck();
    }

    /// <summary>每次启动最多静默检查一次；失败不打扰用户。</summary>
    private void ScheduleAutoUpdateCheck()
    {
        if (_autoUpdateCheckScheduled || !_config.EnableAutoUpdateCheck) return;
        _autoUpdateCheckScheduled = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(20);
        timer.IsRepeating = false;
        timer.Tick += async (_, _) => { timer.Stop(); await CheckForUpdateAsync(silent: true); };
        timer.Start();
    }

    internal async void Update_Check_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(silent: false);

    private async Task CheckForUpdateAsync(bool silent)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = false;
        if (!silent) UpdateCard.Description = "正在检查更新…";
        try
        {
            _pendingUpdate = await UpdateService.CheckAsync();
            if (_pendingUpdate != null)
            {
                UpdateCard.Description = $"发现新版本 {_pendingUpdate.Version}（当前 {UpdateService.CurrentVersionText}）";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                UpdateCard.Description = $"已是最新版本 {UpdateService.CurrentVersionText}";
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("Update.Check", ex);
            if (!silent) UpdateCard.Description = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            _updateBusy = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    internal async void Update_Install_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } update || _updateBusy) return;
        _updateBusy = true;
        CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(value => UpdateCard.Description = $"正在下载 {update.Version}… {value:P0}");
            var path = await UpdateService.DownloadAsync(update, progress);
            UpdateCard.Description = "下载完成，正在安装并重新启动…";
            UpdateService.LaunchInstaller(path);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("Update.Install", ex);
            UpdateCard.Description = $"更新失败：{ex.Message}";
            CheckUpdateButton.IsEnabled = InstallUpdateButton.IsEnabled = true;
        }
        finally { _updateBusy = false; }
    }

    internal void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoadingDragStashSettings) return;
        _config.EnableAutoUpdateCheck = AutoUpdateToggle.IsOn;
        ConfigService.Update(c => c.EnableAutoUpdateCheck = AutoUpdateToggle.IsOn);
        if (AutoUpdateToggle.IsOn) ScheduleAutoUpdateCheck();
    }
}
