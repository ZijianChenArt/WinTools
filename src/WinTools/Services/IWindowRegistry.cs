using Microsoft.UI.Xaml;

namespace WinTools.Services;

/// <summary>
/// 抽象"全程序可见 WinUI 窗口"的注册入口。
/// 实现方把窗口登记到自己的子系统（悬浮暂存、per-app IME 排除列表等），
/// 调用方只通过 <see cref="Register"/> 注册窗口，不再依赖具体 <c>App</c> 类型。
/// <para>
/// 历史背景：早期 <c>MainWindow</c> / <c>DesktopCardManager</c> 直接
/// <c>(App.Current as App)?.RegisterWinToolsWindow(w)</c>，导致
/// <c>MainWindow</c> 跟 <c>App</c> 类型双向耦合、无法脱离 <c>App</c> 单测。
/// 本接口是 2026-08-31 解耦的过渡产物。
/// </para>
/// </summary>
internal interface IWindowRegistry
{
    /// <summary>把窗口登记到所有需要感知它的子系统。</summary>
    void Register(Window window);
}

/// <summary>
/// <see cref="IWindowRegistry"/> 的空实现：仅在 <c>App.Current</c> 尚未就绪、
/// 或处于无 App 上下文（设计器、未来的单测）的场景下使用，
/// 保证调用方拿到 <see cref="IWindowRegistry"/> 后可以无条件调用 <c>Register</c>。
/// </summary>
internal sealed class NullWindowRegistry : IWindowRegistry
{
    public static readonly NullWindowRegistry Instance = new();
    private NullWindowRegistry() { }
    public void Register(Window window) { /* no-op */ }
}
