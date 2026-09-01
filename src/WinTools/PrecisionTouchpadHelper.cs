using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinTools;

/// <summary>精确触控板 Raw Input 解析辅助。</summary>
internal static class PrecisionTouchpadHelper
{
    public const uint WM_INPUT = 0x00FF;
    public const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;

    private const uint RidInput = 0x10000003;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RidiDeviceInfo = 0x2000000b;
    private const uint HidpStatusSuccess = 0x00110000;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const uint RimTypeHid = 2;
    private static readonly Dictionary<IntPtr, DeviceContext> DeviceContexts = new();
    private static readonly object SyncRoot = new();
    private static IntPtr _rawInputBuffer = IntPtr.Zero;
    private static int _rawInputBufferSize;

    public static bool HasPrecisionTouchpad()
    {
        uint deviceCount = 0;
        var listSize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (GetRawInputDeviceList(null, ref deviceCount, listSize) != 0 || deviceCount == 0)
            return false;

        var devices = new RAWINPUTDEVICELIST[deviceCount];
        if (GetRawInputDeviceList(devices, ref deviceCount, listSize) != deviceCount)
            return false;

        for (var i = 0; i < devices.Length; i++)
        {
            if (devices[i].dwType != RimTypeHid)
                continue;

            if (GetOrCreateDeviceContext(devices[i].hDevice)?.IsPrecisionTouchpad == true)
                return true;
        }

        return false;
    }

