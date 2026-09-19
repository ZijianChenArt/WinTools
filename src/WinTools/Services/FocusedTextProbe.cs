using System;
using System.Runtime.InteropServices;

namespace WinTools.Services;

/// <summary>
/// 前台窗口里正在输入的文本框。坐标均为物理像素（本进程是 PerMonitorV2）。
/// 只有找到看得见、且落在焦点元素内的输入光标时才会产生（<see cref="HasCaret"/> 恒为真）。
/// </summary>
internal readonly record struct FocusedTextTarget(
    IntPtr RootWindow,
    IntPtr FocusWindow,
    int[] RuntimeId,
    bool HasCaret,
    int CaretX,
    int CaretTop,
    int CaretBottom,
    int BoxLeft,
    int BoxTop,
    int BoxRight,
    int BoxBottom)
{
    public bool IsSameElement(FocusedTextTarget other) =>
        RootWindow == other.RootWindow && FocusWindow == other.FocusWindow && RuntimeId.AsSpan().SequenceEqual(other.RuntimeId);

    public string Describe() =>
        $"root=0x{RootWindow:X} focus=0x{FocusWindow:X} caret={(HasCaret ? $"{CaretX},{CaretTop}-{CaretBottom}" : "none")} box={BoxLeft},{BoxTop},{BoxRight},{BoxBottom}";
}

/// <summary>
/// 判断前台窗口是否处于文字输入状态，并找出输入光标的位置。
/// </summary>
/// <remarks>
/// <para><b>「有输入光标」才是判定依据，UIA 控件类型只用来排除。</b>第一版只认 UIA 的 Edit /
/// 可写 Value+Text，结果只有少数程序能出小球：2026-09-14 实测 ChatGPT 桌面版的输入框在 UIA 里是
/// Group（50026），不带任何文本模式，但 MSAA 的 <c>OBJID_CARET</c> 能正常给出光标位置。</para>
/// <para>光标位置三个来源按精度排序：UIA TextPattern2.GetCaretRange（WinUI / WPF / 新版记事本）→
/// MSAA <c>OBJID_CARET</c>（Chromium / Electron / Firefox / 大多数 Win32）→ <c>GetGUIThreadInfo</c>
/// 的系统光标（自绘界面的国产软件为了让输入法候选窗跟随，通常会创建它）。实测记事本、Edge 地址栏、
/// ChatGPT 的 MSAA 光标都可用。</para>
/// <para>只能在创建它的那条 MTA 后台线程上使用。COM 接口只声明到用得上的方法为止，
/// vtable 前缀布局已用 PowerShell 探针在记事本 / Edge / ChatGPT 上实测核对。</para>
/// </remarks>
internal sealed class FocusedTextProbe
{
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_ProcessIdPropertyId = 30002;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_IsPasswordPropertyId = 30019;
    private const int UIA_IsTextPatternAvailablePropertyId = 30040;
    private const int UIA_IsValuePatternAvailablePropertyId = 30043;
    private const int UIA_ValueIsReadOnlyPropertyId = 30046;
    private const int UIA_TextPattern2Id = 10024;
    private const int UIA_EditControlTypeId = 50004;

    private const uint OBJID_CARET = 0xFFFFFFF8;
    private const int STATE_SYSTEM_INVISIBLE = 0x8000;
    private const uint GUI_CARETBLINKING = 0x0001;
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    private readonly IUIAutomation _automation = (IUIAutomation)new CUIAutomation();
    private readonly int _ownProcessId = Environment.ProcessId;

    // 上一次命中元素的文本模式：定时跟随光标时直接复用，不必重新 GetFocusedElement。
    private IUIAutomationTextPattern2? _lastTextPattern;

    /// <summary>最近一次判定的简短原因，供诊断日志使用。</summary>
    public string LastReason { get; private set; } = "";

