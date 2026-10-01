using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WinTools.Services;

/// <summary>
/// 桌面卡片右键菜单里的 Shell 操作：属性、以管理员身份运行、打开文件所在的位置、
/// 清空回收站、压缩为 ZIP 文件、在终端中打开、打开方式。
/// </summary>
/// <remarks>
/// 全部直接调 Shell API，**不经过整套系统右键菜单**：那要加载所有第三方扩展，
/// 桌面上一个 .lnk 实测每次约 0.95 秒（见 <see cref="ShellContextMenu.QueryHandler"/>）。
/// 必须借用系统扩展的两项（固定到“开始”、共享）走 <see cref="ShellContextMenu.QueryHandler"/>，
/// 只创建那一个处理器。
/// </remarks>
internal static class ShellItemActions
{
    /// <summary>「固定到“开始”」扩展：注册表里 Folder / exefile 下的 PintoStartScreen。</summary>
    public static readonly Guid StartPinHandler = new("470C0EBD-5D73-4d58-9CED-E91E22E23282");

    /// <summary>「共享」扩展：注册表里 AllFilesystemObjects 下的 ModernSharing。</summary>
    public static readonly Guid ShareHandler = new("e2bf9676-5f8f-435c-97eb-11607a5bedf7");

    /// <summary>回收站的 Shell 解析名，只有它显示「清空回收站」。</summary>
    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>资源管理器对这些类型显示「以管理员身份运行」。</summary>
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".msc",
    };

    private static readonly string TerminalAlias = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", "wt.exe");

    public static bool IsRunnable(string? path) =>
        !string.IsNullOrEmpty(path) && RunnableExtensions.Contains(Path.GetExtension(path));

    /// <summary>装了 Windows 终端才显示「在终端中打开」，和资源管理器一致。</summary>
    public static bool CanOpenInTerminal => File.Exists(TerminalAlias);

    /// <summary>「属性」：和资源管理器一样走 properties 动作，系统图标（此电脑等）也适用。</summary>
    public static bool ShowProperties(string parsingName, IntPtr owner)
    {
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST,
                hwnd = owner,
                lpVerb = "properties",
                lpIDList = pidl,
                nShow = SW_SHOWNORMAL,
            };
            return ShellExecuteEx(ref info);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.ShowProperties({parsingName})", ex);
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>「以管理员身份运行」。用户在 UAC 里点「否」不算失败，不提示。</summary>
    /// <remarks>快捷方式直接交给 ShellExecute：它会解析目标、沿用快捷方式里的起始位置和参数。</remarks>
    public static bool RunAsAdministrator(string path, IntPtr owner)
    {
        try
        {
            var isShortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                hwnd = owner,
                lpVerb = "runas",
                lpFile = path,
                lpDirectory = isShortcut ? null : Path.GetDirectoryName(path),
                nShow = SW_SHOWNORMAL,
            };
            return ShellExecuteEx(ref info) || Marshal.GetLastWin32Error() == ERROR_CANCELLED;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.RunAsAdministrator({path})", ex);
            return false;
        }
    }

    /// <summary>快捷方式指向的文件或文件夹；指向应用商店应用、网址等没有文件路径的目标时返回 null。</summary>
    public static string? ResolveShortcutTarget(string shortcutPath)
    {
        object? link = null;
        try
        {
            link = new ShellLinkCoClass();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new StringBuilder(1024);
            if (((IShellLinkW)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) != 0) return null;
            var target = Environment.ExpandEnvironmentVariables(buffer.ToString().Trim());
            return target.Length > 0 && (File.Exists(target) || Directory.Exists(target)) ? target : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>打开所在文件夹并选中它（「打开文件所在的位置」「在桌面文件夹中显示」都用这个）。</summary>
    public static bool ShowInExplorer(string path)
    {
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return false;
            return SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0) == 0;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.ShowInExplorer({path})", ex);
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>「清空回收站」：沿用系统的确认框、进度和提示音。</summary>
    public static void EmptyRecycleBin(IntPtr owner)
    {
        try { SHEmptyRecycleBinW(owner, null, 0); }
        catch (Exception ex) { ErrorReporter.Log("ShellItemActions.EmptyRecycleBin", ex); }
    }

    /// <summary>「在终端中打开」：用 Windows 终端的默认配置文件，起始目录是这个文件夹。</summary>
    public static bool OpenInTerminal(string folder)
    {
        try
        {
            var start = new ProcessStartInfo(TerminalAlias) { UseShellExecute = false };
            start.ArgumentList.Add("-d");
            // wt 把分号当成命令分隔符，路径里的分号要转义。
            start.ArgumentList.Add(folder.Replace(";", @"\;"));
            Process.Start(start)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.OpenInTerminal({folder})", ex);
            return false;
        }
    }

    /// <summary>
    /// 「压缩为 ZIP 文件」：在原项目旁边生成「名称.zip」，重名时和资源管理器一样依次用「名称 (2).zip」。
    /// 文件夹连同文件夹本身一起压进去。返回生成的文件路径。
    /// </summary>
    /// <remarks>
    /// 先写到临时目录，完成后再挪过去：直接在桌面上写的话，桌面监视器会先看到一个写了一半的文件。
    /// 公共桌面上的项目普通权限写不进原位置，改放到当前用户的桌面。
    /// </remarks>
    public static Task<string> CompressToZipAsync(string path) => Task.Run(() =>
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            throw new FileNotFoundException("要压缩的项目已经不在了。", path);

        var baseName = isDirectory
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path))
            : Path.GetFileNameWithoutExtension(path);
        var temporary = Path.Combine(Path.GetTempPath(), $"WinTools-zip-{Guid.NewGuid():N}.zip");
        try
        {
            if (isDirectory)
            {
                ZipFile.CreateFromDirectory(path, temporary, CompressionLevel.Optimal, includeBaseDirectory: true);
            }
            else
            {
                using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
                archive.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal);
            }

            var folder = Path.GetDirectoryName(path) ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            try
            {
                return MoveToFreeName(temporary, folder, baseName);
            }
            catch (UnauthorizedAccessException)
            {
                return MoveToFreeName(temporary, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), baseName);
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* 临时文件删不掉不影响结果 */ }
        }
    });

    private static string MoveToFreeName(string source, string folder, string baseName)
    {
        for (var index = 1; ; index++)
        {
            var candidate = Path.Combine(folder, index == 1 ? $"{baseName}.zip" : $"{baseName} ({index}).zip");
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            File.Move(source, candidate);
            return candidate;
        }
    }

    #region 打开方式

    /// <summary>「打开方式」子菜单里的一个应用。菜单关闭后 <see cref="Dispose"/>。</summary>
    internal sealed class OpenWithApp : IDisposable
    {
        private IAssocHandler? _handler;

        internal OpenWithApp(string name, string iconPath, int iconIndex, IAssocHandler handler)
        {
            Name = name;
            IconPath = iconPath;
            IconIndex = iconIndex;
            _handler = handler;
        }

        public string Name { get; }
        /// <summary>图标位置：普通程序是 exe / dll 路径；商店应用是 <c>@{...}</c> 间接字符串。</summary>
        public string IconPath { get; }
        public int IconIndex { get; }

        public bool Launch(string path, IntPtr owner)
        {
            if (_handler == null) return false;
            var dataObject = ShellContextMenu.CreateDataObject(path, owner);
            if (dataObject == IntPtr.Zero) return false;
            try { return _handler.Invoke(dataObject) >= 0; }
            catch (Exception ex)
            {
                ErrorReporter.Log($"ShellItemActions.OpenWith({Name}, {path})", ex);
                return false;
            }
            finally { Marshal.Release(dataObject); }
        }

        public void Dispose()
        {
            if (_handler != null && Marshal.IsComObject(_handler)) Marshal.FinalReleaseComObject(_handler);
            _handler = null;
        }
    }

    /// <summary>系统为这种文件推荐的应用（就是资源管理器「打开方式」子菜单里列的那几个）。</summary>
    public static List<OpenWithApp> GetOpenWithApps(string path, int max = 8)
    {
        var result = new List<OpenWithApp>();
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return result;

        IEnumAssocHandlers? enumerator = null;
        try
        {
            if (SHAssocEnumHandlers(extension, ASSOC_FILTER_RECOMMENDED, out enumerator) != 0 || enumerator == null)
                return result;
            var buffer = new IAssocHandler[1];
            while (result.Count < max && enumerator.Next(1, buffer, out var fetched) == 0 && fetched == 1)
            {
                var handler = buffer[0];
                if (handler.GetUIName(out var name) != 0 || string.IsNullOrWhiteSpace(name))
                {
                    Marshal.FinalReleaseComObject(handler);
                    continue;
                }
                if (handler.GetIconLocation(out var iconPath, out var iconIndex) != 0) iconPath = string.Empty;
                result.Add(new OpenWithApp(name, iconPath ?? string.Empty, iconIndex, handler));
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.GetOpenWithApps({path})", ex);
        }
        finally
        {
            if (enumerator != null && Marshal.IsComObject(enumerator)) Marshal.FinalReleaseComObject(enumerator);
        }
        return result;
    }

    /// <summary>「选择其他应用」：系统的「你要如何打开这个文件？」对话框，选完直接打开。</summary>
    public static void ShowOpenWithDialog(string path, IntPtr owner)
    {
        try
        {
            var info = new OPENASINFO
            {
                pcszFile = path,
                oaifInFlags = OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT | OAIF_EXEC,
            };
            SHOpenWithDialog(owner, ref info);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ShellItemActions.ShowOpenWithDialog({path})", ex);
        }
    }

    /// <summary>把商店应用的 <c>@{包名?ms-resource://...}</c> 图标描述解析成实际图片路径。</summary>
    public static string? ResolveIndirectString(string source)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            return SHLoadIndirectString(source, buffer, buffer.Capacity, IntPtr.Zero) == 0 ? buffer.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region interop

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const int SW_SHOWNORMAL = 1;
    private const int ERROR_CANCELLED = 1223;
    private const int ASSOC_FILTER_RECOMMENDED = 0x1;
    private const int OAIF_ALLOW_REGISTRATION = 0x1;
    private const int OAIF_REGISTER_EXT = 0x2;
    private const int OAIF_EXEC = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    /// <summary>只声明用到的第一个方法；vtable 顺序与 SDK 一致即可。</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath,
            IntPtr findData, uint flags);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("973810ae-9599-4b88-9e4d-6ee98c9552da")]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] handlers,
            out uint fetched);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F04061AC-1659-4a3f-A954-775AA57FC083")]
    internal interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(IntPtr dataObject);
        [PreserveSig] int CreateInvoker(IntPtr dataObject, out IntPtr invoker);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl,
        uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folderPidl, uint count, IntPtr items, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(IntPtr owner, string? rootPath, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, out IEnumAssocHandlers? enumerator);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OPENASINFO info);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int capacity, IntPtr reserved);

    #endregion
}
