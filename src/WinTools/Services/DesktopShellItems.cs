using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinTools.Services;

/// <summary>
/// 桌面上的**系统虚拟图标**（此电脑 / 回收站 / 网络 / 控制面板）。
/// </summary>
/// <remarks>
/// 这些项目在桌面文件夹里**没有对应文件**，是 Shell 命名空间里的虚拟对象，所以枚举目录
/// 一个也看不到。以前它们靠系统桌面图标显示，和卡片井水不犯河水；桌面分区把「显示桌面图标」
/// 整体关掉之后，不把它们补进卡片，用户就再也点不到「此电脑」和「回收站」了
/// （2026-09-07 用户反馈）。
/// <para>
/// 表示方式沿用 Shell 的解析名 <c>::{CLSID}</c>，直接当成卡片项的 Path 用：
/// 显示名和图标都通过 PIDL 找 Shell 要，打开则交给 <c>explorer.exe shell:::{CLSID}</c>。
/// </para>
/// </remarks>
internal static class DesktopShellItems
{
    /// <summary>桌面系统图标的显示与否记在这里：值为 1 表示用户在「个性化 → 主题 → 桌面图标设置」里关掉了它。</summary>
    private const string HideDesktopIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    /// <summary>CLSID → 取不到 Shell 显示名时的兜底中文名。</summary>
    private static readonly (string Clsid, string Fallback)[] Known =
    {
        ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑"),
        ("{645FF040-5081-101B-9F08-00AA002F954E}", "回收站"),
        ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "网络"),
        ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "控制面板"),
    };

    /// <summary>判断一个卡片项的 Path 是不是系统虚拟图标。</summary>
    public static bool IsShellItem(string? path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith("::{", StringComparison.Ordinal);

    /// <summary>当前桌面上**启用了**的系统图标，返回 (解析名, 显示名)。</summary>
    public static List<(string ParsingName, string DisplayName)> EnumerateVisible()
    {
        var result = new List<(string, string)>();
        RegistryKey? key = null;
        try { key = Registry.CurrentUser.OpenSubKey(HideDesktopIconsKey); }
        catch (Exception ex) { ErrorReporter.Log("DesktopShellItems.OpenKey", ex); }

        try
        {
            foreach (var (clsid, fallback) in Known)
            {
                // 键不存在 = 用户没动过 = 按系统默认显示；值为 1 才是明确隐藏。
                if (key?.GetValue(clsid) is int hidden && hidden != 0) continue;

                var parsingName = "::" + clsid;
                result.Add((parsingName, GetDisplayName(parsingName) ?? fallback));
            }
        }
        finally { key?.Dispose(); }

        return result;
    }

    /// <summary>向 Shell 要显示名（跟随系统语言，用户改过名也能拿到）。</summary>
    public static string? GetDisplayName(string parsingName)
    {
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return null;

            var info = new SHFILEINFO();
            var result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_PIDL | SHGFI_DISPLAYNAME);
            return result == IntPtr.Zero ? null : info.szDisplayName;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopShellItems.GetDisplayName({parsingName})", ex);
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>取系统图标句柄。调用方负责 <c>DestroyIcon</c>（走 CardItem 里那套 PNG 转换）。</summary>
    /// <remarks>
    /// 两条路，按顺序试：
    /// 1. <c>SHGFI_PIDL | SHGFI_ICON</c> 直接要句柄；
    /// 2. <c>SHGFI_PIDL | SHGFI_SYSICONINDEX</c> 拿系统图像列表索引，再
    ///    <c>ImageList_GetIcon</c> 取图标——虚拟命名空间项走这条更稳。
    ///
    /// **不要再加 `SHGetStockIconInfo` 兜底。** 2026-09-12 之前那版就是这么干的，
    /// 而且 SIID 给错了：控制面板写成 22（`SIID_FIND`，放大镜）、此电脑写成 15
    /// （`SIID_SERVER`，服务器机箱）——Shell 里**根本没有**「控制面板」对应的库存图标。
    /// 结果卡片上的控制面板变成一个放大镜，用户一眼就看出不对。
    /// 取不到就宁可空着，也别画一个错的。
    /// </remarks>
    public static IntPtr GetIconHandle(string parsingName)
    {
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                ErrorReporter.Log($"DesktopShellItems.GetIconHandle({parsingName})",
                    new InvalidOperationException("SHParseDisplayName 失败"));
                return IntPtr.Zero;
            }

            var info = new SHFILEINFO();
            var result = SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON);
            if (result != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                return info.hIcon;

            // 直接取失败，改从系统图像列表取。
            var indexInfo = new SHFILEINFO();
            var imageList = SHGetFileInfo(pidl, 0, ref indexInfo, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_PIDL | SHGFI_SYSICONINDEX | SHGFI_LARGEICON);
            if (imageList != IntPtr.Zero)
            {
                var handle = ImageList_GetIcon(imageList, indexInfo.iIcon, ILD_TRANSPARENT);
                if (handle != IntPtr.Zero)
                {
                    ErrorReporter.Log($"DesktopShellItems.GetIconHandle({parsingName})",
                        new InvalidOperationException($"SHGFI_ICON 没给句柄，已改用系统图像列表 index={indexInfo.iIcon}"));
                    return handle;
                }
            }

            ErrorReporter.Log($"DesktopShellItems.GetIconHandle({parsingName})",
                new InvalidOperationException("两条路径都没取到图标，卡片将只显示文字"));
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopShellItems.GetIconHandle({parsingName})", ex);
            return IntPtr.Zero;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>打开系统图标对应的位置。</summary>
    /// <remarks>
    /// 走 <c>explorer.exe shell:::{CLSID}</c>。直接把 <c>::{CLSID}</c> 丢给 ShellExecute
    /// 在部分系统上会失败，交给 explorer 解析最稳。
    /// </remarks>
    public static bool Open(string parsingName)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:" + parsingName,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopShellItems.Open({parsingName})", ex);
            return false;
        }
    }

    #region interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_PIDL = 0x000000008;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint ILD_TRANSPARENT = 0x00000001;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        IntPtr pidl, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    #endregion
}
