using System;
using System.Runtime.InteropServices;

namespace WinTools;

/// <summary>
/// 通过 Shell 接口可靠控制桌面文件夹视图的“自动排列 / 对齐网格”标志。
/// 这是让自定义图标坐标真正生效的关键（单纯改 SysListView32 窗口样式在 Win10/11 上不可靠）。
///
/// 链路：CLSID_ShellWindows → IShellWindows.FindWindowSW(SWC_DESKTOP) → IServiceProvider
/// → IShellBrowser → QueryActiveShellView → IFolderView2 → Set/GetCurrentFolderFlags。
/// </summary>
internal static class DesktopFolderView
{
    // FOLDERFLAGS
    private const uint FWF_AUTOARRANGE = 0x00000001;
    private const uint FWF_SNAPTOGRID = 0x00000004;
    private const uint AutoArrangeSnapMask = FWF_AUTOARRANGE | FWF_SNAPTOGRID;

    private const int SWC_DESKTOP = 0x08;
    private const int SWFO_NEEDDISPATCH = 0x01;

    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    /// <summary>
    /// 读取桌面当前的“自动排列/对齐网格”标志并清除它们，使自定义坐标可保留。
    /// </summary>
    /// <returns>是否成功；originalFlags 返回清除前这两个标志位的原始值（用于还原）。</returns>
    public static bool TryClearAutoArrangeAndSnap(out uint originalFlags)
    {
        originalFlags = 0;
        try
        {
            var view = GetDesktopFolderView();
            if (view == null) return false;

            if (view.GetCurrentFolderFlags(out var flags) == 0)
                originalFlags = flags & AutoArrangeSnapMask;

            var hr = view.SetCurrentFolderFlags(AutoArrangeSnapMask, 0);
            Marshal.ReleaseComObject(view);
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>恢复桌面的“自动排列/对齐网格”标志到指定的原始值。</summary>
    public static bool TryRestoreFlags(uint originalFlags)
    {
        try
        {
            var view = GetDesktopFolderView();
            if (view == null) return false;
            var hr = view.SetCurrentFolderFlags(AutoArrangeSnapMask, originalFlags & AutoArrangeSnapMask);
            Marshal.ReleaseComObject(view);
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    private static IFolderView2? GetDesktopFolderView()
    {
        var type = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        if (type == null) return null;

        var shellWindowsObj = Activator.CreateInstance(type);
        if (shellWindowsObj is not IShellWindows shellWindows) return null;

        try
        {
            object loc = 0;       // CSIDL_DESKTOP
            object root = null!;  // 空（VT_EMPTY）
            var dispatch = shellWindows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            if (dispatch is not IServiceProvider sp) return null;

            var guidService = SID_STopLevelBrowser;
            var guidBrowser = IID_IShellBrowser;
            if (sp.QueryService(ref guidService, ref guidBrowser, out var browserObj) != 0)
                return null;
            if (browserObj is not IShellBrowser browser) return null;

            if (browser.QueryActiveShellView(out var viewObj) != 0)
                return null;

            return viewObj as IFolderView2;
        }
        finally
        {
            Marshal.ReleaseComObject(shellWindows);
        }
    }

    #region COM 接口声明（仅声明用到的方法，其余以占位方法保留 vtable 槽位）

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellWindows
    {
        // IDispatch（4 个占位）
        [PreserveSig] int GetTypeInfoCount();
        [PreserveSig] int GetTypeInfo();
        [PreserveSig] int GetIDsOfNames();
        [PreserveSig] int Invoke();
        // IShellWindows（FindWindowSW 之前 8 个占位）
        [PreserveSig] int get_Count();
        [PreserveSig] int Item();
        [PreserveSig] int _NewEnum();
        [PreserveSig] int Register();
        [PreserveSig] int RegisterPending();
        [PreserveSig] int Revoke();
        [PreserveSig] int OnNavigate();
        [PreserveSig] int OnActivated();
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            ref object pvarLoc, ref object pvarLocRoot,
            int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow（2 个占位）
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);
        // IShellBrowser（QueryActiveShellView 之前 10 个占位）
        [PreserveSig] int InsertMenusSB();
        [PreserveSig] int SetMenuSB();
        [PreserveSig] int RemoveMenusSB();
        [PreserveSig] int SetStatusTextSB();
        [PreserveSig] int EnableModelessSB();
        [PreserveSig] int TranslateAcceleratorSB();
        [PreserveSig] int BrowseObject();
        [PreserveSig] int GetViewStateStream();
        [PreserveSig] int GetControlWindow();
        [PreserveSig] int SendControlMsg();
        [PreserveSig] int QueryActiveShellView(
            [MarshalAs(UnmanagedType.Interface)] out object ppshv);
    }

    [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        // IFolderView（14 个占位）
        [PreserveSig] int GetCurrentViewMode();
        [PreserveSig] int SetCurrentViewMode();
        [PreserveSig] int GetFolder();
        [PreserveSig] int Item();
        [PreserveSig] int ItemCount();
        [PreserveSig] int Items();
        [PreserveSig] int GetSelectionMarkedItem();
        [PreserveSig] int GetFocusedItem();
        [PreserveSig] int GetItemPosition();
        [PreserveSig] int GetSpacing();
        [PreserveSig] int GetDefaultSpacing();
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem();
        [PreserveSig] int SelectAndPositionItems();
        // IFolderView2（SetCurrentFolderFlags 之前 7 个占位）
        [PreserveSig] int SetGroupBy();
        [PreserveSig] int GetGroupBy();
        [PreserveSig] int SetViewProperty();
        [PreserveSig] int GetViewProperty();
        [PreserveSig] int SetTileViewProperties();
        [PreserveSig] int SetExtendedTileViewProperties();
        [PreserveSig] int SetText();
        [PreserveSig] int SetCurrentFolderFlags(uint dwMask, uint dwFlags);
        [PreserveSig] int GetCurrentFolderFlags(out uint pdwFlags);
    }

    #endregion
}
