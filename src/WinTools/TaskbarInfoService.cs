using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using WinTools.Services;

namespace WinTools;

/// <summary>A non-activating taskbar overlay; does not inject into Explorer.</summary>
/// <remarks>
/// Position and size come from <c>TaskbarInfoService.Layout.cs</c>: the overlay follows the selected left / center
/// alignment, floats above the taskbar when that position overlaps an icon cluster, and shrinks through fixed levels
/// only when the taskbar is too narrow.
/// </remarks>
internal sealed partial class TaskbarInfoService : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IntPtr _hwnd;
    private bool _enabled, _reading, _disposed;
    private bool _actions;
    private bool _centered;
    private float _codexStart, _claudeStart;
    private readonly SubclassProc _subclass;
    private int _buttonStart, _buttonWidth;
    private int _deviceStart, _deviceWidth;
    private float _deviceIconCenter;
    private string _claudeText = "Claude 额度暂不可用";
    private DateTime _nextClaudeRefresh;
    private bool _readingClaude;
    private int _claudeRevision;
    private int _codexDrawWidth, _claudeDrawWidth;

    // 入口：每项 = 该入口自己的开关 && 对应功能已开启（Apply 里合成）。
    private int[] _iconIds = Array.Empty<int>();   // 0 桌面库 / 1 暂存 / 2 语音，按显示顺序
    private int[] _shownIcons = Array.Empty<int>(); // 当前布局实际画出的图标；折叠成单按钮时为 { MenuId }
    private bool _showAudio;
    private Layout _layout;
    private const int MenuId = 6;

    // 入口上的附加状态
    internal Func<int>? StashCountProvider { get; set; }
    internal Func<bool>? VoiceActiveProvider { get; set; }
    private int _stashCount;
    private bool _voiceActive;

    private int _hoveredButton = -1;
    private bool _trackingMouse;
    internal event Action<string>? ActionRequested;
    private string _quotaText = "Codex 正在读取额度…";
    private string? _lastDrawing;
    private string? _lastPlacement;
    private IntPtr _taskbarOwner;
    private bool _visible;
    private bool _floating; // 悬浮在任务栏上方：画一张胶囊形半透明底卡，弹窗从信息条上沿弹出
    private int _floatGap = 10; // 悬浮时与任务栏上沿的距离（DIP），设置页可调
    private const float FloatingIdleScale = .75f;
    private bool _pointerInside;
    private (int X, int Y, int Width, int Height)? _bounds;
    private DateTime _nextRefresh = DateTime.MinValue;
    internal string Status { get; private set; } = "正在读取 Codex 额度";
    internal string CodexDisplayText => _quotaText.Replace("Codex", "").Trim();
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
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => SafeTick();
        _hoverTimer = _dispatcher.CreateTimer();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(16);
        _hoverTimer.Tick += (_, _) => HoverFrame();
    }

    internal void Apply(Config config)
    {
        ResetHover();
        _enabled = config.EnableTaskbarInfo;
        _actions = config.TaskbarInfoActions;
        _centered = config.TaskbarInfoAlignment == "center";
        _floatGap = config.TaskbarFloatGap;
        _iconIds = new[]
        {
            _actions && config.TaskbarShowLibrary && config.EnableDesktopCard ? 0 : -1,
            _actions && config.EnableDragStash ? 1 : -1,
            _actions && config.TaskbarShowVoice ? 2 : -1,
        }.Where(id => id >= 0).ToArray();
        _showAudio = _actions; // 音频设备入口恒显示，没有单独的开关
        _lastDrawing = null;
        _bounds = null;
        if (_enabled) { _timer.Start(); SafeTick(); }
        else { _timer.Stop(); Hide(); }
        StatusChanged?.Invoke();
    }

    internal void Refresh() { _nextRefresh = DateTime.MinValue; if (_enabled) _ = RefreshAsync(); }

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
        if (DateTime.UtcNow >= _nextClaudeRefresh && !_readingClaude) _ = RefreshClaudeAsync();
        if (DateTime.UtcNow >= _nextRefresh) _ = RefreshAsync();
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
        ScheduleScan(taskbar, bar, scale);
        PollStatus();
        if (!TryPlace(bar, scale, out var layout, out var x, out var floating)) { Hide(); return; }
        // 只收小悬浮模式。间距仍使用屏幕 DPI，不能随内容缩放；底边固定，展开向上生长。
        var screenScale = scale;
        if (floating)
        {
            scale *= _floatingScale;
            height = Math.Max(1, (int)(height * _floatingScale));
            // 保持同一档内容，避免动画中途因可用宽度变化增减入口。
            layout = BuildLayout(layout.Level, scale);
            var floatingWidth = FloatingWidth(layout, scale);
            x = _centered ? bar.Left + (bar.Right - bar.Left - floatingWidth) / 2 : bar.Left + (int)(8 * screenScale);
        }
        Place(layout, scale);
        _floating = floating;
        var width = floating ? FloatingWidth(layout, scale) : layout.Width;
        if (floating && _hoverAnchorX is int anchor)
        {
            // 离开后的收回同时回到原来的偏左 / 居中位置。
            var returning = _hoverTarget == FloatingIdleScale && _leaveAfter == 0;
            var weight = returning ? Math.Clamp((_floatingScale - FloatingIdleScale) / Math.Max(.0001f, _hoverFrom - FloatingIdleScale), 0, 1) : 1;
            x = TaskbarHoverMotion.Left(x, anchor, HoverAnchorLocal(width), weight, bar.Left, Math.Max(bar.Left, bar.Right - width));
        }
        if (x + width > bar.Right) { Hide(); return; }
        // 悬浮时上下各加一段内边距，内容区（气泡、图标）和在任务栏里时完全一样。
        var padY = floating ? FloatingPadY(scale) : 0;
        var windowHeight = height + 2 * padY;
        var y = floating
            ? Math.Max(monitor.Monitor.Top, bar.Top - windowHeight - (int)(_floatGap * screenScale))
            : bar.Top + (bar.Bottom - bar.Top - height) / 2;
        // 尺寸变化后用新的屏幕坐标重算命中，避免仍高亮 / 点击缩小状态下的旧按钮。
        _hoveredButton = _pointerInside && GetCursorPos(out var cursor)
            && cursor.X >= x && cursor.X < x + width && cursor.Y >= y && cursor.Y < y + windowHeight
            ? HitAt(cursor.X - x) : -1;
        var dark = (int?)Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 1;
        var drawing = $"{_quotaText}|{_claudeText}|{width}|{height}|{dark}|{scale}|{layout}|{string.Join(',', _shownIcons)}|{_hoveredButton}|{_reading}|{_readingClaude}|{_stashCount}|{_voiceActive}|{_audioName}|{floating}";
        if (drawing != _lastDrawing)
        {
            Draw(width, height, padY, scale, dark);
            _lastDrawing = drawing;
        }
        var bounds = (x, y, width, windowHeight);
        if (!_visible || _bounds != bounds)
        {
            // Do not reinsert the window at the top of the Z order on every timer tick.
            if (!SetWindowPos(_hwnd, new IntPtr(-1), x, y, width, windowHeight,
                0x0010 | 0x0200 | (_visible ? 0x0004u : 0x0040u))) // NOACTIVATE | NOOWNERZORDER | NOZORDER/SHOWWINDOW
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _bounds = bounds;
            _visible = true;
            WarmAudioPicker();
        }
        var placement = $"任务栏{(floating ? "上方悬浮区域" : "显示区域")}：{x},{y}，{width}×{height}，收缩级别 {layout.Level}";
        if (!_hoverTimer.IsRunning && _lastPlacement != placement)
        {
            _lastPlacement = placement;
            ErrorReporter.Log("TaskbarInfo.Placement", placement);
        }
    }

    private void PollStatus()
    {
        try { _stashCount = StashCountProvider?.Invoke() ?? 0; } catch (Exception) { _stashCount = 0; }
        try { _voiceActive = VoiceActiveProvider?.Invoke() ?? false; } catch (Exception) { _voiceActive = false; }
        if (_showAudio && !_readingAudio && DateTime.UtcNow >= _nextAudioRead) _ = RefreshAudioNameAsync();
    }

    private void Hide()
    {
        ResetHover();
        if (!_visible) return;
        ShowWindow(_hwnd, 0);
        _visible = false;
        _hoveredButton = -1;
        _trackingMouse = false;
        _pointerInside = false;
    }

    private int ButtonAt(int x)
    {
        if (_buttonWidth <= 0) return -1;
        if (_shownIcons.Length > 0 && x >= _buttonStart && x < _buttonStart + _shownIcons.Length * _buttonWidth)
            return _shownIcons[(x - _buttonStart) / _buttonWidth];
        return _deviceWidth > 0 && x >= _deviceStart && x < _deviceStart + _deviceWidth ? 3 : -1;
    }

    private int HitAt(int x) => _codexDrawWidth > 0 && x >= _codexStart && x < _codexStart + _codexDrawWidth ? 4
        : _claudeDrawWidth > 0 && x >= _claudeStart && x < _claudeStart + _claudeDrawWidth ? 5 : ButtonAt(x);

    internal bool IsLibraryButtonAt(int screenX, int screenY) => _visible
        && GetWindowRect(_hwnd, out var rect) && screenY >= rect.Top && screenY < rect.Bottom
        && ButtonAt(screenX - rect.Left) == 0;

    /// <summary>
    /// Screen position of the stash shortcut: horizontal centre of its icon and the top edge of the taskbar.
    /// Null while the shortcut is not on screen (overlay hidden, entry turned off, or collapsed).
    /// </summary>
    internal (int CenterX, int Top)? GetStashAnchor()
    {
        if (_disposed || !_visible || _buttonWidth <= 0 || _taskbarOwner == IntPtr.Zero) return null;
        var slot = Array.IndexOf(_shownIcons, 1);
        if (slot < 0) return null;
        if (!GetWindowRect(_hwnd, out var rect) || !GetWindowRect(_taskbarOwner, out var bar)) return null;
        return (rect.Left + _buttonStart + _buttonWidth * slot + _buttonWidth / 2, Math.Min(rect.Top, bar.Top));
    }

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
            if (!_pointerInside)
            {
                EnterOverlay((short)(lParam.ToInt64() & 0xffff));
            }
            else SetHoveredButton(HitAt((short)(lParam.ToInt64() & 0xffff)));
            return IntPtr.Zero;
        }
        if (message == 0x02A3) // WM_MOUSELEAVE
        {
            _trackingMouse = false;
            _pointerInside = false;
            _hoveredButton = -1;
            // 跨越边缘时留出缓冲；重新移入会取消收回，并从当前尺寸继续。
            if (_floating)
            {
                _leaveAfter = Environment.TickCount64 + 160;
                _hoverTimer.Start();
            }
            SafeTick();
            return IntPtr.Zero;
        }
        if (message is 0x0202 or 0x0205)
        {
            var x = (short)(lParam.ToInt64() & 0xffff);
            var button = message == 0x0205 ? -1 : HitAt(x);
            if (message == 0x0202 && button < 0) return IntPtr.Zero;
            if (button is 4 or 5)
            {
                if (button == 4) _ = RefreshAsync();
                else _ = RefreshClaudeAsync();
                SafeTick();
                return IntPtr.Zero;
            }
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

    private async Task RefreshClaudeAsync()
    {
        if (_readingClaude || _disposed) return;
        _readingClaude = true;
        var revision = _claudeRevision;
        _nextClaudeRefresh = DateTime.UtcNow.AddMinutes(5);
        try
        {
            var snapshot = await Task.Run(() => ClaudeAccountService.ReadAsync(_lifetime.Token));
            if (!_disposed && revision == _claudeRevision) _claudeText = snapshot.Text;
        }
        catch (Exception) { if (!_disposed && revision == _claudeRevision) _claudeText = "Claude 额度暂不可用"; }
        finally { _readingClaude = false; }
        if (!_disposed) SafeTick();
    }

    internal void SetClaudeSnapshot(QuotaSnapshot snapshot)
    {
        if (_disposed) return;
        _claudeRevision++;
        _claudeText = snapshot.Text;
        _nextClaudeRefresh = DateTime.UtcNow.AddMinutes(5);
        SafeTick();
    }

    private async Task RefreshAsync()
    {
        if (_reading || _disposed) return;
        _reading = true;
        // 每次读取都要启动一个 codex app-server 子进程；额度变化不快，与 Claude 统一为 5 分钟，
        // 需要最新值时点气泡手动刷新。
        _nextRefresh = DateTime.UtcNow.AddMinutes(5);
        try
        {
            var snapshot = await Task.Run(() => CodexQuotaReader.ReadAsync(_lifetime.Token));
            if (_disposed) return;
            _quotaText = snapshot.Text;
            Status = $"{snapshot.Detail}\n更新于 {DateTime.Now:HH:mm:ss} · 每 5 分钟自动刷新";
        }
        catch (OperationCanceledException)
        {
            if (_disposed) return;
            _quotaText = "Codex 额度暂不可用";
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

    /// <summary>「Codex  5小时 78% · 本周 62%」→「Codex 78% · 62%」；没有百分比时原样返回。</summary>
    private static string CompactQuota(string text)
    {
        var matches = Regex.Matches(text, @"\d+(?:\.\d+)?%");
        if (matches.Count == 0) return text;
        var name = text.StartsWith("Claude") ? "Claude" : text.StartsWith("Codex") ? "Codex" : "";
        return (name + " " + string.Join(" · ", matches.Select(m => m.Value))).Trim();
    }

    /// <param name="height">内容区高度；悬浮时窗口上下再各多出 <paramref name="padY"/>。</param>
    private void Draw(int width, int height, int padY, float scale, bool dark)
    {
        var layout = _layout;
        var windowHeight = height + 2 * padY;
        using var bitmap = new Bitmap(width, windowHeight, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            // Alpha=1 makes the transparent hit area clickable without a visible card background.
            graphics.Clear(Color.FromArgb(1, 0, 0, 0));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 悬浮时下面是任意窗口内容，只有线框看不清：补一张胶囊形的半透明底卡。
            if (_floating) DrawFloatingCard(graphics, width, windowHeight, scale, dark);
            graphics.TranslateTransform(0, padY);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            using var foreground = new SolidBrush(dark ? Color.WhiteSmoke : Color.FromArgb(30, 30, 35));
            using var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var refreshFormat = new StringFormat(format) { Alignment = StringAlignment.Center };
            if (_codexDrawWidth > 0)
            {
                DrawChip(graphics, new RectangleF(_codexStart, 2 * scale, _codexDrawWidth, height - 4 * scale), scale, dark, _hoveredButton == 4);
                var text = _hoveredButton == 4 ? (_reading ? "刷新中…" : "刷新") : layout.Compact ? CompactQuota(_quotaText) : _quotaText;
                graphics.DrawString(text, font, foreground, new RectangleF(_codexStart + 12 * scale, 0, Math.Max(1, _codexDrawWidth - 24 * scale), height), _hoveredButton == 4 ? refreshFormat : format);
            }
            if (_claudeDrawWidth > 0)
            {
                DrawChip(graphics, new RectangleF(_claudeStart, 2 * scale, _claudeDrawWidth, height - 4 * scale), scale, dark, _hoveredButton == 5);
                var text = _hoveredButton == 5 ? (_readingClaude ? "刷新中…" : "刷新") : layout.Compact ? CompactQuota(_claudeText) : _claudeText;
                graphics.DrawString(text, font, foreground, new RectangleF(_claudeStart + 12 * scale, 0, Math.Max(1, _claudeDrawWidth - 24 * scale), height), _hoveredButton == 5 ? refreshFormat : format);
            }
            if (_deviceWidth > 0)
            {
                DrawChip(graphics, new RectangleF(_deviceStart, 2 * scale, _deviceWidth, height - 4 * scale), scale, dark, _hoveredButton == 3);
                if (layout.Audio == 2)
                {
                    var label = string.IsNullOrWhiteSpace(_audioName) ? "音频设备" : _audioName;
                    var left = _deviceStart + 34 * scale;
                    graphics.DrawString(label, font, foreground, new RectangleF(left, 0, Math.Max(1, _deviceStart + _deviceWidth - 10 * scale - left), height), format);
                }
            }
            for (var slot = 0; slot < _shownIcons.Length; slot++)
            {
                if (_hoveredButton != _shownIcons[slot]) continue;
                var hoverX = _buttonStart + slot * _buttonWidth + 2 * scale;
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
            DrawBadges(graphics, scale);
        }
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var handle = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(dc, handle);
        try
        {
            var size = new SIZE { Width = width, Height = windowHeight };
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
        _hoverTimer.Stop();
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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
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
