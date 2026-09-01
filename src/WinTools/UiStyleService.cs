using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace WinTools;

/// <summary>统一管理 Mica 界面材质，对各窗口应用背景并刷新壳层样式。</summary>
public static class UiStyleService
{
    private static readonly HashSet<Window> Windows = new();
    private static readonly HashSet<IUiStyleShell> Shells = new();
    private static string _preference = UiStylePreference.Mica;

    public static string Preference => _preference;

    public static bool IsMica => UiStylePreference.IsMica(_preference);

    public static event EventHandler? StyleChanged;

    public static void Initialize(string? preference)
    {
        _preference = UiStylePreference.Normalize(preference);
        ApplyToAllWindows();
    }

    public static void SetPreference(string? preference)
    {
        _preference = UiStylePreference.Normalize(preference);
        ApplyToAllWindows();
        StyleChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Register(Window window)
    {
        if (!Windows.Add(window)) return;
        if (window is IUiStyleShell shell)
            Shells.Add(shell);
        ApplyToWindow(window);
    }

    public static void Unregister(Window window)
    {
        Windows.Remove(window);
        if (window is IUiStyleShell shell)
            Shells.Remove(shell);
    }

    private static void ApplyToAllWindows()
    {
        foreach (var window in Windows)
            ApplyToWindow(window);
        foreach (var shell in Shells)
            shell.ApplyUiStyleSurfaces();
    }

    private static void ApplyToWindow(Window window) =>
        WindowHelper.ApplyWindowBackdrop(window);
}

/// <summary>可在风格/主题变化时刷新窗口壳层背景的窗口。</summary>
public interface IUiStyleShell
{
    void ApplyUiStyleSurfaces();
}
