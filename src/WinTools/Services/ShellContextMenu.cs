using System;
using System.Runtime.InteropServices;

namespace WinTools.Services;

/// <summary>显示文件系统项或 Shell 虚拟项自己的原生上下文菜单。</summary>
internal static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_MENUCHAR = 0x0120;
    private const nuint SubclassId = 0x5754434D; // "WTCM"
    private static readonly SubclassProc SubclassCallback = ForwardMenuMessage;

    public static bool Show(string parsingName, IntPtr owner)
    {
        IntPtr absolutePidl = IntPtr.Zero;
        IntPtr parentPointer = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        object? parentObject = null;
        object? contextObject = null;
        GCHandle stateHandle = default;
        var subclassInstalled = false;

        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out absolutePidl, 0, out _) != 0
                || absolutePidl == IntPtr.Zero)
                return false;

            var shellFolderId = typeof(IShellFolder).GUID;
            if (SHBindToParent(absolutePidl, ref shellFolderId, out parentPointer, out var childPidl) != 0
                || parentPointer == IntPtr.Zero || childPidl == IntPtr.Zero)
                return false;

            parentObject = Marshal.GetObjectForIUnknown(parentPointer);
            var parent = (IShellFolder)parentObject;
            var children = new[] { childPidl };
            var contextMenuId = typeof(IContextMenu).GUID;
            if (parent.GetUIObjectOf(owner, 1, children, ref contextMenuId, IntPtr.Zero, out var contextPointer) != 0
                || contextPointer == IntPtr.Zero)
                return false;

            try { contextObject = Marshal.GetObjectForIUnknown(contextPointer); }
            finally { Marshal.Release(contextPointer); }
            var contextMenu = (IContextMenu)contextObject;

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero || contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL) < 0)
                return false;

            var state = new MenuState(contextObject);
            stateHandle = GCHandle.Alloc(state);
            subclassInstalled = SetWindowSubclass(owner, SubclassCallback, SubclassId,
                (nuint)GCHandle.ToIntPtr(stateHandle));

            GetCursorPos(out var point);
            SetForegroundWindow(owner);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                point.X, point.Y, owner, IntPtr.Zero);
            if (command == 0) return true;

            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE,
                hwnd = owner,
                lpVerb = new IntPtr(command - 1),
                lpVerbW = new IntPtr(command - 1),
                nShow = 1,
            };
            return contextMenu.InvokeCommand(ref invoke) >= 0;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellContextMenu.Show({parsingName})", ex);
            return false;
        }
        finally
        {
            if (subclassInstalled) RemoveWindowSubclass(owner, SubclassCallback, SubclassId);
            if (stateHandle.IsAllocated) stateHandle.Free();
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (contextObject != null && Marshal.IsComObject(contextObject)) Marshal.FinalReleaseComObject(contextObject);
            if (parentObject != null && Marshal.IsComObject(parentObject)) Marshal.FinalReleaseComObject(parentObject);
            if (parentPointer != IntPtr.Zero) Marshal.Release(parentPointer);
            if (absolutePidl != IntPtr.Zero) Marshal.FreeCoTaskMem(absolutePidl);
        }
    }

    private static IntPtr ForwardMenuMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        nuint id, nuint reference)
    {
        if (message is WM_DRAWITEM or WM_MEASUREITEM or WM_INITMENUPOPUP or WM_MENUCHAR)
        {
            var handle = GCHandle.FromIntPtr((IntPtr)reference);
            if (handle.Target is MenuState state)
            {
                if (state.ContextMenu3 != null
                    && state.ContextMenu3.HandleMenuMsg2(message, wParam, lParam, out var result) >= 0)
                    return result;
                if (state.ContextMenu2 != null && state.ContextMenu2.HandleMenuMsg(message, wParam, lParam) >= 0)
                    return IntPtr.Zero;
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private sealed class MenuState
    {
        public MenuState(object context)
        {
            ContextMenu3 = context as IContextMenu3;
            ContextMenu2 = context as IContextMenu2;
        }
        public IContextMenu2? ContextMenu2 { get; }
        public IContextMenu3? ContextMenu3 { get; }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name,
            ref uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindContext, ref Guid riid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, ref Guid riid,
            IntPtr reserved, out IntPtr result);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags, out IntPtr newPidl);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, IntPtr reserved, IntPtr name, uint maxName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, IntPtr reserved, IntPtr name, uint maxName);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint firstCommand, uint lastCommand, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX command);
        [PreserveSig] int GetCommandString(nuint command, uint flags, IntPtr reserved, IntPtr name, uint maxName);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl,
        uint attributesIn, out uint attributesOut);
    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr parent, out IntPtr childPidl);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint referenceData);
    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
