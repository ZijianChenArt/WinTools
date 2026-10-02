using System;
using System.Runtime.InteropServices;

namespace WinTools.Services;

/// <summary>
/// 判断屏幕上某一点在资源管理器文件夹视图里是不是「空白处」。
/// </summary>
/// <remarks>
/// Win11 资源管理器的文件列表是 DirectUI，不是 SysListView32，不能像桌面那样用 LVM_HITTEST。
/// 用 UI Automation 的 ElementFromPoint 取按下点下的元素（2026-10-03 实测）：
/// 文件 / 文件夹是 <c>ListItem</c>（类名 UIItem），文件名那截是 <c>Edit</c>（UIProperty），
/// 空白处是 <c>List</c>（类名 UIItemsView）。空白处按下再拖是拉框选择，不是文件拖放。
/// 任何失败都当成「不是空白」——宁可多弹一次暂存窗，也不能漏掉真正的文件拖拽。
/// </remarks>
internal static class ExplorerItemProbe
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_ListControlTypeId = 50008;

    public static bool IsBlankArea(int screenX, int screenY)
    {
        object? automation = null;
        IUIAutomationElement? element = null;
        try
        {
            automation = new CUIAutomation();
            element = ((IUIAutomation)automation).ElementFromPoint(new POINT { X = screenX, Y = screenY });
            if (element == null) return false;

            if (string.Equals(element.GetCurrentPropertyValue(UIA_ClassNamePropertyId) as string,
                    "UIItemsView", StringComparison.Ordinal))
                return true;
            return element.GetCurrentPropertyValue(UIA_ControlTypePropertyId) is int type
                   && type == UIA_ListControlTypeId;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (element != null) Marshal.ReleaseComObject(element);
            if (automation != null) Marshal.ReleaseComObject(automation);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        IUIAutomationElement ElementFromPoint(POINT pt);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
    }
}
