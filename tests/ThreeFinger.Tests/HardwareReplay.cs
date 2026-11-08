using System.Text.Json;
using WinTools;

internal static class HardwareReplay
{
    internal static void Run(string path, int expectedGestures)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var events = new List<(long Ms, bool Packet, JsonElement Value)>();
        foreach (var packet in document.RootElement.GetProperty("Packets").EnumerateArray())
            events.Add((packet.GetProperty("Ms").GetInt64(), true, packet));
        foreach (var change in document.RootElement.GetProperty("Changes").EnumerateArray())
            events.Add((change.GetProperty("Ms").GetInt64(), false, change));
        long now = 0; var input = new ReplayInput(() => now);
        using var tracker = new ThreeFingerDragService(input, () => now);
        tracker.Initialize(0, true);
        foreach (var item in events.OrderBy(e => e.Ms))
        {
            now = item.Ms;
            var beforeStarts = input.Starts; var beforeEnds = input.Ends;
            tracker.CheckTimeout();
            if (input.Ends != beforeEnds) Console.WriteLine($"Replay timeout at {now}");
            var value = item.Value;
            if (!item.Packet)
            {
                tracker.HandleWindowMessage(PrecisionTouchpadHelper.WM_INPUT_DEVICE_CHANGE,
                    (nint)value.GetProperty("Kind").GetInt64(), (nint)value.GetProperty("Device").GetInt64());
                continue;
            }
            if (!value.GetProperty("Parsed").GetBoolean()) continue;
            var contacts = value.GetProperty("Contacts").EnumerateArray().Select(c => new TouchpadContact(
                c.GetProperty("ContactId").GetInt32(), c.GetProperty("X").GetInt32(), c.GetProperty("Y").GetInt32(), c.GetProperty("IsDown").GetBoolean())).ToArray();
            var g = value.GetProperty("Geometry");
            TouchpadGeometry? geometry = g.ValueKind == JsonValueKind.Null ? null : new(Axis(g.GetProperty("X")), Axis(g.GetProperty("Y")));
            tracker.ProcessInput((nint)value.GetProperty("Device").GetInt64(), geometry, contacts, value.GetProperty("Count").GetUInt32());
            if (input.Starts != beforeStarts || input.Ends != beforeEnds)
                Console.WriteLine($"Replay transition at {now}: device={value.GetProperty("Device")}, count={value.GetProperty("Count")}, ids={string.Join(',',contacts.Select(c=>$"{c.ContactId}:{c.IsDown}"))}, starts={input.Starts}, ends={input.Ends}");
        }
        now += 251; tracker.CheckTimeout();
        Console.WriteLine($"Hardware replay: starts={input.Starts}, ends={input.Ends}, moves={input.Moves}, longestHoldMs={input.LongestHold}, held={input.Held}.");
        if (input.Starts != expectedGestures || input.Ends != expectedGestures || input.Moves < 10 || input.LongestHold < 1000 || input.Held)
            throw new Exception("Recorded gestures must remain continuous, with releases only at the observed physical lift boundaries.");
    }
    private static TouchpadAxis Axis(JsonElement axis) => new(axis.GetProperty("LogicalMin").GetInt32(), axis.GetProperty("LogicalMax").GetInt32(), axis.GetProperty("HimetricExtent").GetDouble());
    private sealed class ReplayInput(Func<long> clock) : IThreeFingerInput
    {
        internal int Starts, Ends, Moves; internal long LongestHold, Started; internal bool Held;
        public string? Error => null;
        public bool Begin(TouchpadGeometry geometry, double x, double y) { Starts++; Held = true; Started = clock(); return true; }
        public bool Move(double dx, double dy) { Moves++; return true; }
        public void End() { if (Held) { Held = false; Ends++; LongestHold = Math.Max(LongestHold, clock() - Started); } }
        public void Dispose() => End();
    }
}
