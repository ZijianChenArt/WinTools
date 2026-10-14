using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace WinTools.Services;

/// <summary>
/// 记录「这一次运行是怎么结束的」。
/// <para>
/// 进程被 <c>taskkill /f</c>、<c>TerminateProcess</c> 或系统直接结束时，托管代码什么都来不及写：
/// 事件查看器没有崩溃记录，错误日志也没有一行，只剩桌面图标还藏着。所以启动时在
/// <c>%LocalAppData%\WinTools\run-session.json</c> 放一个标记，运行中每分钟刷新「最后存活时间」，
/// 正常退出（托盘退出 / 注销关机 / 进程退出事件）才删掉它并写一条日志。
/// 下次启动如果标记还在，就说明上次是被外部强行结束的，把它的启动时间和最后存活时间记进日志。
/// </para>
/// </summary>
internal static class RunSession
{
    private static readonly string MarkerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinTools", "run-session.json");

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);
    private static readonly object Gate = new();
    private static Timer? _heartbeat;
    private static DateTime _startedAt;
    private static bool _active;

    private sealed record Marker(int Pid, string Version, DateTime StartedAt, DateTime LastAlive);

    /// <summary>必须在单实例判定通过之后调用：被转交的第二个实例不能碰上一个实例的标记。</summary>
    internal static void Start()
    {
        try
        {
            lock (Gate)
            {
                if (_active) return;
                ReportPreviousRun();
                _startedAt = DateTime.Now;
                _active = true;
                Write(_startedAt);
                _heartbeat = new Timer(_ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("RunSession.Start", ex);
        }
    }

    /// <summary>正常退出路径调用：记原因、删标记。没有 <see cref="Start"/> 过（例如被转交的第二个实例）就什么都不做。</summary>
    internal static void MarkCleanExit(string reason)
    {
        try
        {
            lock (Gate)
            {
                if (!_active) return;
                _active = false;
                _heartbeat?.Dispose();
                _heartbeat = null;
                var minutes = (DateTime.Now - _startedAt).TotalMinutes;
                ErrorReporter.Log("RunSession.Exit", $"正常退出：{reason}（本次运行 {minutes:F1} 分钟）");
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("RunSession.MarkCleanExit", ex);
        }
    }

    private static void Beat()
    {
        try
        {
            lock (Gate)
            {
                if (_active) Write(_startedAt);
            }
        }
        catch
        {
            // 心跳写不进去不影响主流程
        }
    }

    private static void Write(DateTime startedAt)
    {
        var marker = new Marker(Environment.ProcessId, UpdateService.CurrentVersionText, startedAt, DateTime.Now);
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
    }

    private static void ReportPreviousRun()
    {
        if (!File.Exists(MarkerPath)) return;
        try
        {
            var previous = JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath));
            if (previous == null) return;
            var alive = (previous.LastAlive - previous.StartedAt).TotalMinutes;
            ErrorReporter.Log("RunSession.PreviousRun",
                $"上次运行没有正常退出（被外部强行结束、断电或系统崩溃）：v{previous.Version}，pid {previous.Pid}，" +
                $"启动于 {previous.StartedAt:yyyy-MM-dd HH:mm:ss}，最后存活 {previous.LastAlive:yyyy-MM-dd HH:mm:ss}" +
                $"（运行了约 {alive:F1} 分钟）。真正的退出时间在最后存活之后一分钟内。");
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("RunSession.PreviousRun", ex);
        }
        finally
        {
            try { File.Delete(MarkerPath); } catch { }
        }
    }
}
