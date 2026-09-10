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

/// <summary>悬浮暂存页面。</summary>
/// <remarks>MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。</remarks>
public sealed partial class MainWindow
{
    #region 悬浮暂存
    internal void DragStashToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (DragStashToggle == null) return;

        _config.EnableDragStash = DragStashToggle.IsOn;

        (App.Current as App)?.SetDragStashEnabled(_config.EnableDragStash);

        UpdateNavStatusIndicators();


    }

    internal void DragStash_Show_Click(object sender, RoutedEventArgs e)
    {

        (App.Current as App)?.ShowDragStashWindow();


    }

    internal async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (AutoStartToggle == null) return;

        var enable = AutoStartToggle.IsOn;

        if (!AutostartService.SetEnabled(enable, out var error))
        {

            _isLoadingDragStashSettings = true;

            AutoStartToggle.IsOn = !enable;

            _isLoadingDragStashSettings = false;

            if (MainNav.XamlRoot != null)
            {

                var dialog = new ContentDialog
                {

                    Title = "开机启动",
                    Content = $"设置失败：{error}",
                    CloseButtonText = "确定",
                    XamlRoot = MainNav.XamlRoot,
                }

;

                await dialog.ShowAsync();


            }

            return;


        }

        _config.AutoStart = enable;

        ConfigService.Update(c => c.AutoStart = enable);


    }

    internal async void ThreeFingerDragToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        if (ThreeFingerDragToggle == null) return;

        _config.EnableThreeFingerDrag = ThreeFingerDragToggle.IsOn;

        _touchpadInputHost.SetEnabled(_config.EnableThreeFingerDrag);

        ConfigService.Update(c => c.EnableThreeFingerDrag = _config.EnableThreeFingerDrag);

        if (_config.EnableThreeFingerDrag && !_touchpadInputHost.HasTouchpad && MainNav.XamlRoot != null)
        {

            var dialog = new ContentDialog
            {

                Title = "三指拖拽",
                Content = "未检测到 Windows 精确触控板，无法使用此功能。",
                CloseButtonText = "确定",
                XamlRoot = MainNav.XamlRoot,
            }

;

            await dialog.ShowAsync();


        }

        UpdateNavStatusIndicators();


    }

    internal void PositionOffset_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {

        if (_isLoadingDragStashSettings) return;

        if (sender == null) return;

        var value = (int)Math.Max(0, Math.Round(sender.Value));

        sender.Value = value;

        if (sender.Name == nameof(StashOffsetXBox))
        {

            _config.StashOffsetX = value;

            _config.ProgramOffsetX = value;


        }

        else if (sender.Name == nameof(StashOffsetYBox))
        {

            _config.StashOffsetY = value;

            _config.ProgramOffsetY = value;

            _config.WindowGap = value;

            // 垂直距离即两窗间距
        }


    }

    #endregion
}
