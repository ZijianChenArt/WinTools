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

/// <summary>全局快捷键：注册 / 注销与窗口过程子类化。</summary>
/// <remarks>MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。</remarks>
public sealed partial class MainWindow
{
    #region 全局快捷键
    private void InitGlobalHotkeys()
    {

        try
        {

            _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

            _hotkeyWndProc = HotkeyWndProc;

            var newProcPtr = Marshal.GetFunctionPointerForDelegate(_hotkeyWndProc);

            var oldProc = NativeSetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProcPtr);

            _originalWndProc = Marshal.GetDelegateForFunctionPointer<WNDPROC>(oldProc);

            if (_config.EnableThreeFingerDrag) _touchpadInputHost.Start(true);

            RegisterAllHotkeys();


        }

        catch
        {

            /* 注册失败时仅无法使用快捷键，不影响其他功能 */
        }


    }

    private LRESULT HotkeyWndProc(HWND hwnd, uint uMsg, WPARAM wParam, LPARAM lParam)
    {

        if (uMsg == WM_HOTKEY)
        {

            var id = (int)(nuint)wParam.Value;

            if (id == HotkeyIdDesktopCard)
            {

                RaiseDesktopCards();

                return (LRESULT)IntPtr.Zero;


            }

            if (id == HotkeyIdSpotlight)
            {

                ToggleSpotlight();

                return (LRESULT)IntPtr.Zero;


            }


        }

        // 注销 / 关机。WinUI 3 收到 WM_ENDSESSION 不会自己退出，Windows 等满
        // WaitToKillAppTimeout（默认 5s）后就把本进程所有可见顶层窗口列进
        // 「这些应用阻止关机」——9 张分区卡片就是 9 行同名条目。必须自己退。
        // 两条消息都只做通知，处理完继续交给原窗口过程（QUERYENDSESSION 由
        // DefWindowProc 返回 TRUE，不能在这里替它决定要不要放行关机）。
        else if (uMsg == WM_QUERYENDSESSION)
        {
            // 会话还可能被别的应用取消，所以只落盘不退出。
            App.SaveBeforeSessionEnd();
        }
        else if (uMsg == WM_ENDSESSION)
        {
            // wParam 为 0 表示会话结束被取消，此时什么都不做。
            if (wParam.Value != 0) App.ShutdownForSessionEnd();
        }

        // 分辨率 / 缩放 / 任务栏变化后卡片必须重排：位置和尺寸都是按当时的
        // 工作区与 RasterizationScale 算成物理像素写死的，系统不会替我们更新。
        // 这些消息只做通知，必须继续传给原窗口过程（WinUI 自己也要处理 DPI）。
        else if (uMsg is WM_DISPLAYCHANGE or WM_DPICHANGED)
        {
            QueueDesktopCardRelayout();
        }
        else if (uMsg == WM_SETTINGCHANGE && (int)(nuint)wParam.Value == SPI_SETWORKAREA)
        {
            QueueDesktopCardRelayout();
        }

        return PInvoke.CallWindowProc(_originalWndProc!, hwnd, uMsg, wParam, lParam);


    }

    private void RegisterAllHotkeys()
    {

        UnregisterAllHotkeys();

        RegisterOneHotkey(_hwnd, HotkeyIdDesktopCard, GetDesktopCardHotkey());

        // 未启用时不注册，把 Alt+Space 让回系统（窗口菜单）。
        if (_config.EnableSpotlight)
            RegisterOneHotkey(_hwnd, HotkeyIdSpotlight, GetSpotlightHotkey());


    }

    private void UnregisterAllHotkeys()
    {

        try
        {

            PInvoke.UnregisterHotKey(_hwnd, HotkeyIdDesktopCard);

            PInvoke.UnregisterHotKey(_hwnd, HotkeyIdSpotlight);


        }

        catch
        {

            /* ignore */
        }


    }

    private static void RegisterOneHotkey(HWND hwnd, int id, string? hotkeyString)
    {

        var parsed = HotkeyHelper.Parse(hotkeyString);

        if (parsed == null) return;

        try
        {

            PInvoke.RegisterHotKey(hwnd, id, HotkeyHelper.ToWin32(parsed.Value.Modifiers), parsed.Value.VirtualKey);


        }

        catch
        {

            /* ignore */
        }


    }

    #endregion
}
