using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools;

internal readonly record struct TouchpadCalibrationResult(
    bool Success,
    double LowSpeedGain,
    double HighSpeedGain,
    double AccelerationStart,
    double AccelerationEnd,
    int SampleCount,
    double FitQuality);

/// <summary>
/// 三指拖动服务。
/// 统一使用 BuildFrame 完成多子报文拼帧，确保每帧数据完整后再决策。
/// 三指位移以 1:1 相对输入交给 Windows，由系统当前指针速度与加速度统一处理。
/// 使用 Win32 SetTimer 代替 System.Timers.Timer，避免每帧两次内核态切换和跨线程锁竞争。
/// </summary>
internal sealed class ThreeFingerDragService : IDisposable
{
    private const int MaxContactCount = 8;
    private const uint ReleaseTimeoutMs = 120;
    private const uint CalibrationReleaseTimeoutMs = 32;
    private const uint WM_TIMER = 0x0113;
    private const nint ReleaseTimerId = 1;
    internal const int SyntheticInputTag = 0x574E_544C;

    private enum DragState { Idle, Active }

    private readonly object _syncRoot = new();

    private IntPtr _hwnd;
    private volatile bool _enabled;
    private volatile bool _isRegistered;
    private DragState _state;
    private long _incompleteFrameStartTick;
    private long _rawInputCount;
    private long _parseSuccessCount;
    private long _parseFailureCount;
    private long _activationCount;
    private long _moveAttemptCount;
    private long _moveSuccessCount;
    private long _releaseCount;
    private int _lastParsedCount;
    private int _lastContactCount;

    // 手势识别：3 个 contact ID
    private readonly int[] _gestureIds = new int[3];
    private int _gestureContactCount;

    // Per-contact 位置追踪（索引对应 _gestureIds）
    private readonly int[] _gestureLastX = new int[3];
    private readonly int[] _gestureLastY = new int[3];
    private readonly bool[] _gestureLastValid = new bool[3];

    // HID 解析/拼帧缓冲
    private readonly TouchpadContact[] _frameContacts = new TouchpadContact[MaxContactCount];
    private readonly TouchpadContact[] _partialContacts = new TouchpadContact[MaxContactCount];
    private int _partialContactCount;
    private uint _expectedContactCount;
    private readonly ConcurrentQueue<string> _calibrationRecords = new();
#if DEBUG
    private long _calibrationSequence;
#endif
    private readonly System.Collections.Generic.List<CalibrationFrame> _calibrationFrames = new();
    private volatile bool _calibrationActive;
    private volatile bool _calibrationSingleFingerDown;
    private bool _calibrationWasSingleFinger;
    private int _calibrationStroke;
    private int _calibrationCapturedFrameCount;

    public bool HasTouchpad => PrecisionTouchpadHelper.HasPrecisionTouchpad();

