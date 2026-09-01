using System;
using System.Runtime.InteropServices;

namespace WinTools;

/// <summary>系统主音量静音切换（Core Audio API，失败时回退模拟静音键）。</summary>
public static class AudioToggleService
{
    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int ERoleConsole = 0;
    private const int ERoleCommunications = 2;
    private static readonly int[] EndpointRoles = [ERoleMultimedia, ERoleConsole, ERoleCommunications];

    private const int ClsctxInprocServer = 1;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const byte VkVolumeMute = 0xAD;

    private static readonly Guid IidIAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private static bool? _cachedMuteState;

    /// <summary>供面板显示：优先读 Core Audio，失败时用缓存。</summary>
    public static bool QueryIsMuted()
    {
        if (TryGetIsMuted(out var muted))
        {
            _cachedMuteState = muted;
            return muted;
        }

        return _cachedMuteState ?? false;
    }

    /// <summary>读取默认播放设备是否静音；无法读取时返回 null。</summary>
    public static bool? TryGetIsMuted()
    {
        return TryGetIsMuted(out var muted) ? muted : null;
    }

    public static string GetMuteLabel() => QueryIsMuted() ? "静音" : "声音";

    public static bool ToggleMute()
    {
        var before = QueryIsMuted();

        if (TryToggleViaCoreAudio(out var after))
        {
            _cachedMuteState = after;
            return true;
        }

        if (SendMuteKeyFallback() || KeybdEventFallback())
        {
            _cachedMuteState = !before;
            return true;
        }

        return false;
    }

    private static bool TryGetIsMuted(out bool muted)
    {
        muted = false;
        IMMDevice? device = null;
        object? volumeObj = null;

        try
        {
            if (!TryGetDefaultRenderDevice(out device))
                return false;

            var iid = IidIAudioEndpointVolume;
            if (device!.Activate(ref iid, ClsctxInprocServer, IntPtr.Zero, out volumeObj) != 0)
                return false;

            var volume = (IAudioEndpointVolume)volumeObj;
            return volume.GetMute(out muted) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(volumeObj);
            ReleaseCom(device);
        }
    }

    private static bool TryToggleViaCoreAudio(out bool mutedAfter)
    {
        mutedAfter = false;
        IMMDevice? device = null;
        object? volumeObj = null;

        try
        {
            if (!TryGetDefaultRenderDevice(out device))
                return false;

            var iid = IidIAudioEndpointVolume;
            if (device!.Activate(ref iid, ClsctxInprocServer, IntPtr.Zero, out volumeObj) != 0)
                return false;

            var volume = (IAudioEndpointVolume)volumeObj;
            if (volume.GetMute(out var muted) != 0)
                return false;

            var eventContext = Guid.Empty;
            if (volume.SetMute(!muted, ref eventContext) != 0)
                return false;

            mutedAfter = !muted;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseCom(volumeObj);
            ReleaseCom(device);
        }
    }

    private static bool TryGetDefaultRenderDevice(out IMMDevice? device)
    {
        device = null;
        IMMDeviceEnumerator? enumerator = null;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            foreach (var role in EndpointRoles)
            {
                if (enumerator.GetDefaultAudioEndpoint(EDataFlowRender, role, out device) == 0 && device != null)
                    return true;
            }
        }
        catch
        {
            device = null;
        }
        finally
        {
            ReleaseCom(enumerator);
        }

        return device != null;
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj == null) return;
        try { Marshal.ReleaseComObject(obj); } catch { /* ignore */ }
    }

    private static bool SendMuteKeyFallback()
    {
        try
        {
            var inputs = new INPUT[2];
            inputs[0] = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VkVolumeMute } },
            };
            inputs[1] = new INPUT
            {
                type = InputKeyboard,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VkVolumeMute, dwFlags = KeyeventfKeyup } },
            };

            return SendInput(2, inputs, Marshal.SizeOf<INPUT>()) == 2;
        }
        catch
        {
            return false;
        }
    }

    private static bool KeybdEventFallback()
    {
        try
        {
            keybd_event(VkVolumeMute, 0, 0, 0);
            keybd_event(VkVolumeMute, 0, KeyeventfKeyup, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F2A-B403-72A99700A659")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    /// <summary>与 Core Audio SDK / NAudio 一致的 vtable 布局（eventContext 必须为 ref Guid，不能用 IntPtr）。</summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float volumeMin, out float volumeMax, out float volumeInc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
