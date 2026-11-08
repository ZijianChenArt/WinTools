using System;
using System.Runtime.InteropServices;

namespace WinTools;

internal interface IThreeFingerInput : IDisposable
{
    bool Begin(TouchpadGeometry geometry, double x, double y);
    bool Move(double dx, double dy);
    void End();
    string? Error { get; }
}

/// <summary>A single virtual touchpad contact, processed by Windows' native PTP mousing path.</summary>
internal sealed class NativeTouchpadInput : IThreeFingerInput
{
    private const uint Touchpad = 5, InRange = 2, InContact = 4, Confidence = 0x4000;
    private IntPtr _device;
    private TouchpadGeometry _geometry;
    private double _x, _y;
    private bool _buttonDown;
    private readonly PointerTypeInfo[] _packet = new PointerTypeInfo[1];
    public string? Error { get; private set; }

    internal static bool IsSupported
    {
        get
        {
            if (!NativeLibrary.TryLoad("user32.dll", out var module)) return false;
            try { return NativeLibrary.TryGetExport(module, "CreateSyntheticPointerDevice2", out _); }
            finally { NativeLibrary.Free(module); }
        }
    }

    public bool Begin(TouchpadGeometry geometry, double x, double y)
    {
        if (!BeginContact(geometry, x, y)) return false;
        // Only the button is synthesized as a mouse event. Movement is never SendInput/SetCursorPos.
        if (!SendButton(2)) return Fail("按下拖动按钮");
        _buttonDown = true;
        return true;
    }

    internal bool BeginContact(TouchpadGeometry geometry, double x, double y)
    {
        End();
        if (_buttonDown) return false;
        Error = null;
        if (!IsSupported) { Error = "此 Windows 版本不支持原生触控板输入，请更新系统后重试。"; return false; }
        var parameters = new CreationParameters
        {
            Type = Touchpad, MaxCount = 1, FeedbackMode = 3,
            Width = geometry.Width, Height = geometry.Height, Options = 1 // SDCO_PHYSICAL_SIZE, NOT gesture-only.
        };
        _device = CreateSyntheticPointerDevice2(ref parameters);
        if (_device == IntPtr.Zero) return Fail("创建原生触控板");
        _geometry = geometry;
        _x = geometry.X.ToHimetric(x);
        _y = geometry.Y.ToHimetric(y);
        if (!Inject(InRange | InContact | Confidence)) return Fail("开始原生触控板输入");
        return true;
    }

    public bool Move(double dx, double dy)
    {
        if (_device == IntPtr.Zero) return false;
        _x += dx * _geometry.X.HimetricExtent / (_geometry.X.LogicalMax - (double)_geometry.X.LogicalMin);
        _y += dy * _geometry.Y.HimetricExtent / (_geometry.Y.LogicalMax - (double)_geometry.Y.LogicalMin);
        return Inject(InRange | InContact | Confidence) || Fail("移动原生触控板");
    }

    public void End()
    {
        if (_device != IntPtr.Zero)
        {
            // Clearing confidence invalidates the contact before lifting it. PTP infers lift
            // from the contact state and ignores POINTER_FLAG_CANCELED for tap recognition.
            // A normal confident lift would create a second click after a stationary drag.
            if (!Inject(InRange | InContact) || !Inject(0)) Error ??= "原生触点释放失败，已销毁输入设备。";
            DestroySyntheticPointerDevice(_device);
            _device = IntPtr.Zero;
        }
        if (_buttonDown)
        {
            if (SendButton(4)) _buttonDown = false;
            else Error = "拖动按钮释放失败，请按一下鼠标左键。";
        }
    }

    public void Dispose() => End();

    private bool Fail(string operation)
    {
        var code = Marshal.GetLastWin32Error();
        Error = $"{operation}失败（{code}），请关闭再开启三指拖拽。";
        End();
        return false;
    }

    private bool Inject(uint flags)
    {
        _packet[0] = new PointerTypeInfo
        {
            Type = Touchpad,
            Touch = new TouchInfo { Pointer = new PointerInfo
            {
                Type = Touchpad, Id = 0, Flags = flags,
                Himetric = new Point { X = (int)Math.Round(Math.Clamp(_x, 0, _geometry.Width)), Y = (int)Math.Round(Math.Clamp(_y, 0, _geometry.Height)) }
            } }
        };
        return InjectSyntheticPointerInput(_device, _packet, 1);
    }

    private static bool SendButton(uint flags)
    {
        var input = new MouseInput { Type = 0, Mouse = new MouseData { Flags = flags, Extra = ThreeFingerDragService.SyntheticInputTag } };
        return SendInput(1, new[] { input }, Marshal.SizeOf<MouseInput>()) == 1;
    }

    [StructLayout(LayoutKind.Sequential)] internal struct CreationParameters
    { public uint Type, MaxCount, FeedbackMode; public IntPtr Monitor; public uint Width, Height, Options; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerInfo
    {
        public uint Type, Id, Frame, Flags;
        public IntPtr Source, Target;
        public Point Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public uint ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TouchInfo
    { public PointerInfo Pointer; public uint Flags, Mask; public Rect Contact, ContactRaw; public uint Orientation, Pressure; }
    [StructLayout(LayoutKind.Sequential)] internal struct PointerTypeInfo { public uint Type; public TouchInfo Touch; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public uint Type; public MouseData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public nint Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateSyntheticPointerDevice2(ref CreationParameters parameters);
    [DllImport("user32.dll")] private static extern void DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, PointerTypeInfo[] info, uint count);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
}
