using System;
using System.Runtime.InteropServices;

namespace WinTools.Services;

/// <summary>
/// 排除资源管理器文件列表里的空白处及列标题拖动。
/// </summary>
/// <remarks>
/// Win11 资源管理器的文件列表是 DirectUI，不是 SysListView32，不能像桌面那样用 LVM_HITTEST。
/// 用 UI Automation 的 ElementFromPoint 取按下点下的元素（2026-10-03 实测）：
/// 文件 / 文件夹是 <c>ListItem</c>（类名 UIItem），文件名那截是 <c>Edit</c>（UIProperty），
/// 空白处是 <c>List</c>（类名 UIItemsView）。空白处按下再拖是拉框选择，不是文件拖放。
/// 详细信息视图的列标题及其分隔线用于调列宽/列顺序，不能触发文件暂存。
/// 任何失败都当成「不是空白」——宁可多弹一次暂存窗，也不能漏掉真正的文件拖拽。
/// </remarks>
internal static class ExplorerItemProbe
{
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_ClassNamePropertyId = 30012;
    private const int UIA_ListControlTypeId = 50008;
    private const int UIA_ListItemControlTypeId = 50007;
    private const int UIA_HeaderControlTypeId = 50034;
    private const int UIA_HeaderItemControlTypeId = 50035;

    public static bool IsNonFileDragArea(int screenX, int screenY)
    {
        object? automation = null;
        IUIAutomationElement? element = null;
        IUIAutomationTreeWalker? walker = null;
        try
        {
            automation = new CUIAutomation();
            element = ((IUIAutomation)automation).ElementFromPoint(new POINT { X = screenX, Y = screenY });
            // A hit may be a text child or a divider rather than the header itself.
            // Stop at a real file item so its filename/property children remain valid drag sources.
            for (var depth = 0; element != null && depth < 12; depth++)
            {
                var type = element.GetCurrentPropertyValue(UIA_ControlTypePropertyId);
                if (type is UIA_HeaderControlTypeId or UIA_HeaderItemControlTypeId) return true;
                if (type is UIA_ListItemControlTypeId) return false;
                if (type is UIA_ListControlTypeId
                    || string.Equals(element.GetCurrentPropertyValue(UIA_ClassNamePropertyId) as string,
                        "UIItemsView", StringComparison.Ordinal)) return true;
                walker ??= ((IUIAutomation)automation).GetRawViewWalker();
                var parent = walker.GetParentElement(element);
                Marshal.ReleaseComObject(element);
                element = parent;
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (element != null) Marshal.ReleaseComObject(element);
            if (walker != null) Marshal.ReleaseComObject(walker);
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
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void GetControlViewWalker();
        void GetContentViewWalker();
        IUIAutomationTreeWalker GetRawViewWalker();
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        IUIAutomationElement? GetParentElement(IUIAutomationElement element);
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
