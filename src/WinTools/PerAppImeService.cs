using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools;

/// <summary>
/// 监听前景窗口切换，按配置自动切换输入法中/英文模式。
/// 在独立后台线程安装 SetWinEventHook，不阻塞 UI。
/// </summary>
public sealed class PerAppImeService : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int WM_CLOSE = 0x0010;
    private const int WM_DESTROY = 0x0002;

    private readonly object _sync = new();
    private readonly object _lifecycleSync = new();
    private readonly HashSet<IntPtr> _excludedHwnds = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = $"WinTools.PerAppIme.{Guid.NewGuid():N}";

    private Thread? _thread;
    private WndProc? _wndProc;
    private WinEventProc? _winEventProc;
    private IntPtr _hwnd;
    private IntPtr _hook;
    private volatile bool _enabled;
    private bool _disposed;
    private List<ImeRuleEntry> _rules = new();
    private string? _lastProcessName;

    public void SetEnabled(bool enabled)
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;

            _enabled = enabled;
            if (enabled && _thread == null)
                StartThread();
            else if (!enabled)
                StopThread();
        }

        if (!enabled)
            _lastProcessName = null;
        else
            ApplyToForegroundWindow();
    }

    public void UpdateRules(IEnumerable<ImeRuleEntry> rules)
    {
        lock (_sync)
        {
            _rules = rules
                .Where(r => !string.IsNullOrWhiteSpace(r.ProcessName))
                .Select(r => new ImeRuleEntry
                {
                    ProcessName = r.ProcessName.Trim(),
                    DisplayName = r.DisplayName?.Trim() ?? r.ProcessName.Trim(),
                    UseChinese = r.UseChinese
                })
                .ToList();
        }
        _lastProcessName = null;
        if (_enabled)
            ApplyToForegroundWindow();
    }

    public void RegisterExcludedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (_sync)
            _excludedHwnds.Add(hwnd);
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            _enabled = false;
            StopThread();
            _disposed = true;
            _ready.Dispose();
        }
    }

    private void StartThread()
    {
        if (_thread != null) return;

        _ready.Reset();
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "WinTools Per-App IME"
        };
        _thread.Start();
        _ready.Wait();
    }

    private void StopThread()
    {
        var thread = _thread;
        if (thread == null) return;

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        if (thread.IsAlive)
            thread.Join(2000);

        _thread = null;
        _hwnd = IntPtr.Zero;
        _ready.Reset();
    }

    private void ThreadMain()
    {
        var instance = GetModuleHandle(null);
        var classRegistered = false;
        try
        {
            _wndProc = WindowProc;
            _winEventProc = OnWinEvent;

            var windowClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = _className
            };

            if (RegisterClass(ref windowClass) == 0)
                return;
            classRegistered = true;

            _hwnd = CreateWindowEx(0, _className, _className, 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                return;

            _hook = SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND,
                EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            _ready.Set();

            MSG msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                DispatchMessage(ref msg);
        }
        finally
        {
            _ready.Set();
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (classRegistered)
                UnregisterClass(_className, instance);
            _wndProc = null;
            _winEventProc = null;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            case WM_DESTROY:
                _hwnd = IntPtr.Zero;
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsTimeStamp)
    {
        if (!_enabled || hwnd == IntPtr.Zero) return;
        TryApplyRule(hwnd, force: false);
    }

    private void ApplyToForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        TryApplyRule(hwnd, force: true);
    }

    private void TryApplyRule(IntPtr hwnd, bool force)
    {
        lock (_sync)
        {
            if (_excludedHwnds.Contains(hwnd)) return;
        }

        try
        {
            var processName = RunningProcessHelper.GetProcessName(hwnd);
            if (string.IsNullOrEmpty(processName)) return;
            if (!force && string.Equals(processName, _lastProcessName, StringComparison.OrdinalIgnoreCase)) return;

            ImeRuleEntry? rule;
            lock (_sync)
                rule = _rules.FirstOrDefault(r => string.Equals(r.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

            if (rule == null) return;

            ImeHelper.SetChineseMode(hwnd, rule.UseChinese);
            _lastProcessName = processName;
        }
        catch
        {
            // ignore
        }
    }

    #region Win32

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsTimeStamp);

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
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool DispatchMessage(ref MSG lpMsg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    #endregion
}
