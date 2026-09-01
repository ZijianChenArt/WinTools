using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinTools;

/// <summary>
/// 监视从桌面图标开始的正常拖放。不会拦截拖动；鼠标松开后通知桌面整理服务恢复分区位置。
/// </summary>
public sealed class DesktopIconPositionService : IDisposable
{
    private readonly GlobalDragWatcher _watcher = new();
    private bool _enabled;
    private bool _desktopDragActive;

    public event EventHandler? DesktopDragEnded;

    public DesktopIconPositionService()
    {
        _watcher.DragStarted += OnDragStarted;
        _watcher.DragEnded += OnDragEnded;
    }

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _desktopDragActive = false;
        if (enabled) _watcher.Start();
        else _watcher.Stop();
    }

    private void OnDragStarted(object? sender, DragStartedArgs args)
    {
        _desktopDragActive = IsDesktopIconView(args.SourceHwnd);
    }

    private void OnDragEnded(object? sender, EventArgs args)
    {
        if (!_desktopDragActive) return;
        _desktopDragActive = false;
        DesktopDragEnded?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsDesktopIconView(IntPtr hwnd)
    {
        try
        {
            var current = hwnd;
            while (current != IntPtr.Zero)
            {
                if (string.Equals(GetWindowClass(current), "SysListView32", StringComparison.Ordinal))
                {
                    var parent = GetParent(current);
                    return parent != IntPtr.Zero &&
                        string.Equals(GetWindowClass(parent), "SHELLDLL_DefView", StringComparison.Ordinal);
                }
                current = GetParent(current);
            }
        }
        catch { /* Explorer 重启期间忽略本次拖动。 */ }
        return false;
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var value = new StringBuilder(64);
        return GetClassName(hwnd, value, value.Capacity) > 0 ? value.ToString() : "";
    }

    public void Dispose()
    {
        _watcher.DragStarted -= OnDragStarted;
        _watcher.DragEnded -= OnDragEnded;
        _watcher.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
}
