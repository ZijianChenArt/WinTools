using System;
using System.Linq;
using System.Runtime.InteropServices;
using WinTools.Services;

// Read-only integration check: never change the user's default devices or open UI.
var devices = AudioDeviceService.Read();
if (devices.Any(device => string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.Name)))
    throw new Exception("An endpoint is missing its identity or display name.");
if (devices.Select(device => device.Id).Distinct().Count() != devices.Count)
    throw new Exception("Duplicate endpoints.");
for (var flow = 0; flow < 2; flow++)
{
    var group = devices.Where(device => device.Flow == flow).ToList();
    if (group.Count(device => device.Default) > 1 || group.Count(device => device.Communications) > 1)
        throw new Exception("Multiple defaults in the same direction.");
    Console.WriteLine($"{(flow == 0 ? "Output" : "Input")}: {group.Count} active devices, {group.Count(device => device.Default)} default.");
}
var policy = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
var unknown = Marshal.GetIUnknownForObject(policy);
try
{
    var iid = new Guid("F8679F50-850A-41CF-9C72-430F290290C8");
    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var config));
    Marshal.Release(config);
    Console.WriteLine("Default-device switching interface available; no settings changed.");
}
finally { Marshal.Release(unknown); Marshal.ReleaseComObject(policy); }
