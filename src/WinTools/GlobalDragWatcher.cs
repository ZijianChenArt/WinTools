using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools;

/// <summary>拖拽开始事件参数：按下点用于来源判断，当前点用于悬浮窗定位。</summary>
public sealed class DragStartedArgs : EventArgs
{
    /// <summary>鼠标按下时的屏幕坐标（用于判断拖拽来源）。</summary>
    public int X { get; }
    public int Y { get; }
    /// <summary>超过拖拽阈值时的屏幕坐标（用于悬浮窗定位）。</summary>
    public int CurrentX { get; }
    public int CurrentY { get; }
    /// <summary>
    /// 拖拽刚开始（位移仅约阈值像素、窗口尚未移走）时按下点下方的窗口句柄。
    /// 用于可靠判断拖拽来源，避免延迟判断时窗口已移走导致按下点坐标命中桌面/后方窗口而误判。
    /// </summary>
    public IntPtr SourceHwnd { get; }
    public DragStartedArgs(int x, int y, int currentX, int currentY, IntPtr sourceHwnd)
    {
        X = x;
        Y = y;
        CurrentX = currentX;
        CurrentY = currentY;
        SourceHwnd = sourceHwnd;
    }
}

/// <summary>
/// 全局低阶鼠标钩子：检测全局拖拽开始与结束。
/// 钩子安装在独立后台线程，不会阻塞 UI 线程的消息循环。
/// DragStarted 携带鼠标按下时的坐标，供调用方判断是否来自文件管理器。
/// </summary>
public sealed class GlobalDragWatcher : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_QUIT = 0x0012;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    private IntPtr _hook;
    private HookProc? _proc;
    private bool _isDown;
    private bool _isDragging;
    private bool _dragNotified;
    private int _dragDetectionClaimed;
    private POINT _downPoint;
    private IntPtr _downSourceHwnd;
    private Timer? _threeFingerMovePollTimer;

    /// <summary>返回 true 时忽略此次拖拽（不触发 DragStarted/DragEnded）。</summary>
    public Func<int, int, bool>? ShouldIgnoreDragAt { get; set; }
    private int _dragThresholdX;
    private int _dragThresholdY;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>拖拽开始（携带按下坐标）。</summary>
    public event EventHandler<DragStartedArgs>? DragStarted;

    /// <summary>拖拽结束。</summary>
    public event EventHandler? DragEnded;

    public void Start()
    {
        if (_hookThread != null) return;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "WinTools Mouse Hook"
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
        _isDown = false;
        _isDragging = false;
        _dragNotified = false;
        _dragDetectionClaimed = 0;
        _threeFingerMovePollTimer?.Dispose();
        _threeFingerMovePollTimer = null;
        _ready.Reset();
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        _dragThresholdX = GetSystemMetrics(SM_CXDRAG);
        _dragThresholdY = GetSystemMetrics(SM_CYDRAG);
        _proc = HookCallback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);

        _ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 三指拖拽通过 SendInput 注入的按键也须参与检测，否则无法触发悬浮暂存。
            // WinTools 自身窗口上的拖动由 ShouldIgnoreDragAt 过滤。
            var msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN)
            {
                _isDown = true;
                _isDragging = false;
                _dragNotified = false;
                Volatile.Write(ref _dragDetectionClaimed, 0);
                _downPoint = info.pt;
                _downSourceHwnd = WindowFromPoint(_downPoint);

                // SetCursorPos-based three-finger motion is intentionally synchronous and may not
                // generate a low-level WM_MOUSEMOVE for every update. Poll only while our tagged
                // synthetic button is down so floating drag targets still detect the gesture.
                if (info.dwExtraInfo.ToInt64() == ThreeFingerDragService.SyntheticInputTag)
                {
                    _threeFingerMovePollTimer ??= new Timer(_ => PollThreeFingerMove(), null,
                        Timeout.Infinite, Timeout.Infinite);
                    _threeFingerMovePollTimer.Change(0, 8);
                }
            }
            else if (msg == WM_MOUSEMOVE && _isDown && !_isDragging)
            {
                TryDetectDrag(info.pt);
            }
            else if (msg == WM_LBUTTONUP)
            {
                _threeFingerMovePollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                if (_isDragging && _dragNotified)
                    DragEnded?.Invoke(this, EventArgs.Empty);
                _isDown = false;
                _isDragging = false;
                _dragNotified = false;
                Volatile.Write(ref _dragDetectionClaimed, 0);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void PollThreeFingerMove()
    {
        if (!_isDown || _isDragging) return;
        if (GetCursorPos(out var point))
            TryDetectDrag(point);
    }

    private void TryDetectDrag(POINT currentPoint)
    {
        if (!_isDown || _isDragging) return;
        var dx = Math.Abs(currentPoint.X - _downPoint.X);
        var dy = Math.Abs(currentPoint.Y - _downPoint.Y);
        if (dx < _dragThresholdX && dy < _dragThresholdY) return;
        if (Interlocked.CompareExchange(ref _dragDetectionClaimed, 1, 0) != 0) return;

        _isDragging = true;
        if (ShouldIgnoreDragAt?.Invoke(_downPoint.X, _downPoint.Y) == true) return;

        _dragNotified = true;
        DragStarted?.Invoke(this, new DragStartedArgs(
            _downPoint.X, _downPoint.Y,
            currentPoint.X, currentPoint.Y,
            _downSourceHwnd));
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

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
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
