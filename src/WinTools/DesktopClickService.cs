using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 监听桌面空白区域的双击，并让资源管理器最小化当前窗口。
/// 桌面图标、任务栏和普通窗口不会触发。
/// </summary>
public sealed class DesktopClickService : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_QUIT = 0x0012;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;
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

    /// <summary>触发一次切换后的冷却时间，避免连点把窗口来回最小化 / 还原。</summary>
    private const uint ToggleCooldownMs = 600;

    private readonly object _sync = new();
    private readonly object _desktopStateSync = new();
    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hook;
    private HookProc? _hookProc;
    private readonly ManualResetEventSlim _ready = new(false);
    private int _dragThresholdX;
    private int _dragThresholdY;
    private int _doubleClickThresholdX;
    private int _doubleClickThresholdY;
    private uint _doubleClickTime;
    private bool _showingDesktop;

    // ↓ 以下字段只在钩子线程上读写，不需要同步。
    private POINT _downPoint;
    private uint _downTime;
    private ClickTarget _downTarget;
    private bool _candidateClick;
    private bool _downIsSecondClick;
    private POINT _firstDownPoint;
    private POINT _firstUpPoint;
    private uint _firstDownTime;
    private ClickTarget _firstDownTarget;
    private ClickTarget _firstUpTarget;
    private bool _hasFirstClick;
    private uint _lastToggleTime;

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
        ResetClickState();
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _dragThresholdX = Math.Max(1, GetSystemMetrics(SM_CXDRAG));
        _dragThresholdY = Math.Max(1, GetSystemMetrics(SM_CYDRAG));
        _doubleClickThresholdX = Math.Max(1, GetSystemMetrics(SM_CXDOUBLECLK));
        _doubleClickThresholdY = Math.Max(1, GetSystemMetrics(SM_CYDOUBLECLK));
        _doubleClickTime = GetDoubleClickTime();
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
    /// 所以回调里只做**不跨进程**的判定：<see cref="WindowFromPoint"/>、<c>GetClassName</c>、
    /// <c>GetForegroundWindow</c> 都在 win32k 内部完成，微秒级；而要 OpenProcess /
    /// WriteProcessMemory / SendMessage 到 Explorer 的图标命中测试，一律扔到线程池里做。
    /// <para>
    /// 但"点在哪个窗口上"必须在这里当场记下来：线程池跑到时可能已经过去几十上百毫秒，
    /// 那时窗口可能已经被关掉 / 从最大化还原 / 移走，同一个坐标就会穿到它背后的桌面上，
    /// 于是"在资源管理器里疯狂点击"也会被判成桌面双击。
    /// </para>
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (nCode >= 0 && message != WM_MOUSEMOVE)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 三指拖拽自己注入的按下 / 抬起不参与判定，否则两次三指轻点会被当成桌面双击。
            var synthetic = info.dwExtraInfo.ToInt64() == ThreeFingerDragService.SyntheticInputTag;

            switch (message)
            {
                case WM_LBUTTONDOWN when !synthetic:
                    OnLeftButtonDown(in info);
                    break;
                case WM_LBUTTONUP when !synthetic && _candidateClick:
                    OnLeftButtonUp(in info);
                    break;
                default:
                    // 中间插进右键 / 中键 / 滚轮 / 合成点击，两次点击就不再是"连续"的了。
                    ResetClickState();
                    break;
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void OnLeftButtonDown(in MSLLHOOKSTRUCT info)
    {
        if (_hasFirstClick && unchecked(info.time - _firstDownTime) > _doubleClickTime)
            _hasFirstClick = false;

        // 按 Windows 自己的双击语义判定：两次**按下**之间的间隔与位移都要在系统阈值内。
        // 旧实现比的是两次抬起的时间，比系统宽松，连点时更容易凑出"双击"。
        _downIsSecondClick = _hasFirstClick
            && Math.Abs(info.pt.X - _firstDownPoint.X) < _doubleClickThresholdX
            && Math.Abs(info.pt.Y - _firstDownPoint.Y) < _doubleClickThresholdY;

        _downPoint = info.pt;
        _downTime = info.time;
        _downTarget = CaptureTarget(info.pt);
        _candidateClick = true;
    }

    private void OnLeftButtonUp(in MSLLHOOKSTRUCT info)
    {
        _candidateClick = false;

        var down = _downPoint;
        var up = info.pt;
        if (Math.Abs(up.X - down.X) >= _dragThresholdX || Math.Abs(up.Y - down.Y) >= _dragThresholdY)
        {
            _hasFirstClick = false;  // 拖拽不算点击，也不能给下一次点击当"第一击"。
            return;
        }

        var upTarget = CaptureTarget(up);

        if (!_downIsSecondClick)
        {
            _firstDownPoint = down;
            _firstUpPoint = up;
            _firstDownTime = _downTime;
            _firstDownTarget = _downTarget;
            _firstUpTarget = upTarget;
            _hasFirstClick = true;
            return;
        }

        var firstDown = _firstDownPoint;
        var firstUp = _firstUpPoint;
        var firstDownTarget = _firstDownTarget;
        var firstUpTarget = _firstUpTarget;
        _hasFirstClick = false;

        if (unchecked(info.time - _lastToggleTime) < ToggleCooldownMs) return;

        // 两次点击的按下与抬起必须**同时命中同一个桌面窗口**。只比坐标是不够的：
        // 连点时窗口可能在两次点击之间被关掉或从最大化还原，坐标没变、下面的窗口却换了。
        var target = _downTarget;
        if (!target.IsDesktop
            || !target.SameWindow(in firstDownTarget)
            || !target.SameWindow(in firstUpTarget)
            || !target.SameWindow(in upTarget))
            return;

        // 点得到桌面，桌面就一定已经是前台窗口。资源管理器被模态对话框禁用时，
        // WindowFromPoint 会跳过禁用窗口、直接返回它背后的桌面 —— 这一条把那种穿透挡掉。
        if (!IsDesktopForeground())
        {
            var rejected = (cls: target.ClassName, fg: GetForegroundWindowClassName());
            ThreadPool.QueueUserWorkItem(static state => ErrorReporter.Log(
                "DesktopClick.Reject",
                $"坐标落在 {state.cls}，但前台窗口是 {state.fg}，按误触忽略。"), rejected, false);
            return;
        }

        _lastToggleTime = info.time;
        var points = new[] { firstDown, firstUp, down, up };
        ThreadPool.QueueUserWorkItem(_ => CompleteToggle(target, points));
    }

    private void ResetClickState()
    {
        _candidateClick = false;
        _hasFirstClick = false;
        _downIsSecondClick = false;
    }

    /// <summary>线程池上收尾：复核窗口没变，再做跨进程的图标命中测试，最后才切换。</summary>
    private void CompleteToggle(ClickTarget target, POINT[] points)
    {
        try
        {
            if (!ConfirmStillDesktopBlankArea(target, points)) return;
            ErrorReporter.Log("DesktopClick.Toggle", $"桌面空白处双击（{target.ClassName}），切换显示桌面。");
            ToggleDesktopWindows();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopClick.CompleteToggle", ex);
        }
    }

    /// <summary>
    /// 复核这四个点现在**仍然**落在按下时那个窗口上，并且那个窗口确实是 Explorer 的桌面。
    /// 布局在这几十毫秒里变过，就放弃这次切换。
    /// </summary>
    private static bool ConfirmStillDesktopBlankArea(ClickTarget target, POINT[] points)
    {
        foreach (var point in points)
            if (WindowFromPoint(point) != target.Hwnd) return false;

        if (!BelongsToShellDesktop(target.Hwnd)) return false;

        // 图标隐藏时（桌面分区会关掉系统的「显示桌面图标」）Explorer 会把 SysListView32
        // 一起藏掉，拿到的是 WorkerW / Progman / SHELLDLL_DefView。这时桌面上根本没有
        // 图标可点，落在桌面窗口上就等于落在空白处。少了这一条，开启桌面分区后
        // 「点击桌面返回」会整个失效（2026-09-07 实测）。
        return !target.NeedsIconHitTest || AllPointsMissIcons(target.Hwnd, points);
    }

    /// <summary>确认窗口属于 Explorer 的桌面实例，而不是恰好同名的别家窗口。</summary>
    private static bool BelongsToShellDesktop(IntPtr hwnd)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return false;

        GetWindowThreadProcessId(progman, out var shellProcessId);
        GetWindowThreadProcessId(hwnd, out var processId);
        return shellProcessId != 0 && processId == shellProcessId;
    }

    /// <summary>抓取某个屏幕坐标下的窗口快照，只用不跨进程的调用，可以在钩子里跑。</summary>
    private static ClickTarget CaptureTarget(POINT screenPoint)
    {
        var hwnd = WindowFromPoint(screenPoint);
        if (hwnd == IntPtr.Zero) return default;

        var className = GetClassName(hwnd);
        var isDesktop = className switch
        {
            "WorkerW" or "Progman" or "SHELLDLL_DefView" => true,
            "SysListView32" => string.Equals(GetClassName(GetParent(hwnd)), "SHELLDLL_DefView", StringComparison.Ordinal),
            _ => false,
        };
        return new ClickTarget(hwnd, className, isDesktop);
    }

    private static bool IsDesktopForeground() =>
        GetForegroundWindowClassName() is "WorkerW" or "Progman" or "SHELLDLL_DefView" or "SysListView32";

    private static string GetForegroundWindowClassName() => GetClassName(GetForegroundWindow());

    /// <summary>跨进程调用桌面 ListView 的命中测试；失败时保守地视为非空白。</summary>
    /// <remarks>四个点共用一次 OpenProcess / VirtualAllocEx，最差耗时从 4×300ms 降到 300ms。</remarks>
    private static bool AllPointsMissIcons(IntPtr listView, POINT[] screenPoints)
    {
        GetWindowThreadProcessId(listView, out var processId);
        if (processId == 0) return false;

        var process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
        if (process == IntPtr.Zero) return false;

        var size = Marshal.SizeOf<LVHITTESTINFO>();
        var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (remote == IntPtr.Zero) return false;

            foreach (var screenPoint in screenPoints)
            {
                var clientPoint = screenPoint;
                if (!ScreenToClient(listView, ref clientPoint)) return false;

                var hit = new LVHITTESTINFO { pt = clientPoint, iItem = -1, iSubItem = -1, iGroup = -1 };
                Marshal.StructureToPtr(hit, local, false);
                if (!WriteProcessMemory(process, remote, local, (nuint)size, out _)) return false;

                // 必须用带超时的版本：Explorer 卡住时 SendMessage 会无限期阻塞，
                // 而这条链路以前跑在鼠标钩子线程上，等于整个系统的鼠标输入一起卡死。
                if (SendMessageTimeout(listView, LVM_HITTEST, IntPtr.Zero, remote,
                        SMTO_ABORTIFHUNG, 300, out _) == IntPtr.Zero)
                    return false;
                if (!ReadProcessMemory(process, remote, local, (nuint)size, out _)) return false;
                if (Marshal.PtrToStructure<LVHITTESTINFO>(local).iItem != -1) return false;
            }

            return true;
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
        if (hwnd == IntPtr.Zero) return "";
        var value = new StringBuilder(64);
        return GetClassName(hwnd, value, value.Capacity) > 0 ? value.ToString() : "";
    }

    /// <summary>一次点击落点的快照：句柄 + 类名，在钩子里当场取，之后只做比对。</summary>
    private readonly struct ClickTarget(IntPtr hwnd, string className, bool isDesktop)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public string ClassName { get; } = className;

        /// <summary>落点是不是 Explorer 桌面本身（含图标列表）。</summary>
        public bool IsDesktop { get; } = isDesktop;

        /// <summary>只有图标列表可见时才需要跨进程问"点到图标没有"。</summary>
        public bool NeedsIconHitTest => ClassName is "SysListView32";

        public bool SameWindow(in ClickTarget other) => Hwnd != IntPtr.Zero && Hwnd == other.Hwnd;
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
    private static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
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
