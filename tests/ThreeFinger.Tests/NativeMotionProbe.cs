using System.Runtime.InteropServices;
using System.Diagnostics;
using WinTools;

internal static class NativeMotionProbe
{
    private static int _downs, _ups;
    private static ThreeFingerDragService? _feedbackService;
    private static int _feedbackNotifications;
    private static int _feedbackHidPackets;
    private static readonly HookProc Hook = (code, message, data) =>
    {
        if (code >= 0) { if (message == 0x201) _downs++; if (message == 0x202) _ups++; }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    };

    internal static void Run()
    {
        SetProcessDpiAwarenessContext(-4);
        GetCursorPos(out var original);
        var surface = CreateWindowEx(8, "STATIC", "WinTools native input integration check", 0x90000000,
            200, 200, 1000, 700, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (surface == IntPtr.Zero) throw new Exception("Cannot create isolated native test surface");
        var hook = SetWindowsHookEx(14, Hook, IntPtr.Zero, 0);
        if (hook == IntPtr.Zero) { DestroyWindow(surface); throw new Exception("Cannot observe input"); }
        try
        {
            using var listener = new TouchpadInputHost();
            listener.Start(true);
            var axis = TouchpadAxis.FromHid(0, 4000, 0, 1200, 0x11, 0xE)!.Value;
            var geometry = new TouchpadGeometry(axis, axis);
            using var input = new NativeTouchpadInput();
            SetCursorPos(650, 500); Pump(80);
            if (!input.BeginContact(geometry, 2000, 2000)) throw new Exception("Native contact start failed");
            Pump(80); input.End(); Pump(250);
            if (input.Error != null || _downs != 0 || _ups != 0) throw new Exception($"Stationary contact cancellation: downs={_downs}, ups={_ups}, error={input.Error}");
            Console.WriteLine("Stationary native contact cancellation: no click.");
            foreach (var interval in new[] { 16, 4 })
            {
                SetCursorPos(650, 500); Pump(80); GetCursorPos(out var before);
                if (!input.BeginContact(geometry, 2000, 2000)) throw new Exception("Native contact start failed");
                Pump(interval);
                for (var i = 0; i < 20; i++) { if (!input.Move(10, 0)) throw new Exception(input.Error); Pump(interval); }
                input.End(); Pump(150); GetCursorPos(out var after);
                if (input.Error != null || after.X <= before.X || Math.Abs(after.Y - before.Y) > 2 || _downs != 0 || _ups != 0)
                    throw new Exception("Native motion or release failed: " + input.Error);
                Console.WriteLine($"Native physical stroke {interval} ms/report: dx={after.X-before.X}, dy={after.Y-before.Y}; no button events.");
            }
            SetCursorPos(650, 500); Pump(80);
            using var dragWatcher = new GlobalDragWatcher();
            var detectedStarts = 0; var detectedEnds = 0;
            dragWatcher.DragStarted += (_, _) => Interlocked.Increment(ref detectedStarts);
            dragWatcher.DragEnded += (_, _) => Interlocked.Increment(ref detectedEnds);
            dragWatcher.Start();
            var timersBeforeDrag = Timer.ActiveCount;
            if (!input.Begin(geometry, 2000, 2000)) throw new Exception(input.Error);
            Pump(30);
            for (var i = 0; i < 10; i++) { if (!input.Move(10, 0)) throw new Exception(input.Error); Pump(8); }
            Pump(50);
            if (Volatile.Read(ref detectedStarts) != 1 || Timer.ActiveCount > timersBeforeDrag)
                throw new Exception("Drag detection failed or the 8ms fallback kept waking after detection");
            input.End(); Pump(250);
            if (Volatile.Read(ref detectedEnds) != 1) throw new Exception("Drag watcher did not report the matching release");
            dragWatcher.Stop();
            if (_downs != 1 || _ups != 1 || (GetAsyncKeyState(1) & 0x8000) != 0 || input.Error != null)
                throw new Exception($"Button pairing failed: downs={_downs}, ups={_ups}, error={input.Error}");
            Console.WriteLine("Native drag input pairing: exactly one down/up; no extra tap; left button released.");
            Console.WriteLine("Global drag detection: one start/end; fallback timer inactive while the drag remains held.");
            Console.WriteLine($"Raw Input device-change notifications during virtual contact creation/destruction: {listener.DeviceChanges}");
            listener.Dispose();
            if (!PrecisionTouchpadHelper.RegisterInput(surface)) throw new Exception("Cannot register lifecycle integration receiver");
            try
            {
                using var tracker = new ThreeFingerDragService();
                tracker.Initialize(IntPtr.Zero, true);
                _feedbackService = tracker;
                _feedbackNotifications = 0;
                _feedbackHidPackets = 0;
                var downsBefore = _downs; var upsBefore = _ups;
                SetCursorPos(650, 500); Pump(80); GetCursorPos(out var start);
                TouchpadContact[] Frame(int shift) => [new(1, 1000+shift, 1000), new(2, 1600+shift, 1000), new(3, 2200+shift, 1000)];
                tracker.ProcessInput(0x12345678, geometry, Frame(0), 3);
                Pump(30);
                if ((GetAsyncKeyState(1) & 0x8000) == 0) throw new Exception("Virtual device arrival prematurely released the drag");
                for (var i = 1; i <= 20; i++)
                {
                    tracker.ProcessInput(0x12345678, geometry, Frame(i*10), 3);
                    Pump(8);
                    if ((GetAsyncKeyState(1) & 0x8000) == 0) throw new Exception("Drag released during native device lifecycle notifications");
                }
                GetCursorPos(out var stop);
                tracker.ProcessInput(0x12345678, geometry, [], 0); Pump(250);
                if (_downs-downsBefore != 1 || _ups-upsBefore != 1 || stop.X <= start.X || _feedbackNotifications == 0 || _feedbackHidPackets == 0 || (GetAsyncKeyState(1) & 0x8000) != 0)
                    throw new Exception($"Lifecycle drag failed: downs={_downs-downsBefore}, ups={_ups-upsBefore}, dx={stop.X-start.X}, notifications={_feedbackNotifications}");
                Console.WriteLine($"Full tracker + real virtual-device notifications: one uninterrupted drag, dx={stop.X-start.X}, notifications={_feedbackNotifications}, parsedFeedbackHid={_feedbackHidPackets}, exactly one down/up.");
            }
            finally { _feedbackService = null; PrecisionTouchpadHelper.UnregisterInput(); }
        }
        finally
        {
            UnhookWindowsHookEx(hook); DestroyWindow(surface); SetCursorPos(original.X, original.Y);
        }
    }

    private static void Pump(int ms)
    {
        var clock = Stopwatch.StartNew();
        var contacts = new TouchpadContact[8];
        do
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                if (_feedbackService != null && msg.Id == PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE)
                {
                    _feedbackNotifications++;
                    _feedbackService.HandleWindowMessage(msg.Id, (nint)msg.WParam, msg.LParam);
                }
                else if (_feedbackService != null && msg.Id == PrecisionTouchpadHelper.WM_INPUT)
                {
                    if (PrecisionTouchpadHelper.TryParseInput(msg.LParam, contacts, out var count, out var reported, out var device, out var geometry))
                    {
                        _feedbackHidPackets++;
                        _feedbackService.ProcessInput(device, geometry, contacts.AsSpan(0, count), reported);
                    }
                }
                TranslateMessage(ref msg); DispatchMessage(ref msg);
            }
            Thread.Sleep(1);
        } while (clock.ElapsedMilliseconds < ms);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Position; public uint Private; }
    private delegate IntPtr HookProc(int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
}
