using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools;

/// <summary>Reads real three-finger motion; Windows processes a virtual one-finger contact.</summary>
internal sealed class ThreeFingerDragService : IDisposable
{
    private const int MaxContactCount = 8;
    private const uint ReleaseTimeoutMs = 250, WM_TIMER = 0x113;
    private const nuint ReleaseTimerId = 1;
    internal const int SyntheticInputTag = 0x574E_544C;
    private readonly object _syncRoot = new();
    private readonly IThreeFingerInput _input;
    private readonly Func<long> _clock;
    private IntPtr _hwnd, _sourceDevice;
    private bool _enabled, _isRegistered, _active, _waitingForLift;
    private long _incompleteSince = -1, _lastFrameTick;
    private TouchpadGeometry? _geometry;
    private readonly TouchpadContact[] _frameContacts = new TouchpadContact[MaxContactCount];
    private readonly TouchpadContact[] _partialContacts = new TouchpadContact[MaxContactCount];
    private int _partialContactCount;
    private uint _expectedContactCount;
    private readonly int[] _gestureIds = new int[3], _gestureLastX = new int[3], _gestureLastY = new int[3];
    private readonly bool[] _gestureLastValid = new bool[3];
    private int _gestureContactCount;
    private long _activationCount, _moveCount, _releaseCount, _parseFailures, _deviceChanges;
    private string? _lastError;

    internal ThreeFingerDragService(IThreeFingerInput? input = null, Func<long>? clock = null)
    { _input = input ?? new NativeTouchpadInput(); _clock = clock ?? (() => Environment.TickCount64); }

    public bool HasTouchpad => PrecisionTouchpadHelper.HasPrecisionTouchpad();
    internal string Status => _lastError ?? _input.Error ?? GetAvailability();
    internal static string GetAvailability()
    {
        if (!NativeTouchpadInput.IsSupported) return "此 Windows 版本不支持原生触控板输入，请更新系统后重试。";
        if (!PrecisionTouchpadHelper.HasPrecisionTouchpad()) return "未检测到精确式触摸板。";
        if (!PrecisionTouchpadHelper.HasPhysicalGeometry()) return "触摸板未提供有效物理尺寸，无法使用原生拖拽。";
        return "跟随 Windows 触摸板速度与加速设置，无需校准。";
    }

    public void Initialize(IntPtr hwnd, bool enabled) { _hwnd = hwnd; SetEnabled(enabled); }
    public void SetEnabled(bool enabled)
    {
        lock (_syncRoot)
        {
            _enabled = enabled;
            if (enabled)
            {
                _lastError = null;
                if (!_isRegistered && _hwnd != IntPtr.Zero)
                {
                    _isRegistered = PrecisionTouchpadHelper.RegisterInput(_hwnd);
                    if (!_isRegistered) _lastError = "无法接收触摸板输入，请关闭再开启三指拖拽。";
                }
            }
            else
            {
                EndDrag(); _waitingForLift = false;
                if (_isRegistered && PrecisionTouchpadHelper.UnregisterInput()) _isRegistered = false;
            }
        }
    }

    internal string GetDiagnosticsSnapshot() =>
        $"enabled={_enabled}; registered={_isRegistered}; active={_active}; " +
        $"activations={Interlocked.Read(ref _activationCount)}; moves={Interlocked.Read(ref _moveCount)}; " +
        $"releases={Interlocked.Read(ref _releaseCount)}; parseFailures={Interlocked.Read(ref _parseFailures)}; " +
        $"deviceChanges={Interlocked.Read(ref _deviceChanges)}; status={Status}";
    internal long DeviceChanges => Interlocked.Read(ref _deviceChanges);