    /// <summary>完整判定：前台窗口里获得焦点的是不是可输入文字的控件。</summary>
    public FocusedTextTarget? GetFocusedTextTarget()
    {
        _lastTextPattern = null;
        if (!TryGetForeground(out var foreground, out var focus, out var info)) return null;

        // UIA 只做「排除」和「补充位置」：取不到（程序不支持 / 正在销毁）不影响用光标判定。
        int[] runtimeId = Array.Empty<int>();
        bool uiaTextInput = false;
        RECT? box = null;
        IUIAutomationTextPattern2? textPattern = null;
        try
        {
            var element = _automation.GetFocusedElement();
            if (element != null && GetInt(element, UIA_ProcessIdPropertyId) != _ownProcessId)
            {
                if (GetBool(element, UIA_IsPasswordPropertyId)) return Reject("password");
                if (element.GetCurrentPropertyValue(UIA_IsEnabledPropertyId) is false) return Reject("disabled");

                var controlType = GetInt(element, UIA_ControlTypePropertyId);
                var hasValue = GetBool(element, UIA_IsValuePatternAvailablePropertyId);
                if (hasValue && GetBool(element, UIA_ValueIsReadOnlyPropertyId)) return Reject($"readonly ct={controlType}");

                uiaTextInput = controlType == UIA_EditControlTypeId ||
                               (hasValue && GetBool(element, UIA_IsTextPatternAvailablePropertyId));
                runtimeId = element.GetRuntimeId() ?? Array.Empty<int>();
                box = GetElementRect(element);
                textPattern = element.GetCurrentPattern(UIA_TextPattern2Id) as IUIAutomationTextPattern2;
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }

        GetWindowRect(foreground, out var window);
        var caret = GetCaret(textPattern, focus, info, window, box, out var caretSource);

        // 必须有「看得见」的光标才算输入状态：点到了文本框但光标还没出来、或者焦点已经移到按钮上，都不显示。
        if (caret == null) return Reject($"no visible caret ({caretSource}) uiaText={uiaTextInput}");

        _lastTextPattern = textPattern;
        var target = BuildTarget(foreground, focus, runtimeId, caret, box, window);
        LastReason = target == null ? "empty rect" : $"ok caret={caretSource} uiaText={uiaTextInput}";
        return target;
    }

    /// <summary>
    /// 轻量刷新：只重新取光标位置（打字、滚动时光标会动）。前台 / 焦点窗口变了、光标没了都返回 null，
    /// 由调用方再做一次完整判定。
    /// </summary>
    public FocusedTextTarget? RefreshCaret(FocusedTextTarget current)
    {
        if (!TryGetForeground(out var foreground, out var focus, out var info)) return null;
        if (foreground != current.RootWindow || focus != current.FocusWindow) return null;

        GetWindowRect(foreground, out var window);
        RECT? box = current.BoxRight > current.BoxLeft
            ? new RECT { Left = current.BoxLeft, Top = current.BoxTop, Right = current.BoxRight, Bottom = current.BoxBottom }
            : null;
        var caret = GetCaret(_lastTextPattern, focus, info, window, box, out _);
        if (caret == null) return null;
        return BuildTarget(foreground, focus, current.RuntimeId, caret, box, window);
    }

    /// <summary>
    /// 隐藏状态下的廉价轮询：前台有没有输入光标。浏览器在页面内部换输入框时不一定发焦点事件，
    /// 只靠事件会漏掉；这里只调一次 MSAA / GetGUIThreadInfo，不走 UIA。
    /// </summary>
    public bool HasAnyCaret(out IntPtr foreground, out IntPtr focus)
    {
        if (!TryGetForeground(out foreground, out focus, out var info)) return false;
        GetWindowRect(foreground, out var window);
        return GetCaret(null, focus, info, window, null, out _) != null;
    }

    private bool TryGetForeground(out IntPtr foreground, out IntPtr focus, out GUITHREADINFO info)
    {
        foreground = GetForegroundWindow();
        focus = IntPtr.Zero;
        info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (foreground == IntPtr.Zero) { LastReason = "no foreground"; return false; }
        if (IsHungAppWindow(foreground)) { LastReason = "hung"; return false; }

        // 本进程自己的窗口不处理：UIA / MSAA 会回调到本进程 UI 线程，而 UI 线程关闭功能时要等这条线程退出。
        var threadId = GetWindowThreadProcessId(foreground, out var pid);
        if (pid == _ownProcessId) { LastReason = "own process"; return false; }

        if (GetGUIThreadInfo(threadId, ref info) && info.hwndFocus != IntPtr.Zero)
            focus = info.hwndFocus;
        else
            focus = foreground;
        return true;
    }

    private FocusedTextTarget? Reject(string reason)
    {
        LastReason = reason;
        return null;
    }

    /// <summary>
    /// 依次尝试三个光标来源。<paramref name="box"/> 是 UIA 焦点元素的矩形：光标不在它里面就是**过期的光标**——
    /// 2026-09-14 诊断日志实测，Chromium / Electron 在焦点从输入框移到按钮之后，MSAA 光标仍停在原输入框的位置
    /// （光标坐标不变，焦点元素换成了 24×24 的按钮），不排除就会在没点文本框时也弹出小球。
    /// </summary>
    private static CaretRect? GetCaret(IUIAutomationTextPattern2? textPattern, IntPtr focus, GUITHREADINFO info, RECT window, RECT? box, out string source)
    {
        source = "uia";
        if (textPattern != null && TryUiaCaret(textPattern) is { } uia && IsInside(uia, window) && IsInsideBox(uia, box)) return uia;
        source = "msaa";
        if (TryMsaaCaret(focus) is { } msaa && IsInside(msaa, window) && IsInsideBox(msaa, box)) return msaa;
        source = "win32";
        if (TryWin32Caret(info) is { } win32 && IsInside(win32, window) && IsInsideBox(win32, box)) return win32;
        source = "none";
        return null;
    }

    private static bool IsInsideBox(CaretRect caret, RECT? box)
    {
        if (box is not { } b) return true;
        const int tolerance = 4;
        return caret.X >= b.Left - tolerance && caret.X <= b.Right + tolerance &&
               caret.Top >= b.Top - tolerance && caret.Bottom <= b.Bottom + tolerance;
    }

    private static CaretRect? TryUiaCaret(IUIAutomationTextPattern2 textPattern)
    {
        try
        {
            var range = textPattern.GetCaretRange(out var active);
            if (range == null || active == 0) return null;

            // 光标是退化区间，不少实现给不出矩形：先取光标前一个字符的右边缘，再退到光标后一个字符的左边缘。
            if (FirstRect(range.GetBoundingRectangles()) is { } direct) return new CaretRect(direct.X, direct.Top, direct.Bottom);

            var previous = range.Clone();
            if (previous.MoveEndpointByUnit(0 /* Start */, 0 /* Character */, -1) != 0 &&
                FirstRect(previous.GetBoundingRectangles()) is { } before)
                return new CaretRect(before.X + before.Width, before.Top, before.Bottom);

            var next = range.Clone();
            next.ExpandToEnclosingUnit(0 /* Character */);
            if (FirstRect(next.GetBoundingRectangles()) is { } after) return new CaretRect(after.X, after.Top, after.Bottom);
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        return null;
    }

    private static CaretRect? TryMsaaCaret(IntPtr focus)
    {
        if (focus == IntPtr.Zero) return null;
        try
        {
            var iid = IID_IAccessible;
            if (AccessibleObjectFromWindow(focus, OBJID_CARET, ref iid, out var obj) != 0 || obj is not IAccessible caret) return null;
            // 光标隐藏时（焦点离开输入框）对象还在，位置也还是旧的，只有状态带 STATE_SYSTEM_INVISIBLE。
            if (caret.get_accState(0) is int state && (state & STATE_SYSTEM_INVISIBLE) != 0) return null;
            caret.accLocation(out var left, out var top, out _, out var height, 0);
            if (height <= 0 || (left == 0 && top == 0)) return null;
            return new CaretRect(left, top, top + height);
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        return null;
    }

    private static CaretRect? TryWin32Caret(GUITHREADINFO info)
    {
        // 系统光标被 HideCaret 藏起来时 hwndCaret 仍在，只是不再闪烁。
        if (info.hwndCaret == IntPtr.Zero || (info.flags & GUI_CARETBLINKING) == 0 || info.rcCaret.Bottom <= info.rcCaret.Top) return null;
        var topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        var bottomRight = new POINT { X = info.rcCaret.Right, Y = info.rcCaret.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight)) return null;
        return new CaretRect(topLeft.X, topLeft.Y, bottomRight.Y);
    }

    private static FocusedTextTarget? BuildTarget(IntPtr foreground, IntPtr focus, int[] runtimeId, CaretRect? caret, RECT? box, RECT window)
    {
        // 输入框矩形只保留落在前台窗口里的那一段（滚动容器里的输入框可能一部分在窗口外）。
        RECT clipped;
        if (box is { } b)
        {
            clipped = new RECT
            {
                Left = Math.Max(b.Left, window.Left),
                Top = Math.Max(b.Top, window.Top),
                Right = Math.Min(b.Right, window.Right),
                Bottom = Math.Min(b.Bottom, window.Bottom),
            };
            if (clipped.Right <= clipped.Left || clipped.Bottom <= clipped.Top)
            {
                if (caret == null) return null;
                clipped = default;
            }
        }
        else if (caret == null)
        {
            return null;
        }
        else
        {
            clipped = default;
        }

        return caret is { } c
            ? new FocusedTextTarget(foreground, focus, runtimeId, true, c.X, c.Top, c.Bottom, clipped.Left, clipped.Top, clipped.Right, clipped.Bottom)
            : new FocusedTextTarget(foreground, focus, runtimeId, false, 0, 0, 0, clipped.Left, clipped.Top, clipped.Right, clipped.Bottom);
    }

    private static RECT? GetElementRect(IUIAutomationElement element)
    {
        if (element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId) is not double[] { Length: 4 } r || r[2] <= 0 || r[3] <= 0)
            return null;
        return new RECT
        {
            Left = (int)Math.Round(r[0]),
            Top = (int)Math.Round(r[1]),
            Right = (int)Math.Round(r[0] + r[2]),
            Bottom = (int)Math.Round(r[1] + r[3]),
        };
    }

    private static (int X, int Top, int Width, int Bottom)? FirstRect(double[]? rects)
    {
        if (rects is not { Length: >= 4 } || rects[3] <= 0) return null;
        return ((int)Math.Round(rects[0]), (int)Math.Round(rects[1]), (int)Math.Round(rects[2]), (int)Math.Round(rects[1] + rects[3]));
    }

    private static bool IsInside(CaretRect caret, RECT window) =>
        caret.X >= window.Left - 2 && caret.X <= window.Right + 2 && caret.Top >= window.Top - 2 && caret.Bottom <= window.Bottom + 2;

    private static bool GetBool(IUIAutomationElement element, int propertyId) =>
        element.GetCurrentPropertyValue(propertyId) is true;

    private static int GetInt(IUIAutomationElement element, int propertyId) =>
        element.GetCurrentPropertyValue(propertyId) is int value ? value : 0;

    private readonly record struct CaretRect(int X, int Top, int Bottom);

    #region COM / Win32

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        IUIAutomationElement GetFocusedElement();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[] GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("506a921a-fcc9-409f-b23b-37eb74106872"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern2
    {
        void RangeFromPoint();
        void RangeFromChild();
        void GetSelection();
        void GetVisibleRanges();
        void get_DocumentRange();
        void get_SupportedTextSelection();
        void RangeFromAnnotation();
        IUIAutomationTextRange GetCaretRange(out int isActive);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        IUIAutomationTextRange Clone();
        void Compare();
        void CompareEndpoints();
        void ExpandToEnclosingUnit(int textUnit);
        void FindAttribute();
        void FindText();
        void GetAttributeValue();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)]
        double[] GetBoundingRectangles();
        void GetEnclosingElement();
        void GetText();
        void Move();
        int MoveEndpointByUnit(int endpoint, int textUnit, int count);
    }

    /// <summary>IAccessible 是双重接口：前 4 个槽位是 IDispatch 的方法。</summary>
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessible
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();
        void get_accParent();
        void get_accChildCount();
        void get_accChild();
        void get_accName();
        void get_accValue();
        void get_accDescription();
        void get_accRole();
        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([MarshalAs(UnmanagedType.Struct)] object child);
        void get_accHelp();
        void get_accHelpTopic();
        void get_accKeyboardShortcut();
        void get_accFocus();
        void get_accSelection();
        void get_accDefaultAction();
        void accSelect();
        void accLocation(out int left, out int top, out int width, out int height, [MarshalAs(UnmanagedType.Struct)] object child);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object accessible);

    #endregion
}
