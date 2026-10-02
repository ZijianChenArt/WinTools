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
    /// <summary>任务栏弹出窗口（暂存、音频设备）下沿到任务栏上沿的间距，统一取 Win11 弹出面板的 12 DIP。</summary>
    internal const int TaskbarPopupGapDip = 12;

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
        => WhenRendered(window, () => { SetWindowCloak(window, false); completed?.Invoke(); });

    internal static void WhenRendered(Window window, Action? completed)
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

    /// <summary>让 DWM 圆角边缘使用与窗口表面一致的颜色，避免透明边框在圆角端点产生亮点。</summary>
    public static void MatchSystemWindowBorder(Window window, Color surfaceColor)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var color = ToColorRef(surfaceColor);
            _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(uint));
            var round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        }
        catch { /* 旧版系统忽略 */ }
    }

    private sealed class MicaState
    {
        public MicaController? Controller;
        public SystemBackdropConfiguration Configuration = new();
        public bool Attached;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, MicaState> PersistentMicaState = new();

    /// <summary>
    /// 让窗口失焦后仍保持 Mica（见 README 7.4）。任务栏弹窗点开后焦点常被任务栏抢回，
    /// 系统默认的 <c>MicaBackdrop</c> 会在失焦时退成不透明灰；改用显式 <c>MicaController</c> + 恒 <c>IsInputActive</c>。
    /// 必须在窗口构造阶段、第一次 <see cref="ApplyWindowBackdrop"/> 之前调用；此后
    /// <see cref="ApplyWindowBackdrop"/> 对该窗口走显式控制器，不要再给它设 <c>SystemBackdrop</c>。
    /// </summary>
    internal static void EnablePersistentMica(Window window)
    {
        PersistentMicaState.GetValue(window, _ => new MicaState());
        window.Closed += (_, _) =>
        {
            if (!PersistentMicaState.TryGetValue(window, out var state)) return;
            try { state.Controller?.RemoveAllSystemBackdropTargets(); state.Controller?.Dispose(); } catch { /* ignore */ }
            state.Controller = null;
            state.Attached = false;
        };
    }

    private static void ApplyPersistentMica(Window window, MicaState state)
    {
        if (!UiStyleService.IsMica || !MicaController.IsSupported())
        {
            // 普通风格：拆掉控制器，回到无背景（表面由不透明画刷负责）。
            if (state.Controller != null)
            {
                try { state.Controller.RemoveAllSystemBackdropTargets(); state.Controller.Dispose(); } catch { /* ignore */ }
                state.Controller = null;
                state.Attached = false;
            }
            window.SystemBackdrop = null;
            return;
        }
        if (state.Controller == null)
        {
            // 只在首次接管合成目标前清掉普通 SystemBackdrop；连接后再清会让目标失效。
            window.SystemBackdrop = null;
            state.Controller = new MicaController { Kind = MicaKind.Base };
            state.Attached = state.Controller.AddSystemBackdropTarget(
                WinRT.CastExtensions.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>(window));
            if (state.Attached) state.Controller.SetSystemBackdropConfiguration(state.Configuration);
        }
        if (!state.Attached) return;
        state.Configuration.IsInputActive = true;
        state.Configuration.Theme = ThemeService.EffectiveTheme == ElementTheme.Dark
            ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    /// <summary>设置窗口 Mica 背景材质。</summary>
    public static void ApplyWindowBackdrop(Window window)
    {
        try
        {
            if (PersistentMicaState.TryGetValue(window, out var state))
            {
                ApplyPersistentMica(window, state);
                return;
            }
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
    /// <remarks>
    /// Mica 风格下**完全不铺色**，和 Windows 设置一致：标题栏、导航栏、内容区背景都是纯 Mica，
    /// 明暗层次全部交给卡片（`CardBackgroundFillColorDefaultBrush`）。
    /// 2026-09-06 取样 Windows 设置窗口反推：它的导航栏就是纯 Mica，卡片是白色 alpha≈13/255
    /// （即标准 `CardBackgroundFillColorDefault` 的 #0D）。本程序原来在这里铺 alpha 235，
    /// Mica 只剩 8%，把窗口从屏幕最左移到最右采样只差 1 个色阶——等于没有材质，换壁纸自然看不出变化。
    /// 要调深浅请改卡片层，不要在壳层重新铺不透明色。
    /// </remarks>
    public static SolidColorBrush GetCodexShellBrush(ElementTheme theme, bool isMica)
    {
        if (isMica) return new SolidColorBrush(Colors.Transparent);
        var c = GetCodexSidebarBrush(theme).Color;
        return new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
    }

    /// <summary>
    /// 子页面内容区背景：叠在 Mica 之上的一层半透明黑（浅色主题是白），让内容区比
    /// 标题栏 / 侧栏深一档。外壳层已按 Windows 标准完全不铺色（见
    /// <see cref="GetCodexShellBrush"/>），所以这里是**唯一**一层遮罩，Mica 仍然透得上来。
    /// 调深浅只改 <see cref="ContentDepthPercent"/>，不要在外壳层重新铺色。
    /// </summary>
    public static SolidColorBrush GetCodexContentBrush(ElementTheme theme, bool isMica)
    {
        if (!isMica) return GetCodexSurfaceBrush(theme);
        return new SolidColorBrush(theme == ElementTheme.Dark
            ? Color.FromArgb(ContentDepthAlpha(32, 150), 0, 0, 0)
            : Color.FromArgb(ContentDepthAlpha(45, 220), 255, 255, 255));
    }

    /// <summary>内容层深度百分比：0 = 完全透出 Mica，100 = 几乎不透明。</summary>
    /// <remarks>深色下 0→alpha 32、100→alpha 150，当前 50 即 alpha 91（Mica 仍透约 64%）。</remarks>
    private const int ContentDepthPercent = 50;

    private static byte ContentDepthAlpha(int shallowAlpha, int deepAlpha)
    {
        var t = ContentDepthPercent / 100d;
        return (byte)Math.Clamp((int)Math.Round(shallowAlpha + (deepAlpha - shallowAlpha) * t), 0, 255);
    }


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
            ? (elevated ? Color.FromArgb(120, 38, 38, 38) : Color.FromArgb(56, 24, 24, 24))
            : (elevated ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(90, 250, 250, 250)));
    }

    /// <summary>Mica 弹窗里的内容面板：与标准 <c>CardBackgroundFillColorDefault</c> 一致的极淡底色，让 Mica 透出来。</summary>
    public static SolidColorBrush GetMicaPanelBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark ? Color.FromArgb(13, 255, 255, 255) : Color.FromArgb(128, 255, 255, 255));

    public static SolidColorBrush GetDesktopCardBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark
            ? Color.FromArgb(90, 30, 31, 34)
            : Color.FromArgb(225, 255, 255, 255));

    public static SolidColorBrush GetCodexBorderBrush(ElementTheme theme) =>
        new(theme == ElementTheme.Dark
            ? Color.FromArgb(255, 56, 56, 56)
            : Color.FromArgb(255, 218, 218, 218));


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

    /// <summary>完全没有标题栏的窗口（桌面分区卡片）的窗口外观：隐藏标题栏，只留 DWM 圆角边框。</summary>
    /// <remarks>
    /// 不要图省事改回 <see cref="ConfigurePopupTitleBar"/>：那条路径上的三件事对卡片都是纯开销——
    /// <c>ApplyWindowBackdrop</c> 建一个系统背景控制器，随后又被卡片自己的显式 <c>MicaController</c>
    /// 拆掉（还违反 README 7.4）；<c>ExtendsContentIntoTitleBar</c> 与标题栏配色是给带自定义标题栏
    /// 的窗口用的，卡片连标题栏元素都没有。2026-09-06 实测：这三步每张卡片 87ms，9 张 = 0.78 秒启动时间。
    /// </remarks>
    public static void ConfigureChromelessWindow(Window window)
    {
        // 同样不能用 SetBorderAndTitleBar(false, false)，理由见上面那条注释。
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(true, false);
    }

    private static void ApplyTransparentTitleBarColors(Window window)
    {
        if (window.AppWindow.TitleBar is not { } tb) return;

        tb.ExtendsContentIntoTitleBar = true;
        // 标准 32px 标题栏（`Tall` 是 48px，显得过厚）。主窗口的 XAML 标题栏高度由
        // HookTitleBarPadding(syncHeight: true) 跟着这个值走，不要在 XAML 里另写死高度。
        try { tb.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard; } catch { /* 旧版系统忽略 */ }
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
        // 悬停 / 按下必须用**半透明**填充，值取自系统标准的 SubtleFillColorSecondary /
        // Tertiary。原来写的是不透明灰（深色 #2D2D2D），在 Mica 标题栏上就是一块突兀的
        // 灰方块——外壳层还铺着 alpha 235 时看不出来，改成纯 Mica 后一眼就能看见。
        tb.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(15, 255, 255, 255)
            : Color.FromArgb(9, 0, 0, 0);
        tb.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(10, 255, 255, 255)
            : Color.FromArgb(6, 0, 0, 0);
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

    private static void UpdateTitleBarPadding(
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

}
