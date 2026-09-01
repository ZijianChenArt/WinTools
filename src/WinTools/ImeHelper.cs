using System;
using System.Runtime.InteropServices;

namespace WinTools;

/// <summary>
/// 向目标窗口的默认 IME 窗口发送 WM_IME_CONTROL，跨进程切换中/英文。
/// ImmGetContext 拿不到其他进程的输入上下文，必须走 IME 窗口消息。
/// </summary>
internal static class ImeHelper
{
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IMC_SETCONVERSIONMODE = 0x0002;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IMC_SETOPENSTATUS = 0x0006;
    private const int IME_CMODE_NATIVE = 0x0001;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SendTimeoutMs = 200;

    /// <summary>将目标窗口输入法明确设为中文或英文（跨进程）。</summary>
    public static void SetChineseMode(IntPtr hwnd, bool useChinese)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var hIme = ImmGetDefaultIMEWnd(hwnd);
            if (hIme == IntPtr.Zero) return;

            // 转换模式仅在输入法开启时生效，先确保开启。
            if (SendControl(hIme, IMC_GETOPENSTATUS, IntPtr.Zero, out _) == IntPtr.Zero)
                SendControl(hIme, IMC_SETOPENSTATUS, new IntPtr(1), out _);

            // NATIVE 位 = 中文，清除该位 = 英文；保留全/半角等其它位。
            int current = SendControl(hIme, IMC_GETCONVERSIONMODE, IntPtr.Zero, out bool ok).ToInt32();
            if (!ok) current = useChinese ? 0 : IME_CMODE_NATIVE;
            int target = useChinese ? (current | IME_CMODE_NATIVE) : (current & ~IME_CMODE_NATIVE);
            if (target != current)
                SendControl(hIme, IMC_SETCONVERSIONMODE, new IntPtr(target), out _);
        }
        catch
        {
            // ignore
        }
    }

    private static IntPtr SendControl(IntPtr hIme, int command, IntPtr value, out bool ok)
    {
        ok = SendMessageTimeout(hIme, WM_IME_CONTROL, new IntPtr(command), value,
            SMTO_ABORTIFHUNG, SendTimeoutMs, out IntPtr result) != IntPtr.Zero;
        return result;
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
