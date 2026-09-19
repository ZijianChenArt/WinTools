using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinTools.Services;

internal static class AudioDeviceService
{
    internal sealed record Device(string Id, string Name, int Flow, bool Default, bool Communications);

    internal static List<Device> Read()
    {
        var enumerator = (IEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
        try
        {
            var result = new List<Device>();
            for (var flow = 0; flow < 2; flow++)
            {
                var main = DefaultId(enumerator, flow, 1);
                var communications = DefaultId(enumerator, flow, 2);
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, 1, out var collection));
                try
                {
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for (uint index = 0; index < count; index++)
                    {
                        Marshal.ThrowExceptionForHR(collection.Item(index, out var device));
                        try
                        {
                            Marshal.ThrowExceptionForHR(device.GetId(out var id));
                            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out var properties));
                            try
                            {
                                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                                Marshal.ThrowExceptionForHR(properties.GetValue(ref key, out var value));
                                try
                                {
                                    var name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
                                    result.Add(new Device(id, name ?? "未命名音频设备", flow, id == main, id == communications));
                                }
                                finally { PropVariantClear(ref value); }
                            }
                            finally { Marshal.ReleaseComObject(properties); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(collection); }
            }
            return result;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static string? DefaultId(IEnumerator enumerator, int flow, int role)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, role, out var device) < 0) return null;
        try { Marshal.ThrowExceptionForHR(device.GetId(out var id)); return id; }
        finally { Marshal.ReleaseComObject(device); }
    }

    internal static void Select(Device device)
    {
        // PolicyConfig is the Windows desktop default-endpoint interface; failures are surfaced by the picker.
        var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
        try
        {
            for (var role = 0; role < 3; role++)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(device.Id, role));
        }
        finally { Marshal.ReleaseComObject(policy); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out ICollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant value);
    }
    // Slots follow the native IPolicyConfig ABI. Unused slots are never invoked.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void Slot0(); void Slot1(); void Slot2(); void Slot3(); void Slot4();
        void Slot5(); void Slot6(); void Slot7(); void Slot8(); void Slot9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
