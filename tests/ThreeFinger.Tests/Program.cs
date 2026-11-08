using System.Runtime.InteropServices;
using WinTools;

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
var axis = TouchpadAxis.FromHid(0, 4000, 0, 1200, 0x11, 0xE)!.Value;
Check(axis.HimetricExtent == 12000 && axis.ToHimetric(2000) == 6000, "SI physical units");
var inches = TouchpadAxis.FromHid(100, 1100, 0, 400, 0x13, 0xFFFFFFFE)!.Value;
Check(Math.Abs(inches.HimetricExtent - 10160) < .001 && inches.ToHimetric(100) == 0, "English units and signed exponent");
Check(TouchpadAxis.FromHid(0, 0, 0, 100, 0x11, 0) == null, "zero logical span rejected");
Check(TouchpadAxis.FromHid(0, 4000, 0, 0, 0x11, 0) == null, "missing physical size rejected");
Check(TouchpadAxis.FromHid(0, 4000, 0, 100, 0, 0) == null, "unknown units rejected");
var geometry = new TouchpadGeometry(axis, axis);
TouchpadContact[] Frame(int shift = 0) => [new(1, 1000 + shift, 1000), new(2, 1600 + shift, 1000), new(3, 2200 + shift, 1000)];
var fake = new FakeInput(); long now = 0;
using var service = new ThreeFingerDragService(fake, () => now);
service.Initialize(IntPtr.Zero, true);
void Send(TouchpadContact[] contacts, uint count = 3, int device = 1, TouchpadGeometry? g = null) => service.ProcessInput(device, g ?? geometry, contacts, count);
Send(Frame()[..1], 1); Check(!fake.Held, "single finger untouched");
Send(Frame()); Check(fake.Held && fake.Starts == 1, "three-finger press");
Send(Frame(10).Reverse().ToArray()); Check(fake.Dx == 10 && fake.Dy == 0, "IDs preserve translation when reports reorder");
Send([new(1,1010,1000),new(1,1010,1000),new(1,1010,1000)]);
Check(fake.Held && fake.Starts == 1 && fake.Dx == 10, "malformed duplicate IDs do not lift or move a valid ongoing drag");
Send(Frame(20)[..2], 2); Check(fake.Dx == 20, "temporary missing finger does not jump centroid");
Send(Frame(30)); Check(fake.Dx == 30, "returning contact does not catch up with stale position");
now = 1; Send(Frame(40)[..2], 2);
now = 42; service.CheckTimeout(); Check(!fake.Held, "missing finger releases at 40ms without further input");
Send([], 0); Send(Frame()); now += 121; service.CheckTimeout(); Check(fake.Held, "short raw input queue stall does not generate a click");
now += 130; service.CheckTimeout(); Check(!fake.Held, "lost input releases at timeout");
Send(Frame()); Check(!fake.Held, "lost-input gesture must lift before restarting");
Send([], 0); Send(Frame());
Send([Frame()[0], Frame()[1], new(3, 2200, 1000, false)]);
now += 41; service.CheckTimeout(); Check(!fake.Held, "reported lifted contacts complete the frame before filtering");
Send([], 0); Send(Frame());
Send([new(1,1000,1000,false)], 3); Send([new(2,1600,1000,false)], 0); Send([new(3,2200,1000,false)], 0);
Check(!fake.Held, "split frame with three lifted tips releases immediately");
Send([],0); Send(Frame()); Send([new(1,1000,1000,false)],0); Check(!fake.Held,"zero-count final tip releases immediately");
Send([], 0); Send(Frame()); service.SetEnabled(false); Check(!fake.Held, "disable releases button");
service.SetEnabled(true);
Send([], 0); Send(Frame()); var deviceEventStarts = fake.Starts;
service.HandleWindowMessage(PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE, 1, 99);
service.HandleWindowMessage(PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE, 2, 99);
service.HandleWindowMessage(PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE, 1, 1);
Send(Frame(10));
Check(fake.Held && fake.Starts == deviceEventStarts && fake.Dx == 10, "virtual device arrival/removal cannot split an active drag into clicks");
service.HandleWindowMessage(PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE, 2, 1);
Check(!fake.Held, "physical source removal still releases");
Send([], 0);
Send([Frame()[0]], 3); Send([Frame()[1]], 3); Check(!fake.Held, "partial reports do not activate early");
Send([Frame()[2]], 3); Check(fake.Held, "same-count partial reports assemble");
Send([], 0); Send([Frame()[0]], 3); Send([Frame()[1]], 0); Send([Frame()[2]], 0);
Check(fake.Held, "zero-count continuation reports assemble");
Send([], 0); Send([Frame()[0]], 3); Send([Frame()[0]], 3); Send([Frame()[2]], 3);
Check(!fake.Held, "duplicate contact is not a third finger");
Send([], 0); Send([Frame()[0], Frame()[0], Frame()[2]]); Check(!fake.Held, "duplicate ID in a complete report is rejected");
Send([], 0); Send(Frame()); var lockedStarts = fake.Starts;
Send(Frame(), device: 2); Check(fake.Held && fake.Starts == lockedStarts, "another device cannot steal an active gesture");
Send([new(0,2000,2000)], 1, device: 99);
Send([], 0, device: 99);
Send(Frame(10)); Check(fake.Held && fake.Starts == lockedStarts && fake.Dx == 10, "virtual single-finger HID feedback cannot release or rearm a physical gesture");
Send([], 0); Send(Frame(), device: 2); Check(fake.Held && fake.Starts == lockedStarts + 1, "new source may start after previous source lifts");
Send([], 0, device: 2);
Send([], 0); fake.FailStart = true; var starts = fake.Starts; Send(Frame()); Send(Frame());
Check(!fake.Held && fake.Starts == starts, "failed start never holds button or retries while fingers remain down");
fake.FailStart = false; Send([], 0); Send(Frame()); fake.FailMove = true; Send(Frame(5)); Check(!fake.Held, "injection failure releases");
service.ProcessInput(1, null, [], 0); service.ProcessInput(1, null, Frame(), 3); Check(!fake.Held, "missing device geometry never presses button");
Check(Marshal.SizeOf<NativeTouchpadInput.PointerInfo>() == (IntPtr.Size == 8 ? 96 : 88), "POINTER_INFO ABI");
Check(Marshal.SizeOf<NativeTouchpadInput.PointerTypeInfo>() == (IntPtr.Size == 8 ? 152 : 144), "POINTER_TYPE_INFO ABI");
Console.WriteLine("Three-finger physical units, report assembly, tracking, timeout, cancellation and failure checks passed.");
if (args.Contains("--device"))
{
    Console.WriteLine("Native APIs: " + NativeTouchpadInput.IsSupported);
    Console.WriteLine(ThreeFingerDragService.GetAvailability());
    using var host = new TouchpadInputHost();
    host.Start(true);
    host.SetEnabled(false);
    host.Dispose();
    Console.WriteLine("Raw input host enable/disable/dispose completed.");
}
if (args.Contains("--native-motion")) NativeMotionProbe.Run();
if (Array.IndexOf(args,"--capture") is >= 0 and var captureIndex) HardwareCapture.Run(args[captureIndex + 1]);
if (Array.IndexOf(args,"--replay") is >= 0 and var replayIndex)
{
    var expectedIndex = Array.IndexOf(args,"--expected-gestures");
    HardwareReplay.Run(args[replayIndex + 1], expectedIndex < 0 ? 1 : int.Parse(args[expectedIndex + 1]));
}

sealed class FakeInput : IThreeFingerInput
{
    public bool Held, FailStart, FailMove;
    public int Starts, Ends;
    public double Dx, Dy;
    public string? Error => "test failure";
    public bool Begin(TouchpadGeometry g, double x, double y) { if (FailStart) return false; Held = true; Starts++; Dx = Dy = 0; return true; }
    public bool Move(double x, double y) { Dx += x; Dy += y; return !FailMove; }
    public void End() { if (Held) { Ends++; Held = false; } }
    public void Dispose() => End();
}
