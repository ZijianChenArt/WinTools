using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using WinTools.Services;

namespace WinTools;

/// <summary>窗口通用辅助：标题栏、尺寸适配与 Mica 材质。</summary>
public static class WindowHelper
{
    private const int DwmwaBorderColor = 34;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaCloak = 13;
    private const int DwmwaTransitionsForceDisabled = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    /// <summary>用 DWM Cloak 把窗口对合成器隐藏 / 显示（隐藏期间 XAML 仍正常渲染，但不绘制到屏幕）。</summary>
    public static void SetWindowCloak(Window window, bool cloak)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var value = cloak ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCloak, ref value, sizeof(int));
        }
        catch { /* ignore */ }
    }

    /// <summary>禁用窗口 DWM 过渡动画，减少显示/隐藏时的闪动。</summary>
    public static void DisableWindowTransitions(Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var value = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaTransitionsForceDisabled, ref value, sizeof(int));
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 在内容完成若干帧渲染后再取消 Cloak，避免 WinUI 3 显示窗口时露出白色默认表面（白闪）。
    /// 带超时兜底，防止 Rendering 不再触发时窗口永久不可见。
    /// </summary>
    public static void UncloakWhenRendered(Window window, Action? completed = null)
    {
        try
        {
            var dq = window.DispatcherQueue;
            var done = false;
            EventHandler<object>? onRendering = null;
            Microsoft.UI.Dispatching.DispatcherQueueTimer? fallback = null;
            var frames = 0;

            void Finish()
            {
                if (done) return;
                done = true;
                if (onRendering != null)
                    Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= onRendering;
                fallback?.Stop();
                SetWindowCloak(window, false);
                try { completed?.Invoke(); }
                catch (Exception ex) { ErrorReporter.Log("WindowHelper.UncloakWhenRendered.Completed", ex); }
            }

            onRendering = (_, _) =>
            {
                // 等待第 2 帧，确保新内容已合成
                if (++frames < 2) return;
                Finish();
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += onRendering;

            fallback = dq.CreateTimer();
            fallback.Interval = TimeSpan.FromMilliseconds(200);
            fallback.IsRepeating = false;
            fallback.Tick += (_, _) => Finish();
            fallback.Start();
        }
        catch
        {
            SetWindowCloak(window, false);
            try { completed?.Invoke(); }
            catch (Exception ex) { ErrorReporter.Log("WindowHelper.UncloakWhenRendered.Fallback", ex); }
        }
    }

    /// <summary>DWMWA_COLOR_NONE：让 DWM 完全不画窗口边框。</summary>
    private const uint DwmColorNone = 0xFFFFFFFE;

    /// <summary>
    /// 去掉 DWM 画的 1px 窗口边框，只保留系统圆角。
    /// 悬浮卡片自己用 Border 画描边，若保留系统边框会出现「双层框」。
    /// </summary>
    public static void RemoveSystemWindowBorder(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var none = DwmColorNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(uint));
            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch { /* 旧版系统忽略 */ }
    }

    /// <summary>设置窗口 Mica 背景材质。</summary>
    public static void ApplyWindowBackdrop(Window window)
    {
        try
        {
            if (UiStyleService.IsMica)
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
            else
                window.SystemBackdrop = null;
        }
        catch
        {
            try { window.SystemBackdrop = null; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 外壳层（标题栏 + 侧栏 + 内容区背后）。Mica 下留一点透明度让材质透上来。
    /// 整个窗口只有这一层和内容层两种颜色，内容区圆角缺口露出的也是这一层。
    /// </summary>
    public static SolidColorBrush GetCodexShellBrush(ElementTheme theme, bool isMica)
    {
        var c = GetCodexSidebarBrush(theme).Color;
        return new SolidColorBrush(Color.FromArgb(isMica ? ShellMicaAlpha : (byte)255, c.R, c.G, c.B));
    }

    /// <summary>
    /// 子页面内容层：盖在外壳层之上的一层半透明黑（浅色主题是白），
    /// 所以内容区永远等于"外壳色再压暗一档" —— 色相一致，Mica 依旧透得上来。
    /// 不要改回写死的近黑色（#181818 / #171717），那会让内容区和侧栏彻底割裂；
    /// 要调深浅只改下面的 alpha。
    /// </summary>
    public static SolidColorBrush GetCodexContentBrush(ElementTheme theme, bool isMica)
    {
        _ = isMica;
        return new SolidColorBrush(theme == ElementTheme.Dark
            ? Color.FromArgb(ContentOverlayDarkAlpha, 0, 0, 0)
            : Color.FromArgb(ContentOverlayLightAlpha, 255, 255, 255));
    }

    /// <summary>外壳层不透明度：越小 Mica 越明显。</summary>
    private const byte ShellMicaAlpha = 235;

    /// <summary>深色下内容区压暗的强度（0-255，越大越深）。</summary>
    private const byte ContentOverlayDarkAlpha = 92;

    /// <summary>浅色下内容区提亮的强度。</summary>
    private const byte ContentOverlayLightAlpha = 92;


    public static SolidColorBrush GetCodexSidebarBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark
            ? Color.FromArgb(255, 28, 34, 48)
            : Color.FromArgb(255, 242, 243, 246));

    public static SolidColorBrush GetCodexSurfaceBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark
            ? Color.FromArgb(255, 36, 36, 36)
            : Color.FromArgb(255, 247, 247, 247));

    /// <summary>Mica 悬浮面板的半透明内容层，让系统材质可见但保持文字对比度。</summary>
    public static SolidColorBrush GetMicaPopupBrush(ElementTheme theme, bool isMica, bool elevated = false)
    {
        if (!isMica)
            return GetCodexSurfaceBrush(theme);

        return new SolidColorBrush(theme == ElementTheme.Dark
            ? (elevated ? Color.FromArgb(188, 38, 38, 38) : Color.FromArgb(154, 24, 24, 24))
            : (elevated ? Color.FromArgb(205, 255, 255, 255) : Color.FromArgb(174, 250, 250, 250)));
    }

    public static SolidColorBrush GetCodexBorderBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark
            ? Color.FromArgb(255, 56, 56, 56)
            : Color.FromArgb(255, 218, 218, 218));

    /// <summary>内容区描边（两种风格一致，均使用系统主题卡片描边）。</summary>
    public static Brush GetFrameBorderBrush() =>
        Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush
        ?? Application.Current.Resources["SystemControlForegroundBaseLowBrush"] as Brush
        ?? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

    /// <summary>内容区填充（两种风格一致，均使用系统主题卡片底色）。</summary>
    public static Brush GetContentFillBrush() =>
        Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush
        ?? new SolidColorBrush(Colors.Transparent);

    public static CornerRadius GetContentCornerRadius() => new(12);

    public static void ApplyBorderlessPopupChrome(Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var borderColor = ResolveBaseBackgroundColorRef(window);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));

            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch { /* ignore */ }
    }

    private static uint ResolveBaseBackgroundColorRef(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            var theme = root.RequestedTheme != ElementTheme.Default
                ? root.RequestedTheme
                : ThemeService.EffectiveTheme;
            root.RequestedTheme = theme;

            if (Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] is SolidColorBrush appBrush)
                return ToColorRef(appBrush.Color);
        }

        return ThemeService.EffectiveTheme == ElementTheme.Dark ? 0x00202020u : 0x00F3F3F3u;
    }

    private static uint ToColorRef(Color color) =>
        (uint)(color.R | (color.G << 8) | (color.B << 16));

    public static void ConfigureTransparentTitleBar(Window window, UIElement titleBarElement)
    {
        ApplyWindowBackdrop(window);
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBarElement);
        ApplyTransparentTitleBarColors(window);
    }

    /// <summary>弹出面板标题栏：与 <see cref="ConfigureTransparentTitleBar"/> 同一套窗口边框/Mica，仅隐藏系统标题栏按钮。</summary>
    public static void ConfigurePopupTitleBar(Window window, UIElement? titleBarElement = null)
    {
        ApplyWindowBackdrop(window);
        // 勿用 SetBorderAndTitleBar(false, false)：会去掉 DWM 圆角边框并露出系统白边。
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);

        // extend=true 让内容填满整个窗口（不为标题栏保留高度）；隐藏标题栏按钮即可无标题栏外观。
        window.ExtendsContentIntoTitleBar = true;
        if (titleBarElement != null)
            window.SetTitleBar(titleBarElement);
        ApplyTransparentTitleBarColors(window);
    }

    private static void ApplyTransparentTitleBarColors(Window window)
    {
        if (window.AppWindow.TitleBar is not { } tb) return;

        tb.ExtendsContentIntoTitleBar = true;
        // 48px「加高」标题栏，容纳应用标识与居中搜索框（与参考设置页一致）。
        try { tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall; } catch { /* 旧版系统忽略 */ }
        tb.BackgroundColor = Colors.Transparent;
        tb.InactiveBackgroundColor = Colors.Transparent;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = Colors.Transparent;
        tb.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    /// <summary>让系统最小化/最大化/关闭按钮融入应用自绘标题栏。</summary>
    public static void ApplyTitleBarButtonColors(Window window, ElementTheme theme)
    {
        if (window.AppWindow.TitleBar is not { } tb) return;

        var dark = theme == ElementTheme.Dark;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(140, 0, 0, 0);
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(255, 45, 45, 45)
            : Color.FromArgb(255, 226, 226, 226);
        tb.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(255, 58, 58, 58)
            : Color.FromArgb(255, 210, 210, 210);
    }

    public static void HookTitleBarPadding(
        Window window,
        FrameworkElement titleBar,
        ColumnDefinition leftPadding,
        ColumnDefinition rightPadding,
        bool syncHeight = false)
    {
        var debounce = window.DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(120);
        debounce.IsRepeating = false;
        debounce.Tick += (_, _) =>
            UpdateTitleBarPadding(window.AppWindow, titleBar, leftPadding, rightPadding, syncHeight);

        titleBar.Loaded += (_, _) =>
            UpdateTitleBarPadding(window.AppWindow, titleBar, leftPadding, rightPadding, syncHeight);

        titleBar.SizeChanged += (_, _) =>
        {
            debounce.Stop();
            debounce.Start();
        };
    }

    public static void UpdateTitleBarPadding(
        AppWindow appWindow,
        FrameworkElement titleBar,
        ColumnDefinition leftPadding,
        ColumnDefinition rightPadding,
        bool syncHeight = false)
    {
        try
        {
            var scale = titleBar.XamlRoot?.RasterizationScale ?? 1.0;
            var tb = appWindow.TitleBar;
            leftPadding.Width = new GridLength(tb.LeftInset / scale);
            rightPadding.Width = new GridLength(tb.RightInset / scale);
            if (syncHeight && tb.Height > 0)
                titleBar.Height = tb.Height / scale;
        }
        catch { /* ignore */ }
    }

    public static void EnforceMinSize(Window window, int minWidthDip, int minHeightDip)
    {
        window.AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange) return;
            try
            {
                var scale = (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
                var minW = (int)(minWidthDip * scale);
                var minH = (int)(minHeightDip * scale);
                var size = window.AppWindow.Size;
                if (size.Width < minW || size.Height < minH)
                {
                    window.AppWindow.Resize(new SizeInt32(
                        Math.Max(size.Width, minW),
                        Math.Max(size.Height, minH)));
                }
            }
            catch { /* ignore */ }
        };
    }

    public static void SizeToContent(Window window, int minWidth = 640, int minHeight = 380)
    {
        try
        {
            if (window.Content is not FrameworkElement root) return;
            root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var desiredW = (int)Math.Ceiling(root.DesiredSize.Width) + 24;
            var desiredH = (int)Math.Ceiling(root.DesiredSize.Height) + 48;
            var scale = root.XamlRoot?.RasterizationScale ?? 1.0;
            var w = (int)(Math.Max(minWidth, desiredW) * scale);
            var h = (int)(Math.Max(minHeight, desiredH) * scale);
            window.AppWindow.Resize(new SizeInt32(w, h));
        }
        catch { /* ignore */ }
    }

    public static void CenterOnScreen(Window window)
    {
        try
        {
            var display = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest);
            var work = display.WorkArea;
            var w = window.AppWindow.Size.Width;
            var h = window.AppWindow.Size.Height;
            var x = work.X + (work.Width - w) / 2;
            var y = work.Y + (work.Height - h) / 2;
            window.AppWindow.Move(new PointInt32(x, y));
        }
        catch { /* ignore */ }
    }

    public static void PlayShowAnimation(Window window, float offsetY = 16f, int durationMs = 250)
    {
        _ = window;
        _ = offsetY;
        _ = durationMs;
    }
}
