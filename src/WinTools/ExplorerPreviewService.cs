using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 资源管理器里的空格预览（仿 QuickLook）：资源管理器文件列表有焦点时按空格，
/// 读取当前选中的项目，交给 <see cref="FilePreviewService"/> 预览。
/// </summary>
/// <remarks>
/// 与 <see cref="DesktopClickService"/> 一样分两段，因为 WH_KEYBOARD_LL 回调是全系统同步的，
/// 超过 <c>LowLevelHooksTimeout</c> 系统会直接丢弃这个钩子：
/// <list type="number">
/// <item>钩子回调只做进程内的判断：前台是不是资源管理器窗口、焦点是不是文件列表、有没有修饰键。
/// 命中就吞掉这次空格（包括之后的抬起），把窗口句柄交给工作线程。</item>
/// <item>工作线程（STA）通过 <c>IShellWindows</c> 找到这个窗口，再拿到 <c>IShellBrowser</c> → <c>IShellView</c> →
/// <c>IFolderView</c>，读出选中项，最后切回 UI 线程显示预览。这些全都是跨进程调用，绝不能放在钩子里做。</item>
/// </list>
/// 吞掉空格后如果没有选中项，这次按键就什么也不做（资源管理器里空格本来也没有有用的默认行为）。
/// </remarks>
internal sealed class ExplorerPreviewService
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;
    private const int VK_SPACE = 0x20;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;

    private readonly DispatcherQueue _dispatcher;
    private readonly BlockingCollection<IntPtr> _selectionQueue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private HookProc? _hookProc;
    private IntPtr _hook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Thread? _workerThread;

    /// <summary>空格按下时已经被吞掉：抬起要吞掉对应的 KeyUp，按住不放产生的自动重复也一并吞掉。</summary>
    private bool _swallowingSpace;

    /// <summary>设置页里的「资源管理器」开关。关闭时钩子原样放行所有按键，不做任何判断。</summary>
    public volatile bool Enabled;

    /// <param name="dispatcher">UI 线程的队列，工作线程靠它把结果切回 UI 线程。</param>
    public ExplorerPreviewService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_hookThread != null) return;

        _workerThread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "WinTools Explorer Preview Worker",
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();

        _ready.Reset();
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "WinTools Explorer Preview Hook",
        };
        _hookThread.Start();
        _ready.Wait();
    }

    public void Stop()
    {
        if (_hookThread == null) return;
        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(2000);
        _hookThread = null;
        _hookThreadId = 0;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        _ready.Set();

        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    /// <summary>只在这里做进程内判断；命中后把跨进程的读取交给工作线程。</summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && Marshal.ReadInt32(lParam) == VK_SPACE)
        {
            var message = wParam.ToInt32();
            if (message is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (TryClaimSpaceDown()) return (IntPtr)1;
            }
            else if (message is (WM_KEYUP or WM_SYSKEYUP) && _swallowingSpace)
            {
                _swallowingSpace = false;
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <returns>这次空格按下是否被本功能接管（接管后吞掉按键）。</returns>
    private bool TryClaimSpaceDown()
    {
        // 按住不放的自动重复：已经接管过，继续吞掉，不再重复触发预览。
        if (_swallowingSpace) return true;
        if (IsModifierDown()) return false;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsExplorerWindow(foreground)) return false;
        if (!IsFileListFocused(foreground)) return false;

        _swallowingSpace = true;
        // 先在 UI 线程弹出加载中的窗口，让用户马上看到反应；选中项在工作线程读取，读完再填进窗口。
        _dispatcher.TryEnqueue(FilePreviewService.ShowPending);
        _selectionQueue.Add(foreground);
        return true;
    }

    private static bool IsModifierDown() =>
        IsKeyDown(VK_SHIFT) || IsKeyDown(VK_CONTROL) || IsKeyDown(VK_MENU);

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>Windows 10 / 11 的资源管理器窗口类名（标签页和旧式窗口都是这两个）。</summary>
    private static bool IsExplorerWindow(IntPtr hwnd)
    {
        var name = ClassName(hwnd);
        return name == "CabinetWClass" || name == "ExploreWClass";
    }

    /// <summary>
    /// 焦点在文件列表（DirectUIHWND）时才接管。重命名框（Edit）、地址栏、搜索框都不是这个类，自然不会被抢走空格。
    /// </summary>
    private static bool IsFileListFocused(IntPtr foreground)
    {
        var threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == IntPtr.Zero) return false;
        return ClassName(info.hwndFocus) == "DirectUIHWND";
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private void WorkerMain()
    {
        foreach (var window in _selectionQueue.GetConsumingEnumerable())
        {
            // 无论读取成功与否，都要回到 UI 线程把结果交给窗口：空结果会收起加载中的窗口。
            IReadOnlyList<string> paths = Array.Empty<string>();
            try
            {
                paths = ExplorerSelection.ReadSelectedPaths(window);
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("ExplorerPreview.Read", ex);
            }

            var selection = paths;
            _dispatcher.TryEnqueue(() => FilePreviewService.ShowSelection(selection));
        }
    }

    #region Win32

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    #endregion
}

/// <summary>
/// 通过 Shell 读取资源管理器窗口里当前选中的文件系统项目。
/// 全部是后期绑定 / 手写的 COM 接口：<c>IShellWindows</c> 不在 Windows SDK 的元数据里，只能走 IDispatch。
/// </summary>
/// <remarks>
/// 接口的虚表顺序必须与系统定义完全一致（IShellBrowser 的 QueryActiveShellView、IFolderView 的 Items、
/// IShellItemArray 的 GetCount / GetItemAt、IShellItem 的 GetDisplayName 都只靠位置调用），顺序错了会直接崩溃。
/// </remarks>
internal static class ExplorerSelection
{
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    // 下面三个要以 ref 传给 COM 方法，所以不能是 readonly。
    private static Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static Guid IidShellItemArray = new("B63EA76D-1F85-456F-A19C-48159EFA858B");

    private const uint SVGIO_SELECTION = 1;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>读取 <paramref name="explorerWindow"/>（资源管理器顶层窗口句柄）里选中的文件 / 文件夹完整路径。</summary>
    public static List<string> ReadSelectedPaths(IntPtr explorerWindow)
    {
        var paths = new List<string>();
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid);
        if (type == null) return paths;

        var shellWindows = Activator.CreateInstance(type);
        if (shellWindows == null) return paths;

        var count = (int)type.InvokeMember("Count", BindingFlags.GetProperty, null, shellWindows, null)!;
        for (var i = 0; i < count; i++)
        {
            object? window;
            long hwnd;
            try
            {
                window = type.InvokeMember("Item", BindingFlags.InvokeMethod, null, shellWindows, new object[] { i });
                if (window == null) continue;
                hwnd = (long)window.GetType().InvokeMember("HWND", BindingFlags.GetProperty, null, window, null)!;
            }
            catch
            {
                // 有些 ShellWindows 项不是资源管理器（例如 IE 窗口），拿不到 HWND 就跳过。
                continue;
            }

            if ((IntPtr)hwnd == explorerWindow)
            {
                paths.AddRange(ReadSelectionFromBrowser(window));
                break;
            }
        }

        return paths;
    }

    private static List<string> ReadSelectionFromBrowser(object window)
    {
        var paths = new List<string>();

        // IWebBrowser2 → IServiceProvider → IShellBrowser → IShellView → IFolderView → IShellItemArray
        var services = (IServiceProviderCom)window;
        services.QueryService(ref SidTopLevelBrowser, ref IidShellBrowser, out var browserObject);
        var browser = (IShellBrowserCom)browserObject;

        browser.QueryActiveShellView(out var shellView);
        var folderView = (IFolderViewCom)shellView;

        folderView.Items(SVGIO_SELECTION, ref IidShellItemArray, out var arrayObject);
        var items = (IShellItemArrayCom)arrayObject;

        items.GetCount(out var itemCount);
        for (uint i = 0; i < itemCount; i++)
        {
            try
            {
                items.GetItemAt(i, out var item);
                item.GetDisplayName(SIGDN_FILESYSPATH, out var namePtr);
                var path = Marshal.PtrToStringUni(namePtr);
                Marshal.FreeCoTaskMem(namePtr);

                // 虚拟项（此电脑、回收站等）没有文件系统路径，跳过；只预览真实存在的文件和文件夹。
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                    paths.Add(path);
            }
            catch (Exception ex)
            {
                ErrorReporter.Log("ExplorerSelection.Item", ex);
            }
        }

        return paths;
    }

    #region COM 接口（按系统定义的虚表顺序声明）

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProviderCom
    {
        // 用 object 承接返回值，由运行时负责引用计数，不手动 Release。
        void QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214E2-0000-0000-C000-000000000046")]
    private interface IShellBrowserCom
    {
        // IOleWindow
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);

        // IShellBrowser 中排在 QueryActiveShellView 之前的方法；这里只需要占位，不调用。
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwnd);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);

        void QueryActiveShellView(out IShellViewCom ppshv);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214E3-0000-0000-C000-000000000046")]
    private interface IShellViewCom
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    private interface IFolderViewCom
    {
        void GetCurrentViewMode(out uint pViewMode);
        void SetCurrentViewMode(uint ViewMode);
        void GetFolder(ref Guid riid, out IntPtr ppv);
        void Item(int iItemIndex, out IntPtr ppidl);
        void ItemCount(uint uFlags, out int pcItems);
        void Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    private interface IShellItemArrayCom
    {
        void BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int dwAttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItemCom ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    private interface IShellItemCom
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItemCom ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItemCom psi, uint hint, out int piOrder);
    }

    #endregion
}
