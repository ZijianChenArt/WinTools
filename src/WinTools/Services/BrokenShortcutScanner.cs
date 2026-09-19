using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinTools.Services;

/// <summary>桌面上一个目标已经不存在的快捷方式。</summary>
internal sealed record BrokenShortcut(string Path, string DisplayName, string Target);

/// <summary>
/// 扫描桌面上**目标已不存在**的 <c>.lnk</c> 快捷方式（卸载软件后常被留下）。
/// </summary>
/// <remarks>
/// 2026-09-12 起因：用户卸载了 InDesign，卡片里还显示 `Adobe InDesign 2024.lnk`。
/// 那不是卡片没刷新——快捷方式文件确实还在桌面上（卸载程序没清理），Windows 桌面
/// 同样会显示它。所以这里只做「找出来 + 交给用户确认」，**绝不自动删除**。
/// <para>
/// 误判防护：目标在可移动磁盘 / 网络位置上时会临时不可达，这类一律跳过。
/// 只看 <c>.lnk</c>；<c>.url</c> 指向网址，没有"目标文件存在与否"可言。
/// </para>
/// </remarks>
internal static class BrokenShortcutScanner
{
    /// <summary>扫描当前用户桌面。必须在后台调用（内部会起一条 STA 线程解析快捷方式）。</summary>
    public static List<BrokenShortcut> Scan()
    {
        var result = new List<BrokenShortcut>();

        // WScript.Shell 要求 STA，和图标解析那条链路同样的约束。
        var thread = new Thread(() => ScanCore(result));
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        return result;
    }

    private static void ScanCore(List<BrokenShortcut> result)
    {
        object? shell = null;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop)) return;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            dynamic shellObject = shell;

            foreach (var path in Directory.EnumerateFiles(desktop, "*.lnk"))
            {
                object? link = null;
                try
                {
                    link = shellObject.CreateShortcut(path);
                    dynamic shortcut = link!;
                    string target = shortcut.TargetPath as string ?? string.Empty;
                    target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

                    // 目标为空：多半指向 Shell 命名空间对象（控制面板项之类），不判死刑。
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    if (IsOnUnreliableVolume(target)) continue;
                    if (File.Exists(target) || Directory.Exists(target)) continue;

                    result.Add(new BrokenShortcut(
                        path, System.IO.Path.GetFileNameWithoutExtension(path), target));
                }
                catch (Exception ex)
                {
                    // 单个快捷方式读不出来就跳过——宁可漏报，也不能误报成"失效"。
                    ErrorReporter.Log($"BrokenShortcutScanner.Read({path})", ex);
                }
                finally
                {
                    try { if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link); }
                    catch { /* 释放失败不影响扫描结果 */ }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("BrokenShortcutScanner.Scan", ex);
        }
        finally
        {
            try { if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
            catch { /* 同上 */ }
        }
    }

    /// <summary>目标是否位于「现在读不到不代表没有」的位置：可移动磁盘、网络盘、UNC 路径。</summary>
    private static bool IsOnUnreliableVolume(string target)
    {
        try
        {
            if (target.StartsWith(@"\\", StringComparison.Ordinal)) return true;   // UNC

            var root = System.IO.Path.GetPathRoot(target);
            if (string.IsNullOrWhiteSpace(root)) return true;

            var drive = new DriveInfo(root);
            return drive.DriveType is DriveType.Removable or DriveType.Network
                or DriveType.CDRom or DriveType.NoRootDirectory
                || !drive.IsReady;
        }
        catch
        {
            // 判断不了就当作不可靠，跳过——同样是宁可漏报。
            return true;
        }
    }
}
