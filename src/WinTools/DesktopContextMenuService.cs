using System;
using System.Linq;
using Microsoft.Win32;

namespace WinTools;

/// <summary>管理桌面空白区域右键菜单中的“整理桌面”命令。</summary>
public static class DesktopContextMenuService
{
    private const string LegacyMenuKeyPath = @"Software\Classes\DesktopBackground\Shell\WinTools.OrganizeDesktop";
    private const string CommandArgument = "--organize-desktop";

    public static bool IsOrganizeCommand(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return false;
        return arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(CommandArgument, StringComparer.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            // 旧版注册表 Shell 动词只会进入 Windows 11 的“显示更多选项”。
            // 现代菜单由 Package.appxmanifest + IExplorerCommand 注册，此处仅清理旧版本残留。
            Registry.CurrentUser.DeleteSubKeyTree(LegacyMenuKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // 注册表不可写时不影响桌面整理主体功能。
        }
    }
}
