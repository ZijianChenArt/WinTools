using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Window = Microsoft.UI.Xaml.Window;

namespace WinTools;

/// <summary>
/// 悬浮暂存管理器：订阅全局拖拽事件，仅在从资源管理器拖拽文件时显示悬浮窗。
/// 有文件时保持显示；文件全部移出后自动隐藏。
/// </summary>
public sealed class FloatingStashManager : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly GlobalDragWatcher _watcher;
    private FloatingStashWindow? _window;
    private ProgramDropWindow? _programWindow;
    private bool _enabled;
    private bool _programAssociationEnabled;
    private int _lastDragX, _lastDragY;
    private bool _programWindowShownForThisDrag;
    private int _dragDecisionToken;
    private readonly HashSet<IntPtr> _winToolsHwnds = new();

    public FloatingStashManager(DispatcherQueue dispatcher, Window mainWindow)
    {
        _dispatcher = dispatcher;
        _watcher = new GlobalDragWatcher();
        _watcher.ShouldIgnoreDragAt = ShouldIgnoreDragAt;
        _watcher.DragStarted += (_, args) => _dispatcher.TryEnqueue(() =>
            ShowForDrag(args.X, args.Y, args.CurrentX, args.CurrentY, args.SourceHwnd));
        _watcher.DragEnded += (_, _) => _dispatcher.TryEnqueue(OnDragEnded);
        RegisterWinToolsWindow(mainWindow);
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            EnsureWindow();
            WarmUpWindows();
            _watcher.Start();
        }
        else
        {
            Interlocked.Increment(ref _dragDecisionToken);
            _watcher.Stop();
            _window?.HideWindow();
            _programWindow?.HideWindow();
        }
    }

    public void SetProgramAssociationEnabled(bool enabled)
    {
        _programAssociationEnabled = enabled;
        if (!enabled)
            _programWindow?.HideWindow();
    }

    public void ShowWindow()
    {
        EnsureWindow();
        _window?.PositionToTopRight();
        _window?.ShowWindow(activate: true, animate: false);
    }

    /// <summary>注册 WinTools 窗口句柄，避免拖拽自身窗口时触发悬浮暂存。</summary>
    public void RegisterWindow(Window window) => RegisterWinToolsWindow(window);

    /// <summary>预初始化悬浮窗句柄，避免首次拖拽时同步创建导致卡顿。</summary>
    private void WarmUpWindows()
    {
        _window?.EnsureInitialized();
        _programWindow?.EnsureInitialized();
    }

    /// <summary>
    /// 全局拖拽开始：仅在“来自资源管理器的文件拖拽”场景显示悬浮窗。
    /// 拖拽窗口/普通鼠标拖动不触发悬浮暂存。
    /// </summary>
    private void ShowForDrag(int downX, int downY, int currentX, int currentY, IntPtr sourceHwnd)
    {
        if (ShouldIgnoreDragAt(downX, downY))
            return;

        _lastDragX = currentX;
        _lastDragY = currentY;
        _programWindowShownForThisDrag = false;
        var token = Interlocked.Increment(ref _dragDecisionToken);

        // 资源管理器来源判断放到后台，完成后立即显示，不增加人为延迟。
        _ = Task.Run(() =>
        {
            if (!_enabled || token != _dragDecisionToken) return;

            // 使用拖拽开始时捕捉的来源窗口句柄判断，避免后续窗口移动影响来源识别。
            // 桌面空白处拉框选择同样会产生“按下并移动”，但它不是文件拖放。
            // 只有按下点真正命中桌面图标时，才允许桌面来源触发悬浮暂存。
            bool fromFileManager = IsFromFileManager(sourceHwnd)
                && !IsDesktopBlankArea(sourceHwnd, downX, downY);
            _dispatcher.TryEnqueue(() =>
            {
                if (!_enabled || token != _dragDecisionToken) return;

                if (fromFileManager)
                {
                    // 悬浮窗刻意避开光标，避免动态窗口切换导致系统拖动预览闪动。
                    EnsureWindow();
                    _window?.MoveBelowCursor(currentX, currentY);
                    _window?.ShowWindow(activate: false, animate: false);
                }
            });
        });
    }

    /// <summary>
    /// 暂存窗口检测到拖拽文件的扩展名后调用。
    /// 同时显示暂存窗口和程序选择窗口（已过滤），避免任何闪动。
    /// </summary>
    private void OnExtensionsDetected(HashSet<string> extensions)
    {
        if (extensions.Count == 0) return;

        EnsureWindow();
        if (_window == null) return;

        _window.MoveBelowCursor(_lastDragX, _lastDragY);
        _window.ShowWindow(activate: false);

        if (!_programAssociationEnabled) return;
        if (_programWindowShownForThisDrag) return;

        // 检查是否有匹配的程序，有则同时显示程序选择窗口（光标右上方）
        EnsureProgramWindow();
        if (_programWindow != null && _programWindow.RebuildPrograms()
            && _programWindow.HasMatchingPrograms(extensions))
        {
            _programWindow.FilterByExtensions(extensions);
            var scale = (_window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var stashWidth = _window.AppWindow.Size.Width;
            _programWindow.PositionAboveCursor(_lastDragX, _lastDragY, stashWidth, scale);
            _programWindow.ShowWindow(activate: false);
        }

        _programWindowShownForThisDrag = true;
    }

    /// <summary>
    /// 全局拖拽结束 → 延迟检查列表是否为空再决定是否隐藏。
    /// 需要延迟是因为 DropArea_Drop 是异步的，鼠标松开时文件可能尚未加入列表。
    /// 程序选择窗口在拖拽结束后一律隐藏（它仅在拖拽进行中有用）。
    /// </summary>
    private void OnDragEnded()
    {
        Interlocked.Increment(ref _dragDecisionToken);
        if (_window == null) return;
        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _programWindow?.HideWindow();
            if (_window != null && !_window.HasItems)
                _window.HideWindow();
        };
        timer.Start();
    }

    private void OnWindowBecameEmpty(object? sender, EventArgs e) => _window?.HideWindow();

    private void EnsureWindow()
    {
        if (_window != null) return;
        _window = new FloatingStashWindow();
        _window.BecameEmpty += OnWindowBecameEmpty;
        _window.DragExtensionsDetected += OnDragExtensionsDetected;
        ThemeService.Register(_window);
        UiStyleService.Register(_window);
        RegisterWinToolsWindow(_window);
    }

    /// <summary>暂存窗口检测到拖入文件的扩展名 → 作为备用触发。</summary>
    private void OnDragExtensionsDetected(HashSet<string> extensions)
    {
        OnExtensionsDetected(extensions);
    }

    private void EnsureProgramWindow()
    {
        if (_programWindow != null) return;
        _programWindow = new ProgramDropWindow();
        ThemeService.Register(_programWindow);
        UiStyleService.Register(_programWindow);
        RegisterWinToolsWindow(_programWindow);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        if (_window != null)
        {
            _window.BecameEmpty -= OnWindowBecameEmpty;
            _window.HideWindow();
        }
        _programWindow?.HideWindow();
    }

    #region 判断拖拽是否来自文件管理器

    private void RegisterWinToolsWindow(Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd != IntPtr.Zero)
                _winToolsHwnds.Add(hwnd);
        }
        catch { /* ignore */ }
    }

    /// <summary>全局拖拽是否应忽略（WinTools 自身窗口、标题栏/边框移动窗口等）。</summary>
    private bool ShouldIgnoreDragAt(int x, int y) =>
        IsFromWinToolsWindow(x, y) || IsWindowMoveDrag(x, y);

    /// <summary>拖拽是否起始于 WinTools 自身窗口（移动窗口标题栏等场景应忽略）。</summary>
    private bool IsFromWinToolsWindow(int x, int y)
    {
        if (_winToolsHwnds.Count == 0) return false;
        try
        {
            var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
            if (hwnd == IntPtr.Zero) return false;
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            return _winToolsHwnds.Contains(root);
        }
        catch { return false; }
    }

    /// <summary>
    /// 按下点是否位于窗口非客户区（标题栏、系统按钮、边框）。
    /// 此类拖动由系统处理窗口移动，不会产生 Shell 文件拖放。
    /// </summary>
    private static bool IsWindowMoveDrag(int x, int y)
    {
        try
        {
            var ht = HitTestAt(x, y);
            return ht is HTCAPTION or HTSYSMENU or HTMINBUTTON or HTMAXBUTTON or HTCLOSE
                or HTTOP or HTBOTTOM or HTLEFT or HTRIGHT
                or HTTOPLEFT or HTTOPRIGHT or HTBOTTOMLEFT or HTBOTTOMRIGHT;
        }
        catch { return false; }
    }

    private static int HitTestAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return HTNOWHERE;
        var root = GetAncestor(hwnd, GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var lParam = (IntPtr)(((long)y << 16) | ((long)x & 0xFFFF));
        return (int)SendMessage(root, WM_NCHITTEST, IntPtr.Zero, lParam);
    }

    /// <summary>
    /// 检查指定窗口句柄是否位于资源管理器的 *文件列表区域*（SHELLDLL_DefView）。
    /// 传入的句柄应是拖拽刚开始时捕捉的来源窗口，窗口层级关系不随窗口移动而改变，
    /// 因此即使后续窗口被移走仍可准确判断（句柄保持有效）。
    /// 注意：桌面图标视图同样含 SHELLDLL_DefView，但用户从桌面拖文件也属于合法暂存场景，
    /// 这里只排除“移动窗口”这类非文件拖拽（其句柄不在文件列表层级内）。
    /// </summary>
    private static bool IsFromFileManager(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return false;

            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;

            GetWindowThreadProcessId(root, out uint pid);
            if (pid == 0) return false;
            var proc = Process.GetProcessById((int)pid);
            if (!string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                return false;

            var current = hwnd;
            while (current != IntPtr.Zero)
            {
                if (IsShellDefView(current))
                    return true;
                if (current == root) break;
                current = GetParent(current);
            }
            return false;
        }
        catch { return false; }
    }

    private static bool IsShellDefView(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return string.Equals(sb.ToString(), "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断按下点是否位于桌面 ListView 的空白区域。资源管理器文件列表不使用此规则，
    /// 因而不会影响在普通文件夹中拖动文件。
    /// </summary>
    private static bool IsDesktopBlankArea(IntPtr sourceHwnd, int screenX, int screenY)
    {
        try
        {
            var listView = sourceHwnd;
            while (listView != IntPtr.Zero
                   && !string.Equals(GetWindowClassName(listView), "SysListView32", StringComparison.Ordinal))
                listView = GetParent(listView);

            if (listView == IntPtr.Zero) return false;
            var parent = GetParent(listView);
            if (parent == IntPtr.Zero
                || !string.Equals(GetWindowClassName(parent), "SHELLDLL_DefView", StringComparison.Ordinal))
                return false;

            var point = new POINT { X = screenX, Y = screenY };
            if (!ScreenToClient(listView, ref point)) return false;
            return HitTestDesktopItem(listView, point) == -1;
        }
        catch { return false; }
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>跨进程命中桌面图标；失败时保守地允许拖拽，避免破坏正常文件拖放。</summary>
    private static int HitTestDesktopItem(IntPtr listView, POINT clientPoint)
    {
        GetWindowThreadProcessId(listView, out var processId);
        if (processId == 0) return int.MinValue;

        var process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, processId);
        if (process == IntPtr.Zero) return int.MinValue;

        var size = Marshal.SizeOf<LVHITTESTINFO>();
        var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)size,
            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        var local = Marshal.AllocHGlobal(size);
        try
        {
            if (remote == IntPtr.Zero) return int.MinValue;
            var hit = new LVHITTESTINFO { pt = clientPoint, iItem = -1, iSubItem = -1, iGroup = -1 };
            Marshal.StructureToPtr(hit, local, false);
            if (!WriteProcessMemory(process, remote, local, (nuint)size, out _)) return int.MinValue;
            SendMessage(listView, (int)LVM_HITTEST, IntPtr.Zero, remote);
            if (!ReadProcessMemory(process, remote, local, (nuint)size, out _)) return int.MinValue;
            return Marshal.PtrToStructure<LVHITTESTINFO>(local).iItem;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
            if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
            CloseHandle(process);
        }
    }

    private const uint GA_ROOT = 2;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTNOWHERE = 0;
    private const int HTCAPTION = 2;
    private const int HTSYSMENU = 3;
    private const int HTMINBUTTON = 8;
    private const int HTMAXBUTTON = 9;
    private const int HTCLOSE = 20;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_HITTEST = LVM_FIRST + 18;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}