    public void Initialize(IntPtr hwnd, bool enabled)
    {
        _hwnd = hwnd;
        SetEnabled(enabled);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_syncRoot)
        {
            _enabled = enabled;
            if (enabled)
            {
                EnsureInputRegistered();
            }
            else
            {
                EndDrag();
                if (!_calibrationActive)
                    UnregisterInput();
            }
        }
    }

    private void EnsureInputRegistered()
    {
        if (!_isRegistered && _hwnd != IntPtr.Zero)
            _isRegistered = PrecisionTouchpadHelper.RegisterInput(_hwnd);
    }

    private void UnregisterInput()
    {
        if (!_isRegistered) return;
        if (PrecisionTouchpadHelper.UnregisterInput())
            _isRegistered = false;
    }

    internal string GetDiagnosticsSnapshot() =>
        $"enabled={_enabled}; registered={_isRegistered}; hasTouchpad={HasTouchpad}; " +
        $"raw={Interlocked.Read(ref _rawInputCount)}; parsed={Interlocked.Read(ref _parseSuccessCount)}; " +
        $"parseFailed={Interlocked.Read(ref _parseFailureCount)}; lastParsed={Volatile.Read(ref _lastParsedCount)}; " +
        $"lastContacts={Volatile.Read(ref _lastContactCount)}; activations={Interlocked.Read(ref _activationCount)}; " +
        $"moveAttempts={Interlocked.Read(ref _moveAttemptCount)}; moveSuccess={Interlocked.Read(ref _moveSuccessCount)}; " +
        $"releases={Interlocked.Read(ref _releaseCount)}";

    public bool HandleWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_TIMER && wParam == ReleaseTimerId)
        {
            KillTimer(_hwnd, (nuint)ReleaseTimerId);
            _calibrationSingleFingerDown = false;
            lock (_syncRoot) { EndDrag(); }
            return true;
        }

        if (message == PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE)
        {
            PrecisionTouchpadHelper.InvalidateDeviceCache();
            return true;
        }

        if ((!_enabled && !_calibrationActive) || !_isRegistered || message != PrecisionTouchpadHelper.WM_INPUT)
            return false;

        Interlocked.Increment(ref _rawInputCount);
        if (!PrecisionTouchpadHelper.TryParseInput(lParam, _frameContacts, out var parsedCount, out var contactCount))
        {
            Interlocked.Increment(ref _parseFailureCount);
            return true;
        }

        Interlocked.Increment(ref _parseSuccessCount);
        Volatile.Write(ref _lastParsedCount, parsedCount);
        Volatile.Write(ref _lastContactCount, (int)contactCount);

        lock (_syncRoot)
        {
            var frameCount = BuildFrame(parsedCount, contactCount);
            if (frameCount > 0)
            {
                ProcessFrame(frameCount);
                RecordCalibrationFrame(frameCount);
            }
        }

        return true;
    }

    internal string[] DrainCalibrationRecords()
    {
        var records = new System.Collections.Generic.List<string>();
        while (_calibrationRecords.TryDequeue(out var record))
            records.Add(record);
        return records.ToArray();
    }

    internal void ConfigurePointerCurve(double lowGain, double highGain, double accelerationStart, double accelerationEnd)
    {
        MouseInputHelper.ConfigureCurve(lowGain, highGain, accelerationStart, accelerationEnd);
    }

    internal bool IsCalibrationFingerDown => _calibrationActive && _calibrationSingleFingerDown;
    internal int CalibrationCapturedFrameCount => Volatile.Read(ref _calibrationCapturedFrameCount);

    internal void BeginCalibration()
    {
        lock (_syncRoot)
        {
            EndDrag();
            EnsureInputRegistered();
            _calibrationFrames.Clear();
            Volatile.Write(ref _calibrationCapturedFrameCount, 0);
            _calibrationStroke = 0;
            _calibrationWasSingleFinger = false;
            _calibrationSingleFingerDown = false;
            _calibrationActive = true;
        }
    }

    internal TouchpadCalibrationResult CompleteCalibration()
    {
        lock (_syncRoot)
        {
            _calibrationActive = false;
            _calibrationSingleFingerDown = false;
            var result = FitCalibrationCurve(_calibrationFrames);
            if (!_enabled)
                UnregisterInput();
            return result;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            _enabled = false;
            _calibrationActive = false;
            EndDrag();
            UnregisterInput();
        }
    }

    // ─── 状态机 ──────────────────────────────────────────────────────────────

    private void ProcessFrame(int contactCount)
    {
        SetTimer(_hwnd, (nuint)ReleaseTimerId,
            _calibrationActive ? CalibrationReleaseTimeoutMs : ReleaseTimeoutMs,
            IntPtr.Zero);

        switch (_state)
        {
            case DragState.Idle:
                if (contactCount == 3)
                {
                    SetGestureIds(_frameContacts, contactCount);
                    InitGesturePositions(_frameContacts, contactCount);
                    _state = DragState.Active;
                    MouseInputHelper.BeginMotion();
                    MouseInputHelper.MouseDown();
                    Interlocked.Increment(ref _activationCount);
                }
                break;

            case DragState.Active:
                var shared = CountSharedGestureIds(_frameContacts, contactCount);
                if (contactCount > 3 || shared < 2)
                {
                    EndDrag();
                    break;
                }

                // 部分触控板会在多子报告拼帧边界短暂只上报两根手指。
                // 给这种瞬时漏报 40ms 容错；真正抬起手指仍会快速释放。
                if (contactCount < 3 || shared < 3)
                {
                    if (_incompleteFrameStartTick == 0)
                        _incompleteFrameStartTick = Environment.TickCount64;
                    else if (Environment.TickCount64 - _incompleteFrameStartTick >= 40)
                    {
                        EndDrag();
                        break;
                    }
                }
                else
                {
                    _incompleteFrameStartTick = 0;
                }

                var (dx, dy, matched) = ComputeDeltaAndUpdate(_frameContacts, contactCount);
                if (matched >= 2)
                {
                    Interlocked.Increment(ref _moveAttemptCount);
                    if (MouseInputHelper.MoveWithSystemAcceleration(dx, dy))
                        Interlocked.Increment(ref _moveSuccessCount);
                }
                break;
        }
    }

    // ─── 帧拼接 ──────────────────────────────────────────────────────────────

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

        for (var i = 0; i < count; i++)
        {
            var c = contacts[i];
            for (var j = 0; j < _gestureContactCount; j++)
            {
                if (_gestureIds[j] != c.ContactId) continue;
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

    private void RecordCalibrationFrame(int contactCount)
    {
        if (_calibrationActive)
        {
            _calibrationSingleFingerDown = contactCount == 1;
            if (contactCount == 1)
            {
                if (!_calibrationWasSingleFinger)
                    _calibrationStroke++;
                _calibrationWasSingleFinger = true;
            }
            else
            {
                _calibrationWasSingleFinger = false;
            }
        }

        if (contactCount != 1 && contactCount != 3)
            return;

        double rawX = 0, rawY = 0;
        for (var i = 0; i < contactCount; i++)
        {
            rawX += _frameContacts[i].X;
            rawY += _frameContacts[i].Y;
        }
        rawX /= contactCount;
        rawY /= contactCount;

        if (!MouseInputHelper.TryGetCursorPosition(out var cursorX, out var cursorY))
            return;

        if (_calibrationActive && contactCount == 1)
        {
            _calibrationFrames.Add(new CalibrationFrame(
                Stopwatch.GetTimestamp(), rawX, rawY, cursorX, cursorY, _calibrationStroke));
            Interlocked.Increment(ref _calibrationCapturedFrameCount);
        }

#if DEBUG
        var line = string.Join(",",
            Interlocked.Increment(ref _calibrationSequence).ToString(CultureInfo.InvariantCulture),
            Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture),
            contactCount.ToString(CultureInfo.InvariantCulture),
            rawX.ToString("0.###", CultureInfo.InvariantCulture),
            rawY.ToString("0.###", CultureInfo.InvariantCulture),
            cursorX.ToString(CultureInfo.InvariantCulture),
            cursorY.ToString(CultureInfo.InvariantCulture));
        _calibrationRecords.Enqueue(line);
#endif
    }

    private static TouchpadCalibrationResult FitCalibrationCurve(
        System.Collections.Generic.List<CalibrationFrame> frames)
    {
        var virtualLeft = GetSystemMetrics(76);  // SM_XVIRTUALSCREEN
        var virtualTop = GetSystemMetrics(77);   // SM_YVIRTUALSCREEN
        var virtualRight = virtualLeft + GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
        var virtualBottom = virtualTop + GetSystemMetrics(79);  // SM_CYVIRTUALSCREEN
        var samples = new System.Collections.Generic.List<CalibrationSample>();
        var sampleSegment = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            var previous = frames[i - 1];
            var current = frames[i];
            if (current.Stroke != previous.Stroke)
            {
                sampleSegment++;
                continue;
            }
            var elapsedMs = (current.Timestamp - previous.Timestamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < 2.0 || elapsedMs > 32.0)
            {
                sampleSegment++;
                continue;
            }

            var dx = current.RawX - previous.RawX;
            var dy = current.RawY - previous.RawY;
            var cursorDx = current.CursorX - previous.CursorX;
            var cursorDy = current.CursorY - previous.CursorY;
            if (Math.Abs(dx) > 100 || Math.Abs(dy) > 100 ||
                Math.Abs(cursorDx) > 250 || Math.Abs(cursorDy) > 250) continue;

            // Once the pointer reaches the desktop boundary Windows clamps its coordinates even
            // though the finger keeps moving. Those samples contain no usable native gain data.
            if (virtualRight > virtualLeft && virtualBottom > virtualTop &&
                (previous.CursorX <= virtualLeft + 2 || previous.CursorX >= virtualRight - 3 ||
                 previous.CursorY <= virtualTop + 2 || previous.CursorY >= virtualBottom - 3 ||
                 current.CursorX <= virtualLeft + 2 || current.CursorX >= virtualRight - 3 ||
                 current.CursorY <= virtualTop + 2 || current.CursorY >= virtualBottom - 3))
            {
                sampleSegment++;
                continue;
            }

            var speed = Math.Sqrt(dx * dx + dy * dy) * 8.0 / elapsedMs;
            samples.Add(new CalibrationSample(dx, dy, cursorDx, cursorDy, speed, sampleSegment));
        }

        if (samples.Count < 120)
            return default;

        // Raw digitizer and native cursor messages arrive a few frames apart. Pick the lag with
        // the strongest directional correlation before fitting the pointer curve.
        var bestLag = 0;
        var bestCorrelation = double.NegativeInfinity;
        for (var lag = 0; lag <= 4; lag++)
        {
            double dot = 0, rawEnergy = 0, cursorEnergy = 0;
            for (var i = 0; i + lag < samples.Count; i++)
            {
                var raw = samples[i];
                var cursor = samples[i + lag];
                if (raw.Stroke != cursor.Stroke) continue;
                dot += raw.Dx * cursor.CursorDx + raw.Dy * cursor.CursorDy;
                rawEnergy += raw.Dx * raw.Dx + raw.Dy * raw.Dy;
                cursorEnergy += cursor.CursorDx * cursor.CursorDx + cursor.CursorDy * cursor.CursorDy;
            }
            var correlation = rawEnergy > 0 && cursorEnergy > 0
                ? dot / Math.Sqrt(rawEnergy * cursorEnergy)
                : 0;
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }

        var pairs = new System.Collections.Generic.List<CalibrationPair>();
        for (var i = 0; i + bestLag < samples.Count; i++)
        {
            var raw = samples[i];
            var cursor = samples[i + bestLag];
            if (raw.Stroke != cursor.Stroke || raw.Speed < 1.0) continue;
            pairs.Add(new CalibrationPair(raw.Dx, raw.Dy, cursor.CursorDx, cursor.CursorDy, raw.Speed));
        }
        if (pairs.Count < 100)
            return default;

        double bestScore = double.PositiveInfinity;
        double bestLow = 0, bestHigh = 0, bestStart = 0, bestEnd = 0;
        for (var start = 2.0; start <= 24.0; start += 2.0)
        {
            for (var end = Math.Max(25.0, start + 10.0); end <= 85.0; end += 5.0)
            {
                double s00 = 0, s01 = 0, s11 = 0, t0 = 0, t1 = 0;
                foreach (var pair in pairs)
                {
                    var rr = pair.Dx * pair.Dx + pair.Dy * pair.Dy;
                    var rc = pair.Dx * pair.CursorDx + pair.Dy * pair.CursorDy;
                    var weight = SmoothStepValue(start, end, pair.Speed);
                    s00 += rr;
                    s01 += rr * weight;
                    s11 += rr * weight * weight;
                    t0 += rc;
                    t1 += rc * weight;
                }

                var determinant = s00 * s11 - s01 * s01;
                if (Math.Abs(determinant) < 0.0001) continue;
                var low = (t0 * s11 - t1 * s01) / determinant;
                var delta = (s00 * t1 - s01 * t0) / determinant;
                var high = low + delta;
                if (low < 0.05 || low > 2.0 || high < low || high > 4.0) continue;

                double error = 0;
                foreach (var pair in pairs)
                {
                    var gain = low + delta * SmoothStepValue(start, end, pair.Speed);
                    var ex = pair.CursorDx - pair.Dx * gain;
                    var ey = pair.CursorDy - pair.Dy * gain;
                    error += ex * ex + ey * ey;
                }
                if (error < bestScore)
                {
                    bestScore = error;
                    bestLow = low;
                    bestHigh = high;
                    bestStart = start;
                    bestEnd = end;
                }
            }
        }

        if (!double.IsFinite(bestScore))
            return default;

        double cursorEnergyTotal = 0;
        foreach (var pair in pairs)
            cursorEnergyTotal += pair.CursorDx * pair.CursorDx + pair.CursorDy * pair.CursorDy;
        var quality = cursorEnergyTotal > 0
            ? Math.Clamp(1.0 - bestScore / cursorEnergyTotal, 0.0, 1.0)
            : 0;

        return new TouchpadCalibrationResult(
            true,
            Math.Round(bestLow, 3),
            Math.Round(bestHigh, 3),
            bestStart,
            bestEnd,
            pairs.Count,
            quality);
    }

    private static double SmoothStepValue(double start, double end, double value)
    {
        var t = Math.Clamp((value - start) / (end - start), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private readonly record struct CalibrationFrame(
        long Timestamp, double RawX, double RawY, int CursorX, int CursorY, int Stroke);

    private readonly record struct CalibrationSample(
        double Dx, double Dy, double CursorDx, double CursorDy, double Speed, int Stroke);

    private readonly record struct CalibrationPair(
        double Dx, double Dy, double CursorDx, double CursorDy, double Speed);

    private void ResetGesture()
    {
        _gestureContactCount = 0;
        for (var j = 0; j < 3; j++)
            _gestureLastValid[j] = false;
    }

    private void EndDrag()
    {
        KillTimer(_hwnd, (nuint)ReleaseTimerId);
        if (_state == DragState.Active)
        {
            MouseInputHelper.MouseUp();
            Interlocked.Increment(ref _releaseCount);
        }
        _state = DragState.Idle;
        ResetGesture();
        _partialContactCount = 0;
        _expectedContactCount = 0;
        _incompleteFrameStartTick = 0;
        MouseInputHelper.ResetDecimal();
    }

    // ─── SendInput ────────────────────────────────────────────────────────────

    private static class MouseInputHelper
    {
        // Measured against this precision touchpad's native one-finger cursor path. Raw PTP units
        // map to roughly 0.25 px/unit at low speed and 0.82 px/unit at high speed.
        private static double _nativeLowSpeedGain = 0.25;
        private static double _nativeHighSpeedGain = 0.82;
        private static double _accelerationStart = 6.0;
        private static double _accelerationEnd = 50.0;
        private const uint InputMouse = 0;
        private const uint MouseeventfMove = 0x0001;
        private const uint MouseeventfLeftdown = 0x0002;
        private const uint MouseeventfLeftup = 0x0004;

        private static readonly INPUT[] SingleInput = new INPUT[1];

        private static double _remainderX;
        private static double _remainderY;
        private static long _lastMoveTimestamp;

        public static void BeginMotion()
        {
            ResetDecimal();
            _lastMoveTimestamp = Stopwatch.GetTimestamp();
        }

        public static void ConfigureCurve(double lowGain, double highGain, double accelerationStart, double accelerationEnd)
        {
            _nativeLowSpeedGain = Math.Clamp(lowGain, 0.05, 3.0);
            _nativeHighSpeedGain = Math.Clamp(highGain, _nativeLowSpeedGain, 4.0);
            _accelerationStart = Math.Clamp(accelerationStart, 1.0, 40.0);
            _accelerationEnd = Math.Clamp(accelerationEnd, _accelerationStart + 5.0, 120.0);
        }

        public static void ResetDecimal()
        {
            _remainderX = 0;
            _remainderY = 0;
            _lastMoveTimestamp = 0;
        }

        public static bool MoveWithSystemAcceleration(float dx, float dy)
        {
            // Keep cursor updates synchronous, but use one continuous, velocity-based gain for
            // both axes. Applying the legacy mouse thresholds independently to each HID packet
            // caused abrupt 1x/2x/4x jumps and made diagonal motion feel detached from the fingers.
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = _lastMoveTimestamp == 0
                ? 8.0
                : (now - _lastMoveTimestamp) * 1000.0 / Stopwatch.Frequency;
            _lastMoveTimestamp = now;

            // PTP devices normally report around 8 ms. Normalize packet distance by elapsed time
            // so acceleration follows finger velocity instead of the device's report frequency.
            elapsedMs = Math.Clamp(elapsedMs, 2.0, 32.0);
            var packetDistance = Math.Sqrt(dx * dx + dy * dy);
            var normalizedSpeed = packetDistance * 8.0 / elapsedMs;
            var multiplier = GetSmoothPointerMultiplier(normalizedSpeed);

            var accumulatedX = dx * multiplier + _remainderX;
            var accumulatedY = dy * multiplier + _remainderY;
            var moveX = (int)Math.Truncate(accumulatedX);
            var moveY = (int)Math.Truncate(accumulatedY);
            _remainderX = accumulatedX - moveX;
            _remainderY = accumulatedY - moveY;
            if (moveX == 0 && moveY == 0) return true;
            return GetCursorPos(out var cursor)
                && SetCursorPos(cursor.X + moveX, cursor.Y + moveY);
        }

        private static double GetSmoothPointerMultiplier(double speed)
        {
            // These endpoints and transition range come from the paired one-finger/three-finger
            // capture rather than legacy mouse thresholds. The continuous curve retains precise
            // low-speed tracking and reaches the native fast-swipe distance without a gain jump.
            var acceleration = SmoothStep(_accelerationStart, _accelerationEnd, speed);
            return _nativeLowSpeedGain
                + (_nativeHighSpeedGain - _nativeLowSpeedGain) * acceleration;
        }

        private static double SmoothStep(double start, double end, double value)
        {
            var t = Math.Clamp((value - start) / (end - start), 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }

        public static void MouseDown() => _ = Send(new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT { dwFlags = MouseeventfLeftdown }
        });

        public static void MouseUp() => _ = Send(new INPUT
        {
            type = InputMouse,
            mi = new MOUSEINPUT { dwFlags = MouseeventfLeftup }
        });

        public static bool TryGetCursorPosition(out int x, out int y)
        {
            if (GetCursorPos(out var point))
            {
                x = point.X;
                y = point.Y;
                return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        private static bool Send(INPUT input)
        {
            input.mi.dwExtraInfo = (IntPtr)SyntheticInputTag;
            SingleInput[0] = input;
            return SendInput(1, SingleInput, Marshal.SizeOf<INPUT>()) == 1;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public MOUSEINPUT mi;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nuint SetTimer(IntPtr hWnd, nuint nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool KillTimer(IntPtr hWnd, nuint uIDEvent);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
