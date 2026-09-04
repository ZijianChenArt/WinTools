using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinTools;

/// <summary>
/// 监听桌面空白区域的单击，并让资源管理器最小化当前窗口。
/// 桌面图标、任务栏和普通窗口不会触发。
/// </summary>
public sealed class DesktopClickService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_QUIT = 0x0012;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;
    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_HITTEST = LVM_FIRST + 18;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private readonly object _sync = new();
    private readonly object _desktopStateSync = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private HookProc? _hookProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private POINT _downPoint;
    private bool _candidateClick;
    private int _dragThresholdX;
    private int _dragThresholdY;
    private bool _showingDesktop;

    public bool IsEnabled => _hookThread != null;

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (enabled)
                Start();
            else
                Stop();
        }
    }

    private void Start()
    {
        if (_hookThread != null) return;

        _ready.Reset();
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "WinTools Desktop Click Hook"
        };
        _hookThread.Start();
        _ready.Wait();
    }

    private void Stop()
    {
        if (_hookThread == null) return;

        if (_hookThreadId != 0)
            PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

        _hookThread.Join(2000);
        _hookThread = null;
        _hookThreadId = 0;
        _candidateClick = false;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _dragThresholdX = Math.Max(1, GetSystemMetrics(SM_CXDRAG));
        _dragThresholdY = Math.Max(1, GetSystemMetrics(SM_CYDRAG));
        _hookProc = HookCallback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        _ready.Set();

        if (_hook == IntPtr.Zero) return;
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    /// <summary>
    /// WH_MOUSE_LL 的回调是**全系统同步**的：这里每多花 1ms，所有应用的每一次点击就慢 1ms；
    /// 超过 <c>LowLevelHooksTimeout</c>（默认 300ms）Windows 还会直接丢弃这个钩子的事件。
    /// 所以回调里只记坐标，"是不是点在桌面空白处"这种要跨进程 OpenProcess /
    /// WriteProcessMemory / SendMessage 到 Explorer 的判定，一律扔到线程池里做。
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (message == WM_LBUTTONDOWN)
            {
                _downPoint = info.pt;
                _candidateClick = true;
            }
            else if (message == WM_LBUTTONUP && _candidateClick)
            {
                _candidateClick = false;
                var dx = Math.Abs(info.pt.X - _downPoint.X);
                var dy = Math.Abs(info.pt.Y - _downPoint.Y);
                if (dx < _dragThresholdX && dy < _dragThresholdY)
                {
                    var down = _downPoint;
                    var up = info.pt;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        // 按下和抬起都必须落在桌面空白处，避免把图标上的点击也当成"显示桌面"。
                        if (IsDesktopBlankArea(down) && IsDesktopBlankArea(up))
                            ToggleDesktopWindows();
                    });
                }
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDesktopBlankArea(POINT screenPoint)
    {
        var listView = WindowFromPoint(screenPoint);
        if (listView == IntPtr.Zero || !string.Equals(GetClassName(listView), "SysListView32", StringComparison.Ordinal))
            return false;

        var parent = GetParent(listView);
        if (parent == IntPtr.Zero || !string.Equals(GetClassName(parent), "SHELLDLL_DefView", StringComparison.Ordinal))
            return false;

        var clientPoint = screenPoint;
        if (!ScreenToClient(listView, ref clientPoint)) return false;
        return HitTestDesktopItem(listView, clientPoint) == -1;
    }

    /// <summary>跨进程调用桌面 ListView 的命中测试；失败时保守地视为非空白。</summary>
    private static int HitTestDesktopItem(IntPtr listView, POINT clientPoint)
    {
        GetWindowThreadProcessId(listView, out var processId);
        if (processId == 0) return int.MinValue;

        var process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
        if (process == IntPtr.Zero) return int.MinValue;

        var size = Marshal.SizeOf<LVHITTESTINFO>();
        var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (remote == IntPtr.Zero) return int.MinValue;

            var hit = new LVHITTESTINFO { pt = clientPoint, iItem = -1, iSubItem = -1, iGroup = -1 };
            Marshal.StructureToPtr(hit, local, false);
            if (!WriteProcessMemory(process, remote, local, (nuint)size, out _)) return int.MinValue;

            // 必须用带超时的版本：Explorer 卡住时 SendMessage 会无限期阻塞，
            // 而这条链路以前跑在鼠标钩子线程上，等于整个系统的鼠标输入一起卡死。
            if (SendMessageTimeout(listView, LVM_HITTEST, IntPtr.Zero, remote,
                    SMTO_ABORTIFHUNG, 300, out _) == IntPtr.Zero)
                return int.MinValue;
            if (!ReadProcessMemory(process, remote, local, (nuint)size, out _)) return int.MinValue;
            return Marshal.PtrToStructure<LVHITTESTINFO>(local).iItem;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    private void ToggleDesktopWindows()
    {
        lock (_desktopStateSync)
        {
            object? shell = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;
                shell = Activator.CreateInstance(shellType);
                shellType.InvokeMember(
                    _showingDesktop ? "UndoMinimizeALL" : "MinimizeAll",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    null);
                _showingDesktop = !_showingDesktop;
            }
            catch { /* Explorer 重启等瞬时状态下忽略此次点击，并保留当前切换状态。 */ }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                    Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync) Stop();
        _ready.Dispose();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var value = new StringBuilder(64);
        return GetClassName(hwnd, value, value.Capacity) > 0 ? value.ToString() : "";
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
