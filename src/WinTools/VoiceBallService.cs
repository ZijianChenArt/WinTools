using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 语音小球：任意程序里的文本框处于输入状态时，在输入光标的正下方（放不下就正上方）显示一个麦克风小球；
/// 点一下就模拟按下设置里绑定的快捷键（输入法 / 系统的语音输入键），再点一下再按一次。
/// </summary>
/// <remarks>
/// 焦点监听、光标查询、小球窗口和按键模拟全部在一条独立 MTA 后台线程上，不碰 UI 线程。
/// 小球是纯 Win32 分层窗口（<c>WS_EX_NOACTIVATE</c> + <c>MA_NOACTIVATE</c>），**不能换成 WinUI 窗口**：
/// 点击小球时输入焦点必须留在原来的文本框里，快捷键才会发给正确的程序；XAML 岛在点击时会
/// 自己 SetFocus，存在把前台抢过来的风险。外观的 Mica 底色为什么是算出来的，见 <see cref="MicaColorSampler"/>。
/// <para>诊断：在 %TEMP% 放一个 <c>WinTools-voiceball-trace.on</c> 空文件后重启程序，每次判定的结果与原因
/// 写入 <c>%TEMP%\WinTools-voiceball-trace.txt</c>（相邻重复行合并）。</para>
/// </remarks>
public sealed class VoiceBallService : IDisposable
{
    public const string DefaultHotkey = "Win+H";

    private const double BallDip = 32;
    private const double PadDip = 8;      // 小球四周留给投影与「正在听」脉冲圈的空间
    private const double GapDip = 6;      // 小球与光标之间的距离
    private const double GlyphDip = 16;

    private const nuint TimerEvaluate = 1;
    private const nuint TimerTrack = 2;
    private const nuint TimerAnimate = 3;
    private const nuint TimerPoll = 4;
    private const uint EvaluateDelayMs = 80;
    private const uint TrackIntervalMs = 150;
    private const uint AnimateIntervalMs = 30;
    private const uint PollIntervalMs = 500;
    private const int PulseFrames = 50;   // 1.5s 一个脉冲周期
    private const int FadeStep = 64;      // 约 120ms 淡入

    private static readonly string TracePath = Path.Combine(Path.GetTempPath(), "WinTools-voiceball-trace.txt");

    private readonly object _lifecycleSync = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _className = $"WinTools.VoiceBall.{Guid.NewGuid():N}";

    private Thread? _thread;
    private IntPtr _hwnd;
    private bool _disposed;
    private volatile string _hotkey = DefaultHotkey;

    // 用户拖出来的「小球中心相对光标底端」偏移（DIP）；null = 默认的光标正下方 / 正上方。
    private readonly object _offsetSync = new();
    private (double X, double Y)? _customOffset;

    /// <summary>用户拖动小球松手后触发（后台线程），参数是新的偏移（DIP）。调用方负责切回 UI 线程保存。</summary>
    public event Action<double, double>? OffsetDragged;

    // ---- 以下字段只在后台线程上读写 ----
    private WndProc? _wndProc;
    private WinEventProc? _winEventProc;
    private IntPtr _hookSystem;
    private IntPtr _hookFocus;
    private FocusedTextProbe? _probe;
    private MicaColorSampler? _mica;
    private FocusedTextTarget? _target;
    private FocusedTextTarget? _dismissed;
    private bool _visible;
    private bool _hover;
    private bool _pressed;
    private bool _listening;
    private bool _moveSizing;
    private int _x;
    private int _y;
    private int _pixelSize;
    private double _scale = 1;
    private int _anchorX;
    private int _anchorTop;
    private bool _anchorAbove;
    private int _anchorBottom;
    private bool _forceReanchor;
    private bool _dragging;
    private int _dragStartCursorX;
    private int _dragStartCursorY;
    private int _dragStartX;
    private int _dragStartY;
    private RECT _monitorRect;
    private byte _alpha;
    private int _pulseFrame;
    private long _lastTriggerTicks;
    private IntPtr _rejectedForeground;
    private long _rejectedTicks;
    private bool _darkTheme = true;
    private Color _accent = Color.FromArgb(0, 120, 212);
    private Color _micaFill = Color.FromArgb(32, 32, 32);
    private FontFamily? _glyphFont;
    private bool _traceEnabled;
    private string? _lastTrace;

