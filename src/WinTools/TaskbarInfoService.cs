using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using WinTools.Services;

namespace WinTools;

/// <summary>A non-activating taskbar overlay with an optional tray-only mode; does not inject into Explorer.</summary>
internal sealed partial class TaskbarInfoService : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IntPtr _hwnd;
    private bool _enabled, _custom, _reading, _disposed;
    private bool _trayOnly, _background, _actions;
    private readonly SubclassProc _subclass;
    private int _buttonStart, _buttonWidth;
    private int _deviceStart, _deviceWidth;
    private float _deviceIconCenter;

    private int _hoveredButton = -1;
    private bool _trackingMouse;
    internal event Action<string>? ActionRequested;
    internal string TraySummary => !_enabled ? "WinTools" : _custom ? _customText : Status;
    private int _offset;
    private string _customText = "", _quotaText = "Codex 正在读取额度…";
    private string? _lastDrawing;
    private string? _lastPlacement;
    private IntPtr _taskbarOwner;
    private bool _visible;
    private (int X, int Y, int Width, int Height)? _bounds;
    private DateTime _nextRefresh = DateTime.MinValue;
    internal string Status { get; private set; } = "正在读取 Codex 额度";
    internal event Action? StatusChanged;

    internal TaskbarInfoService()
    {
        _subclass = WindowProc;
        _hwnd = CreateWindowEx(0x08080080, "STATIC", "WinTools 任务栏信息", 0x80000000,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        if (!SetWindowSubclass(_hwnd, _subclass, 1, 0))
        {
            DestroyWindow(_hwnd);
            throw new InvalidOperationException("无法创建任务栏快捷按钮");
        }
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => SafeTick();
    }

    internal void Apply(Config config)
    {
        _enabled = config.EnableTaskbarInfo;
        _trayOnly = config.TaskbarInfoPlacement == "tray";
        _background = config.TaskbarInfoBackground;
        _actions = config.TaskbarInfoActions;
        _custom = config.TaskbarInfoMode == "text";
        _customText = (config.TaskbarInfoText ?? "").Replace('\r', ' ').Replace('\n', ' ');
        _offset = Math.Clamp(config.TaskbarInfoOffset, 0, 1200);
        if (_enabled) { _timer.Start(); SafeTick(); }
        else { _timer.Stop(); Hide(); }
        StatusChanged?.Invoke();
    }

    internal void Refresh() { _nextRefresh = DateTime.MinValue; if (_enabled && !_custom) _ = RefreshAsync(); }

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex)
        {
            _timer.Stop();
            Hide();
            Status = "任务栏信息显示失败，请关闭后重新开启";
            ErrorReporter.Log("TaskbarInfo.Display", ex);
            StatusChanged?.Invoke();
        }
    }

    private void Tick()
    {
        if (_disposed || !_enabled) return;
        if (!_custom && DateTime.UtcNow >= _nextRefresh) _ = RefreshAsync();
        if (_trayOnly) { Hide(); return; }
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !IsWindowVisible(taskbar) || !GetWindowRect(taskbar, out var bar)) { Hide(); return; }
        if (_taskbarOwner != taskbar)
        {
            // An owned popup stays above its owner when Explorer raises the taskbar.
            // This is ownership, not parenting or injection into Explorer.
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtr(_hwnd, -8, taskbar); // GWLP_HWNDPARENT: popup owner
            if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            _taskbarOwner = taskbar;
            _bounds = null;
        }
        var monitor = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(taskbar, 2), ref monitor)) { Hide(); return; }
        // Only horizontal bottom taskbars. Off-screen bounds indicate auto-hide.
        if (bar.Right - bar.Left < bar.Bottom - bar.Top || bar.Top < monitor.Monitor.Bottom - 200
            || Math.Min(bar.Bottom, monitor.Monitor.Bottom) - Math.Max(bar.Top, monitor.Monitor.Top) < (bar.Bottom - bar.Top) * .75)
        { Hide(); return; }
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != taskbar && GetWindowRect(foreground, out var front)
            && front.Left <= monitor.Monitor.Left && front.Top <= monitor.Monitor.Top
            && front.Right >= monitor.Monitor.Right && front.Bottom >= monitor.Monitor.Bottom)
        {
            var name = new System.Text.StringBuilder(128);
            GetClassName(foreground, name, name.Capacity);
            if (name.ToString() is not "Progman" and not "WorkerW") { Hide(); return; }
        }
        var scale = Math.Max(1, GetDpiForWindow(taskbar) / 96f);
        var height = Math.Max(1, bar.Bottom - bar.Top - (int)(8 * scale));
        var label = _custom ? _customText : _quotaText;
        var infoWidth = MeasureInfoWidth(label, scale);
        var width = Math.Min((int)((_actions ? 240 : 0) * scale) + infoWidth, (bar.Right - bar.Left) / 3);
        var x = bar.Left + (int)((8 + _offset) * scale);
        if (x + width > bar.Right) { Hide(); return; }
        var y = bar.Top + (bar.Bottom - bar.Top - height) / 2;
        var dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 1;
        var text = _custom ? _customText : _quotaText;
        var drawing = $"{text}|{width}|{height}|{dark}|{scale}|{_background}|{_actions}|{_hoveredButton}";
        if (drawing != _lastDrawing)
        {
            Draw(text, width, height, scale, dark);
            _lastDrawing = drawing;
        }
        var bounds = (x, y, width, height);
        if (!_visible || _bounds != bounds)
        {
            // Do not reinsert the window at the top of the Z order on every timer tick.
            if (!SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, height,
                0x0010 | 0x0200 | (_visible ? 0x0004u : 0x0040u))) // NOACTIVATE | NOOWNERZORDER | NOZORDER/SHOWWINDOW
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _bounds = bounds;
            _visible = true;
        }
        var placement = $"任务栏显示区域：{x},{y}，{width}×{height}";
        if (_lastPlacement != placement)
        {
            _lastPlacement = placement;
            ErrorReporter.Log("TaskbarInfo.Placement", placement);
        }
    }

    private void Hide()
    {


        if (!_visible) return;
        ShowWindow(_hwnd, 0);
        _visible = false;
        _hoveredButton = -1;
        _trackingMouse = false;
    }

    private int ButtonAt(int x) => !_actions || _buttonWidth <= 0 ? -1
        : x >= _deviceStart && x < _deviceStart + _deviceWidth ? 3
        : x >= _buttonStart && x < _buttonStart + 3 * _buttonWidth ? (x - _buttonStart) / _buttonWidth : -1;

    internal bool IsLibraryButtonAt(int screenX, int screenY) => _visible
        && GetWindowRect(_hwnd, out var rect) && screenY >= rect.Top && screenY < rect.Bottom
        && ButtonAt(screenX - rect.Left) == 0;

    private void SetHoveredButton(int button)
    {
        if (_hoveredButton == button) return;
        _hoveredButton = button;
        SafeTick();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        // Keep typing focus in the previous app. The native tray menu supplies keyboard access.
        if (message == 0x0021) return new IntPtr(3); // MA_NOACTIVATE
        if (message == 0x0084) return new IntPtr(1); // HTCLIENT (STATIC otherwise passes input to its parent)
        if (message == 0x0200) // WM_MOUSEMOVE
        {
            if (!_trackingMouse)
            {
                var tracking = new TRACKMOUSEEVENT { Size = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2, Hwnd = hwnd };
                _trackingMouse = TrackMouseEvent(ref tracking);
            }
            SetHoveredButton(ButtonAt((short)(lParam.ToInt64() & 0xffff)));
            return IntPtr.Zero;
        }
        if (message == 0x02A3) // WM_MOUSELEAVE
        {
            _trackingMouse = false;
            SetHoveredButton(-1);
            return IntPtr.Zero;
        }
        if (message is 0x0202 or 0x0205)
        {
            var x = (short)(lParam.ToInt64() & 0xffff);
            var button = message == 0x0205 ? -1 : ButtonAt(x);
            if (message == 0x0202 && button < 0) return IntPtr.Zero;
            var action = button switch { 0 => "library", 1 => "stash", 2 => "voice", 3 => "audio", _ => "menu" };
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                if (_disposed) return;
                try
                {
                    if (action == "audio") _ = ShowAudioDevicesAsync();
                    else ActionRequested?.Invoke(action);
                }
                catch (Exception ex) { ErrorReporter.Log("TaskbarInfo.Action", ex); }
            });
            return IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private async Task RefreshAsync()
    {
        if (_reading || _disposed) return;
        _reading = true;
        _nextRefresh = DateTime.UtcNow.AddMinutes(2);
        try
        {
            var snapshot = await Task.Run(() => CodexQuotaReader.ReadAsync(_lifetime.Token));
            if (_disposed) return;
            _quotaText = snapshot.Text;
            Status = $"{snapshot.Detail}\n更新于 {DateTime.Now:HH:mm:ss} · 每 2 分钟自动刷新";
        }
        catch (OperationCanceledException)
        {
            if (_disposed) return;
            _quotaText = "Codex 额度读取超时";
            Status = "读取超时，请检查网络后刷新";
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            _quotaText = "Codex 额度暂不可用";
            Status = ex is InvalidOperationException ? ex.Message : "额度读取失败，请检查 Codex 安装、登录状态和网络";
        }
        finally { _reading = false; }
        if (!_disposed) { StatusChanged?.Invoke(); SafeTick(); }
    }

    private void Draw(string text, int width, int height, float scale, bool dark)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Alpha=1 makes the transparent hit area clickable without a visible card background.
            graphics.Clear(_background ? Color.Transparent : Color.FromArgb(1, 0, 0, 0));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var background = new SolidBrush(dark ? Color.FromArgb(235, 35, 35, 39) : Color.FromArgb(235, 242, 242, 245));
            using var path = new GraphicsPath();
            var diameter = Math.Min(12 * scale, height);
            path.AddArc(0, 0, diameter, diameter, 180, 90);
            path.AddArc(width - diameter, 0, diameter, diameter, 270, 90);
            path.AddArc(width - diameter, height - diameter, diameter, diameter, 0, 90);
            path.AddArc(0, height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            if (_background) graphics.FillPath(background, path);
            using var font = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 35));
            using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            _buttonWidth = (int)(36 * scale);
            _buttonStart = (int)(8 * scale);
            _deviceStart = _buttonStart + 3 * _buttonWidth + (int)(4 * scale);
            _deviceWidth = (int)(112 * scale);
            var infoLeft = _actions ? _deviceStart + _deviceWidth + 8 * scale : 0;
            DrawChip(graphics, new RectangleF(infoLeft, 2 * scale, Math.Max(1, width - infoLeft), height - 4 * scale), scale, dark, false);
            var textLeft = infoLeft + 12 * scale;
            graphics.DrawString(text, font, foreground, new RectangleF(textLeft, 0, Math.Max(1, width - textLeft - 12 * scale), height), format);
            if (_actions)
            {
                DrawChip(graphics, new RectangleF(_deviceStart, 2 * scale, _deviceWidth, height - 4 * scale), scale, dark, _hoveredButton == 3);
                using var deviceFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                var labelWidth = graphics.MeasureString("音频设备", font, PointF.Empty, StringFormat.GenericTypographic).Width;
                var groupWidth = 18 * scale + 6 * scale + labelWidth;
                var groupLeft = _deviceStart + (_deviceWidth - groupWidth) / 2;
                _deviceIconCenter = groupLeft + 9 * scale;
                using var labelFormat = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center };
                graphics.DrawString("音频设备", font, foreground, new RectangleF(groupLeft + 24 * scale, 0, labelWidth + scale, height), labelFormat);
                if (_hoveredButton >= 0 && _hoveredButton < 3)
                {
                    var hoverX = _buttonStart + _hoveredButton * _buttonWidth + 2 * scale;
                    var hoverY = 2 * scale;
                    var hoverWidth = _buttonWidth - 4 * scale;
                    var hoverHeight = height - 4 * scale;
                    var corner = Math.Min(8 * scale, hoverHeight);
                    using var hoverPath = new GraphicsPath();
                    hoverPath.AddArc(hoverX, hoverY, corner, corner, 180, 90);
                    hoverPath.AddArc(hoverX + hoverWidth - corner, hoverY, corner, corner, 270, 90);
                    hoverPath.AddArc(hoverX + hoverWidth - corner, hoverY + hoverHeight - corner, corner, corner, 0, 90);
                    hoverPath.AddArc(hoverX, hoverY + hoverHeight - corner, corner, corner, 90, 90);
                    hoverPath.CloseFigure();
                    using var hoverBrush = new SolidBrush(dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(25, 0, 0, 0));
                    graphics.FillPath(hoverBrush, hoverPath);
                }
                DrawFeatureIcons(graphics, foreground, scale, height);
            }
        }
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(dc, handle);
        try
        {
            var size = new SIZE { Width = width, Height = height };
            var source = new POINT();
            var blend = new BLENDFUNCTION { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(_hwnd, screen, IntPtr.Zero, ref size, dc, ref source, 0, ref blend, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SelectObject(dc, old); DeleteObject(handle); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        DisposeFeatureIcons();
        _audioPicker?.Close();


        RemoveWindowSubclass(_hwnd, _subclass, 1);
        DestroyWindow(_hwnd);
        _lifetime.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint Size, Flags; public IntPtr Hwnd; public uint HoverTime; }
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tracking);
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int show);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, IntPtr destination, ref SIZE size, IntPtr dc, ref POINT source, uint key, ref BLENDFUNCTION blend, uint flags);
}
