using System;
using System.IO;
using System.Text;

namespace WinTools.Services;

/// <summary>
/// 统一错误日志入口：把过去散落在代码里的 <c>catch { /* ignore */ }</c>
/// 收敛到一处，方便用户反馈问题时直接读取日志。
/// 写入 <c>%TEMP%\WinTools-error-yyyyMMdd.log</c>，单文件追加，进程内互斥。
/// <para>
/// 调用方约定：传一个简短的 <paramref name="scope"/>（例如 <c>"App.Startup"</c>、
/// <c>"DesktopCard.GetWorkAreaDip"</c>）描述出错的子系统；本类只负责落盘，
/// 不弹窗、不修改 UI。剩余 20+ 处尚未迁移的 <c>catch</c> 是有意保留的，
/// 它们基本是"非关键路径失败就降级"，不影响主流程，等真正出问题再补。
/// </para>
/// </summary>
internal static class ErrorReporter
{
    private static readonly object _lock = new();

    public static void Log(string scope, Exception ex)
    {
        if (ex == null) return;
        Write(scope, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    }

    /// <summary>
    /// 记一条不带异常的诊断信息。给"难复现、只能靠日志回溯"的路径用
    /// （例如桌面双击到底是被哪一条判定放行 / 拦下的），调用点请自觉保持稀疏。
    /// </summary>
    public static void Log(string scope, string message) => Write(scope, message);

    private static void Write(string scope, string detail)
    {
        try
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"WinTools-error-{DateTime.Now:yyyyMMdd}.log");
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[{DateTime.Now:HH:mm:ss.fff}] [{scope}] {detail}\n\n");
            lock (_lock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写不进去也不能反过来影响主流程
        }
    }
}
