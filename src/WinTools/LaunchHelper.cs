using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WinTools;

public static class LaunchHelper
{
    private const int SW_SHOW = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(IntPtr hwnd, string lpOperation, string lpFile, string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>用指定程序打开文件。返回 (是否成功, 失败时的错误信息)。</summary>
    public static (bool ok, string? error) OpenWith(string programPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return (false, "程序路径为空。");
        if (string.IsNullOrWhiteSpace(filePath))
            return (false, "文件路径为空。");

        var expandedProgram = Environment.ExpandEnvironmentVariables(programPath.Trim());
        var fullPath = Path.GetFullPath(expandedProgram);
        var fileFull = Path.GetFullPath(filePath);

        if (!File.Exists(fullPath))
            return (false, $"程序不存在：\n{fullPath}\n请在“程序关联”中检查路径。");

        if (!File.Exists(fileFull))
            return (false, $"要打开的文件不存在：\n{fileFull}");

        var fileDir = Path.GetDirectoryName(fileFull) ?? ".";

        try
        {
            // 优先用 ShellExecute("open", 程序, 文件路径) — 与资源管理器“打开方式”一致，部分程序只认这种方式
            IntPtr result = ShellExecute(IntPtr.Zero, "open", fullPath, "\"" + fileFull + "\"", fileDir, SW_SHOW);
            long code = result.ToInt64();
            if (code > 32)
            {
                return (true, null);
            }

            // ShellExecute 返回值 <= 32 表示错误，再用 Process.Start 试一次
            var startInfo = new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = "\"" + fileFull + "\"",
                WorkingDirectory = fileDir,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}\n程序：{fullPath}");
        }
    }
}