    public void SetEnabled(bool enabled)
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            if (enabled) StartThread();
            else StopThread();
        }
    }

    public void SetHotkey(string? hotkey) =>
        _hotkey = string.IsNullOrWhiteSpace(hotkey) ? DefaultHotkey : hotkey.Trim();

    /// <summary>设置 / 清除自定义偏移；小球正显示时立即按新规则重新摆放。</summary>
    public void SetCustomOffset((double X, double Y)? offset)
    {
        lock (_offsetSync) _customOffset = offset;
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_APP_REPLACE, IntPtr.Zero, IntPtr.Zero);
    }

    private (double X, double Y)? CustomOffset
    {
        get { lock (_offsetSync) return _customOffset; }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            StopThread();
            _disposed = true;
            _ready.Dispose();
        }
    }

    #region 线程生命周期

    private void StartThread()
    {
        if (_thread != null) return;
        _ready.Reset();
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "WinTools Voice Ball" };
        // UIA 客户端官方建议跑在 MTA；消息循环照样能收 WinEvent 与定时器。
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _ready.Wait(3000);
    }

    private void StopThread()
    {
        var thread = _thread;
        if (thread == null) return;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (thread.IsAlive) thread.Join(2000);
        _thread = null;
        _hwnd = IntPtr.Zero;
    }

    private void ThreadMain()
    {
        var instance = GetModuleHandle(null);
        var classRegistered = false;
        try
        {
            _traceEnabled = File.Exists(Path.Combine(Path.GetTempPath(), "WinTools-voiceball-trace.on"));
            _probe = new FocusedTextProbe();
            _mica = new MicaColorSampler();
            _wndProc = WindowProc;
            _winEventProc = OnWinEvent;

            var windowClass = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                hCursor = LoadCursor(IntPtr.Zero, IDC_HAND),
                lpszClassName = _className,
            };
            if (RegisterClass(ref windowClass) == 0) return;
            classRegistered = true;

            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                _className, string.Empty, WS_POPUP, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;

            // 前台切换 / 拖动窗口 / 最小化走一个区间钩子，焦点变化单独一个；都跳过本进程。
            _hookSystem = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZESTART,
                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            _hookFocus = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
                IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            _ready.Set();
            Trace("started");
            ScheduleEvaluate();
            SetTimer(_hwnd, TimerPoll, PollIntervalMs, IntPtr.Zero);

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                DispatchMessage(ref msg);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("VoiceBallService.ThreadMain", ex);
        }
        finally
        {
            _ready.Set();
            if (_hookSystem != IntPtr.Zero) { UnhookWinEvent(_hookSystem); _hookSystem = IntPtr.Zero; }
            if (_hookFocus != IntPtr.Zero) { UnhookWinEvent(_hookFocus); _hookFocus = IntPtr.Zero; }
            if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
            if (classRegistered) UnregisterClass(_className, instance);
            _visible = false;
            _target = null;
            _probe = null;
            _mica?.Dispose();
            _mica = null;
            _glyphFont?.Dispose();
            _glyphFont = null;
            _wndProc = null;
            _winEventProc = null;
        }
    }

    private void Trace(string message)
    {
        if (!_traceEnabled || message == _lastTrace) return;
        _lastTrace = message;
        try { File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); }
        catch { /* 诊断日志写不进去不影响功能 */ }
    }

    #endregion

    #region 焦点跟踪

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        switch (eventType)
        {
            case EVENT_SYSTEM_MOVESIZESTART:
                // 拖动窗口期间位置每帧都在变，直接藏起来，松手后再算。
                _moveSizing = true;
                Hide();
                break;
            case EVENT_SYSTEM_MOVESIZEEND:
                _moveSizing = false;
                ScheduleEvaluate();
                break;
            case EVENT_OBJECT_FOCUS:
            case EVENT_SYSTEM_FOREGROUND:
            case EVENT_SYSTEM_MINIMIZESTART:
                ScheduleEvaluate();
                break;
        }
    }

    /// <summary>焦点事件往往一次来好几条（窗口 → 容器 → 输入框），防抖后只查一次。</summary>
    private void ScheduleEvaluate() => SetTimer(_hwnd, TimerEvaluate, EvaluateDelayMs, IntPtr.Zero);

    private void Evaluate()
    {
        KillTimer(_hwnd, TimerEvaluate);
        if (_moveSizing || _probe == null) return;

        FocusedTextTarget? next;
        try { next = _probe.GetFocusedTextTarget(); }
        catch (Exception ex)
        {
            ErrorReporter.Log("VoiceBallService.Evaluate", ex);
            next = null;
        }

        if (next is not { } found)
        {
            Trace($"hide: {_probe.LastReason}");
            // 正在语音输入时，输入法 / 系统的语音面板可能暂时拿走焦点或光标；只要前台还是原窗口，
            // 小球就留着，否则用户没法再点一次结束语音。
            if (_listening && _visible && _target is { } current && GetForegroundWindow() == current.RootWindow)
                return;
            RememberRejection();
            _dismissed = null;
            Hide();
            return;
        }

        if (_dismissed is { } dismissed && dismissed.IsSameElement(found))
        {
            Trace("dismissed by right click");
            return;
        }
        _dismissed = null;
        Trace($"show: {_probe.LastReason} {found.Describe()}");

        if (_target is { } previous && previous.RootWindow != found.RootWindow)
            _listening = false;
        ShowAt(found);
    }

    private void RememberRejection()
    {
        _rejectedForeground = GetForegroundWindow();
        _rejectedTicks = Environment.TickCount64;
    }

    /// <summary>
    /// 隐藏时每 500ms 看一眼前台有没有输入光标。浏览器 / Electron 在页面内部换输入框时不一定发焦点事件，
    /// 只靠事件会漏掉。同一个窗口刚被判定过「不是输入框」（例如密码框）的，2 秒内不重复做完整判定。
    /// </summary>
    private void Poll()
    {
        if (_visible || _moveSizing || _probe == null) return;
        if (!_probe.HasAnyCaret(out var foreground, out var focus)) return;
        if (_dismissed is { } dismissed && dismissed.RootWindow == foreground && dismissed.FocusWindow == focus) return;
        if (foreground == _rejectedForeground && Environment.TickCount64 - _rejectedTicks < 2000) return;
        Evaluate();
    }

    /// <summary>显示期间定时跟随光标：打字换行、网页滚动、窗口改尺寸都不会发焦点事件。</summary>
    private void Track()
    {
        if (!_visible || _probe == null || _target is not { } current) return;

        FocusedTextTarget? refreshed;
        try { refreshed = _probe.RefreshCaret(current); }
        catch (Exception) { refreshed = null; }

        if (refreshed is not { } moved)
        {
            Evaluate();
            return;
        }
        if (moved != current) ShowAt(moved);
    }

    #endregion

    #region 显示与位置

    private void ShowAt(FocusedTextTarget target)
    {
        // 正在听（小球是蓝色）时不跟随光标：语音转写会不停地往框里写字，小球跟着跑既晃眼又点不准。
        // 拖动中也不能被定时跟随打断。
        if (_visible && (_listening || _dragging) && !_forceReanchor)
        {
            _target = target;
            return;
        }

        int anchorX, anchorTop, anchorBottom;
        if (target.HasCaret)
        {
            anchorX = target.CaretX;
            anchorTop = target.CaretTop;
            anchorBottom = target.CaretBottom;
        }
        else if (target.BoxRight > target.BoxLeft)
        {
            // 取不到光标（少数只支持 UIA 的控件）：当作光标停在输入框左端。
            anchorX = target.BoxLeft + (target.BoxBottom - target.BoxTop) / 2;
            anchorTop = target.BoxTop;
            anchorBottom = target.BoxBottom;
        }
        else
        {
            Hide();
            return;
        }

        var point = new POINT { X = anchorX, Y = anchorTop };
        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            Hide();
            return;
        }
        var work = monitorInfo.rcWork;
        GetWindowRect(target.RootWindow, out var window);

        var ball = (int)Math.Round(BallDip * scale);
        var gap = (int)Math.Round(GapDip * scale);
        var size = ball + 2 * (int)Math.Round(PadDip * scale);
        var pad = (size - ball) / 2;

        // 打字时光标一直右移：同一行里挪动不超过三个小球宽度就不跟，免得小球每敲一个字跳一下。
        var sameLine = _visible && !_forceReanchor && _target is { } shown && shown.IsSameElement(target) &&
                       Math.Abs(anchorTop - _anchorTop) <= Math.Max(2, (anchorBottom - anchorTop) / 2) &&
                       Math.Abs(anchorX - _anchorX) <= ball * 3;
        if (!sameLine)
        {
            _anchorX = anchorX;
            _anchorTop = anchorTop;
            // 默认放在光标下方；下面超出窗口或屏幕工作区时放到上方。
            var bottomLimit = Math.Min(work.Bottom, window.Bottom > window.Top ? window.Bottom : work.Bottom);
            _anchorAbove = anchorBottom + gap + ball > bottomLimit && anchorTop - gap - ball >= work.Top;
        }

        _forceReanchor = false;
        _anchorBottom = anchorBottom;

        int bx, by;
        if (CustomOffset is { } offset)
        {
            // 用户拖出来的位置：小球中心 = 光标底端 + 偏移。下方超出屏幕时整体翻到光标上方（反之亦然），
            // 否则贴底的输入框里小球会被夹到光标身上。
            var centerX = _anchorX + (int)Math.Round(offset.X * scale);
            var centerY = anchorBottom + (int)Math.Round(offset.Y * scale);
            if (offset.Y > 0 && centerY + ball / 2 > work.Bottom)
                centerY = anchorTop - (int)Math.Round(offset.Y * scale);
            else if (offset.Y < 0 && centerY - ball / 2 < work.Top)
                centerY = anchorBottom - (int)Math.Round(offset.Y * scale);
            bx = centerX - ball / 2;
            by = centerY - ball / 2;
        }
        else
        {
            bx = _anchorX - ball / 2;
            by = _anchorAbove ? anchorTop - gap - ball : anchorBottom + gap;
        }
        bx = Math.Clamp(bx, work.Left, Math.Max(work.Left, work.Right - ball));
        by = Math.Clamp(by, work.Top, Math.Max(work.Top, work.Bottom - ball));

        var x = bx - pad;
        var y = by - pad;
        var moved = x != _x || y != _y || size != _pixelSize || Math.Abs(scale - _scale) > 0.001;
        _x = x;
        _y = y;
        _pixelSize = size;
        _scale = scale;
        _target = target;
        _monitorRect = monitorInfo.rcMonitor;

        if (!_visible)
        {
            RefreshColors();
            UpdateMicaFill(monitorInfo.rcMonitor, bx + ball / 2, by + ball / 2);
            _visible = true;
            _hover = false;
            _pressed = false;
            _alpha = 0;
            Render();
            SetWindowPos(_hwnd, HWND_TOPMOST, _x, _y, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetTimer(_hwnd, TimerAnimate, AnimateIntervalMs, IntPtr.Zero);
            SetTimer(_hwnd, TimerTrack, TrackIntervalMs, IntPtr.Zero);
        }
        else if (moved)
        {
            UpdateMicaFill(monitorInfo.rcMonitor, bx + ball / 2, by + ball / 2);
            Render();
        }
    }

    private void Hide()
    {
        if (!_visible) return;
        _visible = false;
        _hover = false;
        _pressed = false;
        _dragging = false;
        _listening = false;
        _target = null;
        if (GetCapture() == _hwnd) ReleaseCapture();
        KillTimer(_hwnd, TimerTrack);
        KillTimer(_hwnd, TimerAnimate);
        ShowWindow(_hwnd, SW_HIDE);
    }

    private void Animate()
    {
        if (!_visible)
        {
            KillTimer(_hwnd, TimerAnimate);
            return;
        }
        if (_alpha < 255) _alpha = (byte)Math.Min(255, _alpha + FadeStep);
        if (_listening) _pulseFrame = (_pulseFrame + 1) % PulseFrames;
        Render();
        if (_alpha == 255 && !_listening) KillTimer(_hwnd, TimerAnimate);
    }

    #endregion

    #region 点击与按键模拟

    private void OnClick()
    {
        // 连点防抖：输入法切换语音状态需要时间，太快的第二下会被当成同一次。
        var now = Environment.TickCount64;
        if (now - _lastTriggerTicks < 350) return;
        _lastTriggerTicks = now;

        var parsed = HotkeyHelper.ParseForSend(_hotkey);
        if (parsed == null) return;

        _listening = !_listening;
        _pulseFrame = 0;
        if (_listening) SetTimer(_hwnd, TimerAnimate, AnimateIntervalMs, IntPtr.Zero);
        Render();
        Trace($"click: listening={_listening} send {_hotkey}");

        TriggerHotkey(_hotkey);
    }

    internal static void TriggerHotkey(string? hotkey)
    {
        var parsed = HotkeyHelper.ParseForSend(string.IsNullOrWhiteSpace(hotkey) ? DefaultHotkey : hotkey);
        if (parsed == null) return;
        var (modifiers, key) = parsed.Value;
        _ = Task.Run(() => SendHotkey(modifiers, key));
    }

    private void BeginPress()
    {
        _pressed = true;
        GetCursorPos(out var cursor);
        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartX = _x;
        _dragStartY = _y;
        SetCapture(_hwnd);
        Render();
    }

    /// <summary>未点亮时按住拖动：超过系统拖动阈值才算拖，普通点击的轻微抖动不会挪走小球。</summary>
    private void ContinueDrag()
    {
        if (!_pressed || _listening) return;
        GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursorX;
        var dy = cursor.Y - _dragStartCursorY;
        if (!_dragging)
        {
            if (Math.Abs(dx) < GetSystemMetrics(SM_CXDRAG) && Math.Abs(dy) < GetSystemMetrics(SM_CYDRAG)) return;
            _dragging = true;
        }
        _x = _dragStartX + dx;
        _y = _dragStartY + dy;
        Render();
    }

    private void EndPress()
    {
        // 先清按下状态再释放捕获：ReleaseCapture 会同步发 WM_CAPTURECHANGED，不能让它再进来一次。
        var wasPressed = _pressed;
        _pressed = false;
        if (GetCapture() == _hwnd) ReleaseCapture();
        if (!wasPressed) return;

        if (!_dragging)
        {
            OnClick();
            return;
        }

        _dragging = false;
        var half = _pixelSize / 2;
        var offsetX = Math.Round((_x + half - _anchorX) / _scale);
        var offsetY = Math.Round((_y + half - _anchorBottom) / _scale);
        lock (_offsetSync) _customOffset = (offsetX, offsetY);
        UpdateMicaFill(_monitorRect, _x + half, _y + half);
        Render();
        Trace($"drag: offset={offsetX},{offsetY}");
        OffsetDragged?.Invoke(offsetX, offsetY);
    }

    /// <summary>右键：本次不再在这个输入框旁显示，焦点换到别的输入框后恢复。</summary>
    private void OnDismiss()
    {
        _dismissed = _target;
        Hide();
    }

    private static void SendHotkey(HotkeyModifiers modifiers, ushort key)
    {
        try
        {
            Span<ushort> held = stackalloc ushort[4];
            var count = 0;
            if ((modifiers & HotkeyModifiers.Win) != 0) held[count++] = VK_LWIN;
            if ((modifiers & HotkeyModifiers.Control) != 0) held[count++] = VK_CONTROL;
            if ((modifiers & HotkeyModifiers.Alt) != 0) held[count++] = VK_MENU;
            if ((modifiers & HotkeyModifiers.Shift) != 0) held[count++] = VK_SHIFT;

            // 逐个按下并留出间隔：不少输入法用低级键盘钩子 + GetAsyncKeyState 判断组合键，
            // 一次性塞进同一个 SendInput 的话，它们读到的修饰键状态可能还没更新。
            for (var i = 0; i < count; i++) { SendKey(held[i], up: false); Thread.Sleep(10); }
            if (key != 0)
            {
                SendKey(key, up: false);
                Thread.Sleep(30);
                SendKey(key, up: true);
            }
            else
            {
                Thread.Sleep(30);
            }
            for (var i = count - 1; i >= 0; i--) { Thread.Sleep(10); SendKey(held[i], up: true); }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("VoiceBallService.SendHotkey", ex);
        }
    }

    private static void SendKey(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0u;
        if (IsExtendedKey(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags },
            },
        };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk) =>
        vk is VK_LWIN or 0x5C or (>= 0x21 and <= 0x28) or 0x2D or 0x2E;

    #endregion

    #region 绘制（Win11 Fluent：Mica 底 + 1px 描边 + 柔和投影）

    private void RefreshColors()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _darkTheme = key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch { _darkTheme = true; }

        try
        {
            // 与 WinUI 的 AccentFillColorDefault 一致：深色主题用 AccentLight2，浅色主题用 AccentDark1。
            var type = _darkTheme ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1;
            var accent = new Windows.UI.ViewManagement.UISettings().GetColorValue(type);
            _accent = Color.FromArgb(accent.R, accent.G, accent.B);
        }
        catch { /* 保留默认蓝 */ }
    }

    private void UpdateMicaFill(RECT monitor, int centerX, int centerY)
    {
        try
        {
            _micaFill = _mica?.GetMicaColor(monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                centerX, centerY, _darkTheme) ?? _micaFill;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("VoiceBallService.UpdateMicaFill", ex);
            _micaFill = _darkTheme ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        }
    }

    private FontFamily GetGlyphFont()
    {
        if (_glyphFont != null) return _glyphFont;
        _glyphFont = new FontFamily(FeatureIcons.FontName);
        return _glyphFont;
    }

    private void Render()
    {
        if (_hwnd == IntPtr.Zero || _pixelSize <= 0) return;
        var size = _pixelSize;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            DrawBall(g, size);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size, // 自顶向下，与 GDI+ LockBits 的行序一致
            biPlanes = 1,
            biBitCount = 32,
        };
        var dib = CreateDIBSection(memoryDc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var oldBitmap = SelectObject(memoryDc, dib);
        try
        {
            // UpdateLayeredWindow 要求预乘 alpha：用 PArgb 格式锁定，取到的就是预乘后的像素。
            var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[size * 4];
                for (var y = 0; y < size; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits + y * size * 4, row.Length);
                }
            }
            finally { bitmap.UnlockBits(data); }

            var destination = new POINT { X = _x, Y = _y };
            var extent = new SIZE { cx = size, cy = size };
            var source = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = _alpha, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screenDc, ref destination, ref extent, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(dib);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void DrawBall(Graphics g, int size)
    {
        var s = (float)_scale;
        var center = size / 2f;
        var radius = (float)(BallDip * s / 2);

        if (_listening)
        {
            // 「正在听」：强调色脉冲圈向外扩散并淡出。
            var phase = _pulseFrame / (float)PulseFrames;
            var ringRadius = radius + phase * (float)(PadDip * s - 1);
            using var ring = new SolidBrush(Color.FromArgb((int)(110 * (1 - phase)), _accent));
            g.FillEllipse(ring, center - ringRadius, center - ringRadius, ringRadius * 2, ringRadius * 2);
        }

        // Win11 浮层投影：半径逐层放大、透明度逐层衰减，整体略向下偏移 1dip。
        for (var i = 4; i >= 1; i--)
        {
            var shadowRadius = radius + i * 1.1f * s;
            using var shadow = new SolidBrush(Color.FromArgb((_darkTheme ? 44 : 26) / (i + 1), 0, 0, 0));
            g.FillEllipse(shadow, center - shadowRadius, center - shadowRadius + s, shadowRadius * 2, shadowRadius * 2);
        }

        var body = new RectangleF(center - radius, center - radius, radius * 2, radius * 2);
        Color glyph;
        if (_listening)
        {
            using var accent = new SolidBrush(_accent);
            g.FillEllipse(accent, body);
            // TextOnAccentFillColorPrimary：深色主题是黑字，浅色主题是白字。
            glyph = _darkTheme ? Color.Black : Color.White;
            if (_hover || _pressed)
            {
                using var overlay = new SolidBrush(_darkTheme ? Color.FromArgb(_pressed ? 40 : 24, 0, 0, 0) : Color.FromArgb(_pressed ? 40 : 24, 255, 255, 255));
                g.FillEllipse(overlay, body);
            }
        }
        else
        {
            using (var mica = new SolidBrush(_micaFill))
                g.FillEllipse(mica, body);

            // SubtleFillColorSecondary / Tertiary：悬停与按下时叠一层很淡的白（深色）或黑（浅色）。
            if (_hover || _pressed)
            {
                var alpha = _pressed ? (_darkTheme ? 10 : 6) : (_darkTheme ? 15 : 9);
                using var overlay = new SolidBrush(_darkTheme ? Color.FromArgb(alpha, 255, 255, 255) : Color.FromArgb(alpha, 0, 0, 0));
                g.FillEllipse(overlay, body);
            }

            // 描边：深色主题上沿亮、下沿暗（Win11 控件的立体描边），浅色主题一圈淡黑。
            var strokeWidth = Math.Max(1f, s);
            var strokeRect = RectangleF.Inflate(body, -strokeWidth / 2, -strokeWidth / 2);
            if (_darkTheme)
            {
                using var gradient = new LinearGradientBrush(strokeRect, Color.FromArgb(36, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), LinearGradientMode.Vertical);
                using var pen = new Pen(gradient, strokeWidth);
                g.DrawEllipse(pen, strokeRect);
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(26, 0, 0, 0), strokeWidth);
                g.DrawEllipse(pen, strokeRect);
            }
            glyph = _darkTheme ? Color.White : Color.FromArgb(28, 28, 28);
        }

        // 麦克风图标（Segoe Fluent Icons E720）：按墨迹边界居中，DrawString 的行盒居中会偏上。
        using var path = new GraphicsPath();
        path.AddString(FeatureIcons.Voice, GetGlyphFont(), (int)FontStyle.Regular, (float)(GlyphDip * s), PointF.Empty, StringFormat.GenericTypographic);
        var bounds = path.GetBounds();
        using (var matrix = new Matrix())
        {
            matrix.Translate(center - bounds.X - bounds.Width / 2, center - bounds.Y - bounds.Height / 2);
            path.Transform(matrix);
        }
        using var glyphBrush = new SolidBrush(glyph);
        g.FillPath(glyphBrush, path);
    }

    #endregion

    #region 窗口过程

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return MA_NOACTIVATE;
                case WM_TIMER:
                    switch ((nuint)wParam)
                    {
                        case TimerEvaluate: Evaluate(); break;
                        case TimerTrack: Track(); break;
                        case TimerAnimate: Animate(); break;
                        case TimerPoll: Poll(); break;
                    }
                    return IntPtr.Zero;
                case WM_MOUSEMOVE:
                    if (!_hover)
                    {
                        _hover = true;
                        var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = hwnd };
                        TrackMouseEvent(ref track);
                        if (!_pressed) Render();
                    }
                    ContinueDrag();
                    return IntPtr.Zero;
                case WM_MOUSELEAVE:
                    _hover = false;
                    // 拖动时持有鼠标捕获，光标暂时落在小球外面很正常，不能因此取消按下状态。
                    if (GetCapture() != hwnd) _pressed = false;
                    if (_visible) Render();
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    BeginPress();
                    return IntPtr.Zero;
                case WM_RBUTTONDOWN:
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    EndPress();
                    return IntPtr.Zero;
                case WM_CAPTURECHANGED:
                    // 捕获被系统拿走（例如弹出 UAC、按了 Win 键）：当作松手，已拖出的位置照常保存。
                    if (_pressed && lParam != hwnd) EndPress();
                    return IntPtr.Zero;
                case WM_APP_REPLACE:
                    if (_visible && _target is { } current)
                    {
                        _forceReanchor = true;
                        ShowAt(current);
                    }
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    OnDismiss();
                    return IntPtr.Zero;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("VoiceBallService.WindowProc", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    #endregion

    #region Win32

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint WM_CAPTURECHANGED = 0x0215;
    private const uint WM_APP_REPLACE = 0x8001;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;
    private const nint MA_NOACTIVATE = 3;
    private const uint TME_LEAVE = 0x00000002;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
    private const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const byte AC_SRC_ALPHA = 1;
    private const uint ULW_ALPHA = 2;
    private static readonly IntPtr IDC_HAND = new(32649);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

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
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS wndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint elapse, IntPtr timerProc);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT eventTrack);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    #endregion
}