    public static bool RegisterInput(IntPtr hwndTarget)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x000D,
            usUsage = 0x0005,
            dwFlags = RidevInputSink | RidevDevNotify,
            hwndTarget = hwndTarget
        };

        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    /// <summary>停止接收精确触控板 Raw Input，避免功能关闭后仍处理每一帧 HID 消息。</summary>
    public static bool UnregisterInput()
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = 0x000D,
            usUsage = 0x0005,
            dwFlags = RidevRemove,
            hwndTarget = IntPtr.Zero
        };

        return RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    public static void InvalidateDeviceCache()
    {
        lock (SyncRoot)
        {
            foreach (var context in DeviceContexts.Values)
            {
                if (context.PreparsedData != IntPtr.Zero)
                    Marshal.FreeHGlobal(context.PreparsedData);
            }

            DeviceContexts.Clear();
        }
    }

    public static bool TryParseInput(IntPtr lParam, Span<TouchpadContact> contactsBuffer, out int parsedCount, out uint contactCount)
    {
        parsedCount = 0;
        contactCount = 0;

        uint rawInputSize = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref rawInputSize, headerSize) != 0 || rawInputSize == 0)
            return false;

        EnsureRawInputBuffer((int)rawInputSize);
        if (GetRawInputData(lParam, RidInput, _rawInputBuffer, ref rawInputSize, headerSize) != rawInputSize)
            return false;

        var rawInput = Marshal.PtrToStructure<RAWINPUT>(_rawInputBuffer);
        var context = GetOrCreateDeviceContext(rawInput.Header.hDevice);
        if (context == null || !context.IsPrecisionTouchpad || context.ValueCaps.Length == 0 || context.PreparsedData == IntPtr.Zero)
            return false;

        var singleReportSize = rawInput.Hid.dwSizeHid;
        var rawDataLength = checked((int)(singleReportSize * rawInput.Hid.dwCount));
        if (rawDataLength <= 0 || rawDataLength > rawInputSize)
            return false;

        var rawDataOffset = (int)rawInputSize - rawDataLength;
        var rawDataPointer = IntPtr.Add(_rawInputBuffer, rawDataOffset);
        var reportCount = (int)rawInput.Hid.dwCount;

        Span<TouchpadContactBuilder> builders = reportCount <= 8
            ? stackalloc TouchpadContactBuilder[8]
            : new TouchpadContactBuilder[reportCount];

        for (var i = 0; i < context.ValueCaps.Length; i++)
        {
            var valueCap = context.ValueCaps[i];
            for (var contactIndex = 0; contactIndex < reportCount; contactIndex++)
            {
                var reportPointer = IntPtr.Add(rawDataPointer, (int)(singleReportSize * contactIndex));
                if (HidP_GetUsageValue(
                        HIDP_REPORT_TYPE.HidP_Input,
                        valueCap.UsagePage,
                        valueCap.LinkCollection,
                        valueCap.Usage,
                        out var value,
                        context.PreparsedData,
                        reportPointer,
                        singleReportSize) != HidpStatusSuccess)
                    continue;

                if (valueCap.LinkCollection == 0)
                {
                    if (valueCap.UsagePage == 0x0D && valueCap.Usage == 0x54)
                        contactCount = value;
                    continue;
                }

                switch (valueCap.UsagePage, valueCap.Usage)
                {
                    case (0x0D, 0x51): builders[contactIndex].ContactId = (int)value; break;
                    case (0x01, 0x30): builders[contactIndex].X = (int)value; break;
                    case (0x01, 0x31): builders[contactIndex].Y = (int)value; break;
                }
            }

            for (var builderIndex = 0; builderIndex < reportCount; builderIndex++)
            {
                if ((contactCount == 0 || parsedCount < contactCount)
                    && parsedCount < contactsBuffer.Length
                    && builders[builderIndex].TryCreate(out var contact))
                {
                    contactsBuffer[parsedCount++] = contact;
                    builders[builderIndex].Clear();
                }
            }

            if (contactCount != 0 && parsedCount >= contactCount)
                break;
        }

        return parsedCount > 0;
    }

    private static void EnsureRawInputBuffer(int size)
    {
        if (_rawInputBuffer != IntPtr.Zero && _rawInputBufferSize >= size)
            return;

        if (_rawInputBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_rawInputBuffer);

        _rawInputBuffer = Marshal.AllocHGlobal(size);
        _rawInputBufferSize = size;
    }

    private static DeviceContext? GetOrCreateDeviceContext(IntPtr deviceHandle)
    {
        lock (SyncRoot)
        {
            if (DeviceContexts.TryGetValue(deviceHandle, out var cached))
                return cached;

            uint deviceInfoSize = 0;
            if (GetRawInputDeviceInfo(deviceHandle, RidiDeviceInfo, IntPtr.Zero, ref deviceInfoSize) != 0 || deviceInfoSize == 0)
                return null;

            var deviceInfo = new RID_DEVICE_INFO { cbSize = (uint)Marshal.SizeOf<RID_DEVICE_INFO>() };
            if (GetRawInputDeviceInfo(deviceHandle, RidiDeviceInfo, ref deviceInfo, ref deviceInfoSize) == unchecked((uint)-1))
                return null;

            var context = new DeviceContext
            {
                IsPrecisionTouchpad = deviceInfo.dwType == RimTypeHid
                    && deviceInfo.hid.usUsagePage == 0x000D
                    && deviceInfo.hid.usUsage == 0x0005
            };

            if (!context.IsPrecisionTouchpad)
            {
                DeviceContexts[deviceHandle] = context;
                return context;
            }

            uint preparsedDataSize = 0;
            if (GetRawInputDeviceInfo(deviceHandle, RidiPreparsedData, IntPtr.Zero, ref preparsedDataSize) != 0 || preparsedDataSize == 0)
            {
                DeviceContexts[deviceHandle] = context;
                return context;
            }

            var preparsedData = Marshal.AllocHGlobal((int)preparsedDataSize);
            if (GetRawInputDeviceInfo(deviceHandle, RidiPreparsedData, preparsedData, ref preparsedDataSize) != preparsedDataSize)
            {
                Marshal.FreeHGlobal(preparsedData);
                DeviceContexts[deviceHandle] = context;
                return context;
            }

            if (HidP_GetCaps(preparsedData, out var caps) != HidpStatusSuccess)
            {
                Marshal.FreeHGlobal(preparsedData);
                DeviceContexts[deviceHandle] = context;
                return context;
            }

            var valueCapsLength = caps.NumberInputValueCaps;
            var valueCaps = new HIDP_VALUE_CAPS[valueCapsLength];
            if (HidP_GetValueCaps(HIDP_REPORT_TYPE.HidP_Input, valueCaps, ref valueCapsLength, preparsedData) != HidpStatusSuccess)
            {
                Marshal.FreeHGlobal(preparsedData);
                DeviceContexts[deviceHandle] = context;
                return context;
            }

            var filtered = Array.FindAll(valueCaps, static c =>
                (c.UsagePage == 0x0D && c.Usage is 0x51 or 0x54) ||
                (c.UsagePage == 0x01 && c.Usage is 0x30 or 0x31));
            Array.Sort(filtered, static (left, right) => left.LinkCollection.CompareTo(right.LinkCollection));

            context.PreparsedData = preparsedData;
            context.ValueCaps = filtered;
            DeviceContexts[deviceHandle] = context;
            return context;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, ref RID_DEVICE_INFO pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern uint HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint HidP_GetValueCaps(HIDP_REPORT_TYPE reportType, [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint HidP_GetUsageValue(HIDP_REPORT_TYPE reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsedData, IntPtr report, uint reportLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT
    {
        public RAWINPUTHEADER Header;
        public RAWHID Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        public IntPtr bRawData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    private enum HIDP_REPORT_TYPE
    {
        HidP_Input,
        HidP_Output,
        HidP_Feature
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;

        [MarshalAs(UnmanagedType.U1)] public bool IsAlias;

        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;

        [MarshalAs(UnmanagedType.U1)] public bool IsRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsStringRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsDesignatorRange;
        [MarshalAs(UnmanagedType.U1)] public bool IsAbsolute;
        [MarshalAs(UnmanagedType.U1)] public bool HasNull;

        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public ushort[] Reserved2;

        public uint UnitsExp;
        public uint Units;
        public int LogicalMin;
        public int LogicalMax;
        public int PhysicalMin;
        public int PhysicalMax;

        public ushort UsageMin;
        public ushort UsageMax;
        public ushort StringMin;
        public ushort StringMax;
        public ushort DesignatorMin;
        public ushort DesignatorMax;
        public ushort DataIndexMin;
        public ushort DataIndexMax;

        public ushort Usage => UsageMin;
    }

    private struct TouchpadContactBuilder
    {
        public int? ContactId { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }

        public bool TryCreate(out TouchpadContact contact)
        {
            if (ContactId.HasValue && X.HasValue && Y.HasValue)
            {
                contact = new TouchpadContact(ContactId.Value, X.Value, Y.Value);
                return true;
            }

            contact = default;
            return false;
        }

        public void Clear()
        {
            ContactId = null;
            X = null;
            Y = null;
        }
    }

    private sealed class DeviceContext
    {
        public bool IsPrecisionTouchpad { get; init; }
        public IntPtr PreparsedData { get; set; }
        public HIDP_VALUE_CAPS[] ValueCaps { get; set; } = Array.Empty<HIDP_VALUE_CAPS>();
    }
}
