using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace WinTools.Services;

/// <summary>
/// 界面线程卡死时留证据。Windows 的「应用无响应」报告（WER AppHang）不含堆栈，事后查不出卡在哪。
/// 这里后台每 2 秒给界面线程投一个心跳；超过 8 秒没回应，就把整个进程转储到
/// <c>%LocalAppData%\WinTools\hangs</c>（最多保留 3 份，10 分钟内只写一份），
/// 之后用 <c>dotnet-dump analyze</c> 的 <c>clrstack</c> / <c>threads</c> 就能看到界面线程卡在哪一行。
/// </summary>
internal static class UiHangWatchdog
{
    private static long _ack;
    private static bool _started;

    internal static void Start(DispatcherQueue ui)
    {
        if (_started) return;
        _started = true;
        Volatile.Write(ref _ack, Environment.TickCount64);
        new Thread(() => Loop(ui)) { IsBackground = true, Name = "WinTools UI Watchdog" }.Start();
    }

    private static void Loop(DispatcherQueue ui)
    {
        var lastDump = DateTime.MinValue;
        var lastLoop = Environment.TickCount64;
        while (true)
        {
            Thread.Sleep(2000);
            var now = Environment.TickCount64;
            // 睡眠 / 休眠恢复后循环间隔会远超 2 秒：此时的「没回应」不是卡死，重置后继续。
            if (now - lastLoop > 6000) Volatile.Write(ref _ack, now);
            lastLoop = now;

            ui.TryEnqueue(() => Volatile.Write(ref _ack, Environment.TickCount64));
            var stalled = now - Volatile.Read(ref _ack);
            if (stalled <= 8000 || DateTime.UtcNow - lastDump < TimeSpan.FromMinutes(10)) continue;
            lastDump = DateTime.UtcNow;
            try { WriteDump(stalled); }
            catch (Exception ex) { ErrorReporter.Log("UiHangWatchdog", ex); }
        }
    }

    private static void WriteDump(long stalledMs)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "hangs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"hang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
        using (var file = File.Create(path))
        {
            using var process = Process.GetCurrentProcess();
            // MiniDumpWithFullMemory：dotnet-dump 看托管堆栈需要完整内存。
            if (!MiniDumpWriteDump(process.Handle, process.Id, file.SafeFileHandle.DangerousGetHandle(), 0x2, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception();
        }
        ErrorReporter.Log("UiHangWatchdog", $"界面线程 {stalledMs / 1000} 秒无响应，已写出转储：{path}");
        foreach (var old in new DirectoryInfo(directory).GetFiles("hang-*.dmp").OrderByDescending(f => f.CreationTimeUtc).Skip(3))
            try { old.Delete(); } catch { /* 占用时下次再清 */ }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr process, int processId, IntPtr file, int dumpType,
        IntPtr exception, IntPtr userStream, IntPtr callback);
}
