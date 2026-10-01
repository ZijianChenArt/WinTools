using System;
using System.Runtime.InteropServices;

namespace WinTools.Services;

/// <summary>显示文件系统项或 Shell 虚拟项自己的原生上下文菜单。</summary>
internal static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;
    private const uint GCS_VERBW = 0x00000004;
    private const uint MF_BYPOSITION = 0x00000400;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_MEASUREITEM = 0x002C;
    private const uint WM_INITMENUPOPUP = 0x0117;
    private const uint WM_MENUCHAR = 0x0120;
    private const nuint SubclassId = 0x5754434D; // "WTCM"
    private static readonly SubclassProc SubclassCallback = ForwardMenuMessage;

    /// <param name="screenPoint">菜单左上角（屏幕物理像素）。不给就弹在光标处；键盘呼出时要给图标的位置。</param>
    public static bool Show(string parsingName, IntPtr owner, (int X, int Y)? screenPoint = null)
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

            var point = default(POINT);
            if (screenPoint is { } at) { point.X = at.X; point.Y = at.Y; }
            else GetCursorPos(out point);
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

    /// <summary>
    /// 只创建**一个**系统右键菜单扩展（例如「固定到“开始”」「共享」），读出它此刻会提供的菜单项。
    /// </summary>
    /// <remarks>
    /// 不能走 <see cref="Show"/> 那条路先建整套系统菜单再挑：那会把所有第三方扩展都加载一遍，
    /// 2026-09-24 实测桌面上一个 .lnk 冷启动 1.6 秒、之后每次仍要约 0.95 秒，右键会明显卡一下。
    /// 单独创建处理器首次约 70–90ms，之后 1–15ms；而且菜单文字就是它当下给的
    /// （已固定时自然变成「从“开始”菜单取消固定」），不用自己猜状态。
    /// 返回的对象要在菜单项执行完、菜单关闭后 <c>Dispose</c>。
    /// </remarks>
    public static HandlerMenu? QueryHandler(Guid handlerClsid, string parsingName, IntPtr owner)
    {
        IntPtr dataObject = IntPtr.Zero;
        object? handler = null;
        var menu = IntPtr.Zero;
        try
        {
            dataObject = CreateDataObject(parsingName, owner);
            if (dataObject == IntPtr.Zero) return null;

            var type = Type.GetTypeFromCLSID(handlerClsid, throwOnError: false);
            handler = type == null ? null : Activator.CreateInstance(type);
            if (handler is not IShellExtInit init || handler is not IContextMenu contextMenu) return null;
            if (init.Initialize(IntPtr.Zero, dataObject, IntPtr.Zero) != 0) return null;

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero || contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL) < 0) return null;

            var entries = new System.Collections.Generic.List<HandlerMenuEntry>();
            var count = GetMenuItemCount(menu);
            for (var index = 0; index < count; index++)
            {
                var id = GetMenuItemID(menu, index);
                if (id == uint.MaxValue || id == 0) continue;   // 子菜单或分隔线
                var text = new System.Text.StringBuilder(256);
                if (GetMenuStringW(menu, (uint)index, text, text.Capacity, MF_BYPOSITION) <= 0) continue;
                entries.Add(new HandlerMenuEntry(id - 1, GetVerb(contextMenu, id - 1), StripAccessKey(text.ToString())));
            }

            var result = new HandlerMenu(handler, menu, entries);
            handler = null;   // 所有权交给 HandlerMenu
            menu = IntPtr.Zero;
            return result;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellContextMenu.QueryHandler({handlerClsid}, {parsingName})", ex);
            return null;
        }
        finally
        {
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (handler != null && Marshal.IsComObject(handler)) Marshal.FinalReleaseComObject(handler);
            if (dataObject != IntPtr.Zero) Marshal.Release(dataObject);
        }
    }

    /// <summary>
    /// 该项目的 Shell 数据对象（<c>IDataObject*</c>，已 AddRef，调用方负责 <c>Marshal.Release</c>）。
    /// 扩展处理器初始化、「打开方式」里的应用启动都要用它。失败返回 <c>IntPtr.Zero</c>。
    /// </summary>
    internal static IntPtr CreateDataObject(string parsingName, IntPtr owner)
    {
        IntPtr absolutePidl = IntPtr.Zero;
        IntPtr parentPointer = IntPtr.Zero;
        object? parentObject = null;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out absolutePidl, 0, out _) != 0
                || absolutePidl == IntPtr.Zero)
                return IntPtr.Zero;
            var shellFolderId = typeof(IShellFolder).GUID;
            if (SHBindToParent(absolutePidl, ref shellFolderId, out parentPointer, out var childPidl) != 0
                || parentPointer == IntPtr.Zero || childPidl == IntPtr.Zero)
                return IntPtr.Zero;
            parentObject = Marshal.GetObjectForIUnknown(parentPointer);
            var dataObjectId = new Guid("0000010e-0000-0000-C000-000000000046");
            return ((IShellFolder)parentObject).GetUIObjectOf(owner, 1, new[] { childPidl }, ref dataObjectId,
                    IntPtr.Zero, out var dataObject) == 0 ? dataObject : IntPtr.Zero;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellContextMenu.CreateDataObject({parsingName})", ex);
            return IntPtr.Zero;
        }
        finally
        {
            if (parentObject != null && Marshal.IsComObject(parentObject)) Marshal.FinalReleaseComObject(parentObject);
            if (parentPointer != IntPtr.Zero) Marshal.Release(parentPointer);
            if (absolutePidl != IntPtr.Zero) Marshal.FreeCoTaskMem(absolutePidl);
        }
    }

    /// <summary>菜单项的规范动作名（例如 PinToStartScreen），拿不到时返回空串。</summary>
    private static string GetVerb(IContextMenu contextMenu, uint offset)
    {
        const int capacity = 256;
        var buffer = Marshal.AllocCoTaskMem(capacity * sizeof(char));
        try
        {
            return contextMenu.GetCommandString(offset, GCS_VERBW, IntPtr.Zero, buffer, capacity) == 0
                ? Marshal.PtrToStringUni(buffer) ?? string.Empty
                : string.Empty;
        }
        catch { return string.Empty; }
        finally { Marshal.FreeCoTaskMem(buffer); }
    }

    /// <summary>「固定到“开始”(&amp;P)」→「固定到“开始”」：新式菜单不显示访问键。</summary>
    private static string StripAccessKey(string text)
    {
        var withoutSuffix = System.Text.RegularExpressions.Regex.Replace(text, @"\(&.\)", string.Empty);
        return withoutSuffix.Replace("&", string.Empty).Trim();
    }

    internal readonly record struct HandlerMenuEntry(uint Offset, string Verb, string Text);

    /// <summary>单个扩展处理器查询出的菜单项，以及执行它们的方法。</summary>
    internal sealed class HandlerMenu : IDisposable
    {
        private object? _handler;
        private IntPtr _menu;

        internal HandlerMenu(object handler, IntPtr menu, System.Collections.Generic.IReadOnlyList<HandlerMenuEntry> entries)
        {
            _handler = handler;
            _menu = menu;
            Entries = entries;
        }

        public System.Collections.Generic.IReadOnlyList<HandlerMenuEntry> Entries { get; }

        /// <param name="screenPoint">给「共享」这类会弹窗的动作定位（屏幕物理像素）。</param>
        public bool Invoke(HandlerMenuEntry entry, IntPtr owner, (int X, int Y)? screenPoint = null)
        {
            if (_handler is not IContextMenu contextMenu) return false;
            try
            {
                var invoke = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask = CMIC_MASK_UNICODE | (screenPoint.HasValue ? CMIC_MASK_PTINVOKE : 0),
                    hwnd = owner,
                    lpVerb = new IntPtr(entry.Offset),
                    lpVerbW = new IntPtr(entry.Offset),
                    nShow = 1,
                    ptInvoke = screenPoint is { } at ? new POINT { X = at.X, Y = at.Y } : default,
                };
                return contextMenu.InvokeCommand(ref invoke) >= 0;
            }
            catch (Exception ex)
            {
                ErrorReporter.Log($"ShellContextMenu.HandlerMenu.Invoke({entry.Verb})", ex);
                return false;
            }
        }

        public void Dispose()
        {
            if (_menu != IntPtr.Zero) { DestroyMenu(_menu); _menu = IntPtr.Zero; }
            if (_handler != null && Marshal.IsComObject(_handler)) Marshal.FinalReleaseComObject(_handler);
            _handler = null;
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

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E8-0000-0000-C000-000000000046")]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr folderPidl, IntPtr dataObject, IntPtr progIdKey);
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
    private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint item, System.Text.StringBuilder text, int maxCount, uint flags);
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