    public bool HandleWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_TIMER && (nuint)wParam == ReleaseTimerId)
        { lock (_syncRoot) { CheckTimeout(); } return true; }
        if (message == PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE)
        {
            Interlocked.Increment(ref _deviceChanges);
            lock (_syncRoot)
            {
                // Creating/destroying our virtual touchpad generates these notifications too.
                // Only removal of the physical source can end a gesture; arrivals and removals
                // of other devices must not release and re-press the drag button.
                if (wParam == (IntPtr)2 && lParam == _sourceDevice && _sourceDevice != IntPtr.Zero)
                { EndDrag(); _sourceDevice = IntPtr.Zero; _waitingForLift = true; }
                PrecisionTouchpadHelper.InvalidateDeviceCache(lParam);
            }
            return true;
        }
        if (!_enabled || !_isRegistered || message != PrecisionTouchpadHelper.WM_INPUT) return false;
        lock (_syncRoot)
        {
            if (!PrecisionTouchpadHelper.TryParseInput(lParam, _frameContacts, out var parsed, out var count, out var device, out var geometry))
            { Interlocked.Increment(ref _parseFailures); return true; }
            ProcessInput(device, geometry, _frameContacts.AsSpan(0, parsed), count);
        }
        return true;
    }

    // Shared by parsed HID reports and deterministic state-machine regression tests.
    internal void ProcessInput(IntPtr device, TouchpadGeometry? geometry, ReadOnlySpan<TouchpadContact> contacts, uint count)
    {
        if (!_enabled) return;
        if (device != _sourceDevice)
        {
            // The virtual one-finger PTP injects real Raw Input HID feedback. Never let it
            // steal the physical source, release its button, or rearm a failed gesture.
            // Lock an active gesture to its source; only a new 3+ finger candidate can
            // select a source while idle (including split reports with count=3).
            if (_active || count < 3 && contacts.Length < 3) return;
            EndDrag(); _sourceDevice = device; _geometry = geometry; _waitingForLift = false;
        }
        else if (geometry != _geometry)
        { EndDrag(); _geometry = geometry; }
        if (count > MaxContactCount || contacts.Length > MaxContactCount) { EndDrag(); _waitingForLift = true; return; }
        if (contacts.Length == 0)
        {
            if (count == 0) { EndDrag(); _waitingForLift = false; }
            return;
        }
        contacts.CopyTo(_frameContacts);
        if (count == 0 && _expectedContactCount == 0)
        {
            var anyDown = false;
            foreach (var contact in contacts) anyDown |= contact.IsDown;
            if (!anyDown) { EndDrag(); _waitingForLift = false; return; }
        }
        var assembled = BuildFrame(contacts.Length, count);
        if (assembled == 0) return;
        for (var i = 0; i < assembled; i++)
            for (var j = 0; j < i; j++)
                if (_frameContacts[i].ContactId == _frameContacts[j].ContactId)
                {
                    // A malformed frame is not a finger lift. Keep a valid ongoing drag until
                    // another complete frame or its watchdog timeout, without moving on bad data.
                    return;
                }
        var activeCount = 0;
        for (var i = 0; i < assembled; i++)
            if (_frameContacts[i].IsDown) _frameContacts[activeCount++] = _frameContacts[i];
        assembled = activeCount;
        if (assembled == 0) { EndDrag(); _waitingForLift = false; return; }
        _lastFrameTick = _clock();
        if (_waitingForLift) { if (assembled < 3) _waitingForLift = false; return; }
        if (!_active)
        {
            if (assembled != 3) return;
            if (_geometry == null) { _lastError = "触摸板未提供有效物理尺寸，无法使用原生拖拽。"; _waitingForLift = true; return; }
            SetGestureIds(_frameContacts, assembled);
            InitGesturePositions(_frameContacts, assembled);
            double x = 0, y = 0;
            foreach (var contact in _frameContacts.AsSpan(0, assembled)) { x += contact.X; y += contact.Y; }
            if (!_input.Begin(_geometry.Value, x / 3, y / 3)) { _lastError = _input.Error; EndDrag(); _waitingForLift = true; return; }
            _lastError = null;
            _active = true;
            Interlocked.Increment(ref _activationCount);
        }
        else
        {
            var shared = CountSharedGestureIds(_frameContacts, assembled);
            if (assembled > 3 || shared < 2) { EndDrag(); _waitingForLift = assembled >= 3; return; }
            if (assembled < 3 || shared < 3)
            {
                if (_incompleteSince < 0) _incompleteSince = _clock();
                if (_clock() - _incompleteSince >= 40) { EndDrag(); return; }
            }
            else _incompleteSince = -1;
            var (dx, dy, matched) = ComputeDeltaAndUpdate(_frameContacts, assembled);
            if (matched >= 2)
            {
                if (!_input.Move(dx, dy)) { _lastError = _input.Error; EndDrag(); _waitingForLift = true; return; }
                Interlocked.Increment(ref _moveCount);
            }
        }
        ArmTimeout();
    }

    private void ArmTimeout()
    {
        if (_hwnd == IntPtr.Zero) return;
        var delay = _incompleteSince >= 0 ? (uint)Math.Max(1, 40 - (_clock() - _incompleteSince)) : ReleaseTimeoutMs;
        SetTimer(_hwnd, ReleaseTimerId, delay, IntPtr.Zero);
    }

    internal void CheckTimeout()
    {
        if (_active && (_clock() - _lastFrameTick >= ReleaseTimeoutMs || _incompleteSince >= 0 && _clock() - _incompleteSince >= 40))
        { EndDrag(); _waitingForLift = true; }
        else if (_active) ArmTimeout();
    }

    private void EndDrag()
    {
        if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, ReleaseTimerId);
        _input.End();
        if (_active) Interlocked.Increment(ref _releaseCount);
        _active = false;
        _gestureContactCount = _partialContactCount = 0;
        _expectedContactCount = 0; _incompleteSince = -1;
        Array.Clear(_gestureLastValid);
    }

    public void Dispose() { lock (_syncRoot) { SetEnabled(false); _input.Dispose(); } }

    private int BuildFrame(int parsedCount, uint count)
    {
        if (parsedCount <= 0)
            return 0;

        // 没有待拼帧数据时，完整报告可直接使用。
        if (_expectedContactCount == 0 && count == parsedCount)
        {
            _partialContactCount = 0;
            return parsedCount;
        }

        // 部分 PTP 设备把同一帧的每根手指拆成独立 WM_INPUT，但每份子报告
        // 都重复携带总 ContactCount。相同 count 必须继续累积，不能重新清空。
        if (count > 0)
        {
            if (count > MaxContactCount) return 0;
            if (_expectedContactCount != count)
            {
                _partialContactCount = 0;
                _expectedContactCount = count;
            }
        }
        else if (_expectedContactCount == 0)
        {
            return 0;
        }

        for (var i = 0; i < parsedCount; i++)
            UpsertPartialContact(_frameContacts[i]);

        if (_partialContactCount < _expectedContactCount)
            return 0;

        var assembled = Math.Min(_partialContactCount, (int)_expectedContactCount);
        for (var i = 0; i < assembled; i++)
            _frameContacts[i] = _partialContacts[i];

        _partialContactCount = 0;
        _expectedContactCount = 0;
        return assembled;
    }

    /// <summary>同一 Contact ID 的新子报告覆盖旧位置；新 ID 才增加拼帧数量。</summary>
    private void UpsertPartialContact(TouchpadContact contact)
    {
        for (var i = 0; i < _partialContactCount; i++)
        {
            if (_partialContacts[i].ContactId == contact.ContactId)
            {
                _partialContacts[i] = contact;
                return;
            }
        }

        if (_partialContactCount < MaxContactCount)
            _partialContacts[_partialContactCount++] = contact;
    }

    // ─── 手势位置追踪 ─────────────────────────────────────────────────────────

    private void SetGestureIds(TouchpadContact[] contacts, int count)
    {
        _gestureContactCount = count > 3 ? 3 : count;
        for (var i = 0; i < _gestureContactCount; i++)
            _gestureIds[i] = contacts[i].ContactId;
    }

    private void InitGesturePositions(TouchpadContact[] contacts, int count)
    {
        for (var j = 0; j < _gestureContactCount; j++)
            _gestureLastValid[j] = false;

        UpdateGesturePositions(contacts, count);
    }

    private void UpdateGesturePositions(TouchpadContact[] contacts, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            for (var j = 0; j < _gestureContactCount; j++)
            {
                if (_gestureIds[j] != c.ContactId) continue;
                _gestureLastX[j] = c.X;
                _gestureLastY[j] = c.Y;
                _gestureLastValid[j] = true;
                break;
            }
        }
    }

    /// <summary>计算当前帧各 contact 相对上帧的位移均值，同时更新记录位置。</summary>
    private (float Dx, float Dy, int Matched) ComputeDeltaAndUpdate(TouchpadContact[] contacts, int count)
    {
        float totalDx = 0, totalDy = 0;
        var matched = 0;
        Span<bool> seen = stackalloc bool[3];
        seen.Clear();

        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            for (var j = 0; j < _gestureContactCount; j++)
            {
                if (_gestureIds[j] != c.ContactId) continue;
                seen[j] = true;
                if (_gestureLastValid[j])
                {
                    totalDx += c.X - _gestureLastX[j];
                    totalDy += c.Y - _gestureLastY[j];
                    matched++;
                }
                _gestureLastX[j] = c.X;
                _gestureLastY[j] = c.Y;
                _gestureLastValid[j] = true;
                break;
            }
        }

        for (var j = 0; j < _gestureContactCount; j++)
            if (!seen[j]) _gestureLastValid[j] = false;
        return matched == 0 ? (0, 0, 0) : (totalDx / matched, totalDy / matched, matched);
    }

    private int CountSharedGestureIds(TouchpadContact[] contacts, int count)
    {
        var shared = 0;
        for (var i = 0; i < _gestureContactCount; i++)
            for (var j = 0; j < count; j++)
                if (_gestureIds[i] == contacts[j].ContactId)
                { shared++; break; }
        return shared;
    }



    [DllImport("user32.dll")] private static extern nuint SetTimer(IntPtr hwnd, nuint id, uint interval, IntPtr callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(IntPtr hwnd, nuint id);
}
