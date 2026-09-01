using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools;

/// <summary>在独立线程中创建隐藏窗口，专门接收触控板 Raw Input。</summary>
internal sealed class TouchpadInputHost : IDisposable
{
    private const int WM_CLOSE = 0x0010;
    private const int WM_DESTROY = 0x0002;

    private readonly ThreeFingerDragService _service = new();
    private readonly ManualResetEventSlim _readyEvent = new(false);
    private readonly string _className = $"WinTools.TouchpadInputHost.{Guid.NewGuid():N}";
    private Thread? _thread;
    private WndProc? _wndProc;
    private IntPtr _hwnd;
    private Exception? _startupException;
    private bool _initialEnabled;
    private Timer? _diagnosticTimer;
    private string? _lastDiagnosticSnapshot;
    private readonly string _diagnosticPath = Path.Combine(AppContext.BaseDirectory, "three_finger_diagnostics.log");
    private readonly string _calibrationPath = Path.Combine(AppContext.BaseDirectory, "three_finger_calibration.csv");

    public bool HasTouchpad => _service.HasTouchpad;

    public void ConfigurePointerCurve(double lowGain, double highGain, double accelerationStart, double accelerationEnd) =>
        _service.ConfigurePointerCurve(lowGain, highGain, accelerationStart, accelerationEnd);

    public void BeginCalibration(bool dragEnabled)
    {
        if (_thread == null)
            Start(dragEnabled);
        _service.BeginCalibration();
    }

    public TouchpadCalibrationResult CompleteCalibration() => _service.CompleteCalibration();

    public bool IsCalibrationFingerDown => _service.IsCalibrationFingerDown;
    public int CalibrationCapturedFrameCount => _service.CalibrationCapturedFrameCount;

    public void Start(bool enabled)
    {
        if (_thread != null)
        {
            _service.SetEnabled(enabled);
            return;
        }

        _initialEnabled = enabled;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "WinTools Touchpad Input",
            // 略高于普通后台任务即可，避免抢占 WinUI 与桌面合成线程。
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        _readyEvent.Wait();

        if (_startupException != null)
            throw new InvalidOperationException("无法启动触控板输入线程。", _startupException);
    }

    public void SetEnabled(bool enabled)
    {
        // 关闭状态不创建 Raw Input 窗口和高优先级后台线程。
        if (!enabled && _thread == null)
            return;

        if (_thread == null)
        {
            Start(enabled);
            return;
        }

        _service.SetEnabled(enabled);
    }

    public void Dispose()
    {
        _diagnosticTimer?.Dispose();
        _diagnosticTimer = null;
        FlushCalibrationRecords();
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        if (_thread != null && _thread.IsAlive)
            _thread.Join(2000);

        _readyEvent.Dispose();
        _service.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            _wndProc = WindowProc;
            var instance = GetModuleHandle(null);
            var windowClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className
            };

            if (RegisterClass(ref windowClass) == 0)
                throw new InvalidOperationException("注册触控板输入窗口类失败。");

            _hwnd = CreateWindowEx(
                0,
                _className,
                _className,
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException("创建触控板输入窗口失败。");

            _service.Initialize(_hwnd, _initialEnabled);
            StartDiagnosticLogging();
        }
        catch (Exception ex)
        {
            _startupException = ex;
            AppendDiagnostic($"startupError={ex.GetType().Name}: {ex.Message}");
            _readyEvent.Set();
            return;
        }

        _readyEvent.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    [Conditional("DEBUG")]
    private void StartDiagnosticLogging()
    {
        AppendDiagnostic($"sessionStart pid={Environment.ProcessId}");
        try
        {
            File.WriteAllText(_calibrationPath,
                "sequence,stopwatch_ticks,contacts,raw_center_x,raw_center_y,cursor_x,cursor_y" + Environment.NewLine);
        }
        catch { /* Calibration logging must never affect input handling. */ }
        _diagnosticTimer = new Timer(_ =>
        {
            try
            {
                FlushCalibrationRecords();
                var snapshot = _service.GetDiagnosticsSnapshot();
                if (string.Equals(snapshot, _lastDiagnosticSnapshot, StringComparison.Ordinal)) return;
                _lastDiagnosticSnapshot = snapshot;
                AppendDiagnostic(snapshot);
            }
            catch { /* 诊断不能影响输入线程。 */ }
        }, null, 0, 1000);
    }

    [Conditional("DEBUG")]
    private void FlushCalibrationRecords()
    {
        try
        {
            var records = _service.DrainCalibrationRecords();
            if (records.Length > 0)
                File.AppendAllLines(_calibrationPath, records);
        }
        catch { /* Calibration logging must never affect input handling. */ }
    }

    [Conditional("DEBUG")]
    private void AppendDiagnostic(string message)
    {
        try
        {
            File.AppendAllText(_diagnosticPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* 便携目录不可写时忽略。 */ }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_service.HandleWindowMessage(msg, wParam, lParam))
            return IntPtr.Zero;

        if (msg == WM_CLOSE)
        {
            DestroyWindow(hwnd);
            return IntPtr.Zero;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
