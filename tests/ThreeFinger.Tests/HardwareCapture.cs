using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinTools;

internal static class HardwareCapture
{
    internal sealed record Packet(long Ms, long Device, bool Parsed, uint Count, TouchpadContact[] Contacts, TouchpadGeometry? Geometry);
    internal sealed record Button(long Ms, int Message, long Tag, uint Flags);
    internal sealed record DeviceChange(long Ms, long Device, long Kind);
    internal sealed record Capture(Packet[] Packets, Button[] Buttons, DeviceChange[] Changes);

    internal static void Run(string path)
    {
        var packets = new List<Packet>(); var buttons = new List<Button>(); var changes = new List<DeviceChange>();
        var clock = Stopwatch.StartNew(); long lastThree = -1;
        var contacts = new TouchpadContact[8];
        var window = CreateWindowEx(0, "STATIC", "WinTools read-only touchpad capture", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0 || !PrecisionTouchpadHelper.RegisterInput(window)) throw new Exception("Cannot start read-only HID capture");
        HookProc callback = (code, message, data) =>
        {
            if (code >= 0 && (message == 0x201 || message == 0x202))
            {
                var mouse = Marshal.PtrToStructure<Mouse>(data);
                if (buttons.Count < 1000) buttons.Add(new(clock.ElapsedMilliseconds, (int)message, (long)mouse.Extra, mouse.Flags));
            }
            return CallNextHookEx(0, code, message, data);
        };
        var hook = SetWindowsHookEx(14, callback, 0, 0);
        Console.WriteLine("Read-only hardware capture ready. Move three fingers and lift; capture stops automatically after the gesture.");
        try
        {
            while (clock.ElapsedMilliseconds < 600000 && packets.Count < 100000)
            {
                while (PeekMessage(out var msg, 0, 0, 0, 1))
                {
                    if (msg.Id == PrecisionTouchpadHelper.WM_INPUT)
                    {
                        var ok = PrecisionTouchpadHelper.TryParseInput(msg.L, contacts, out var parsed, out var count, out var device, out var geometry);
                        packets.Add(new(clock.ElapsedMilliseconds, (long)device, ok, count, contacts.AsSpan(0, parsed).ToArray(), geometry));
                        if (ok && count >= 3) lastThree = clock.ElapsedMilliseconds;
                    }
                    else if (msg.Id == PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE)
                        changes.Add(new(clock.ElapsedMilliseconds, (long)msg.L, (long)msg.W));
                    TranslateMessage(ref msg); DispatchMessage(ref msg);
                }
                if (lastThree >= 0 && clock.ElapsedMilliseconds - lastThree > 1800) break;
                Thread.Sleep(1);
            }
        }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            PrecisionTouchpadHelper.UnregisterInput(); DestroyWindow(window); GC.KeepAlive(callback);
            File.WriteAllText(path, JsonSerializer.Serialize(new Capture(packets.ToArray(), buttons.ToArray(), changes.ToArray()), new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Saved {packets.Count} raw packets, {buttons.Count} button events, {changes.Count} device notifications to {path}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { public Point Position; public uint Data,Flags,Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public nint Window; public uint Id; public nuint W; public nint L; public uint Time; public Point Position; public uint Private; }
    private delegate nint HookProc(int code,nint message,nint data);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookEx(int id,HookProc callback,nint module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg msg,nint hwnd,uint min,uint max,uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Msg msg);
}
