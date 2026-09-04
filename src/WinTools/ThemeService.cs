using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace WinTools;

/// <summary>应用主题偏好：浅色、深色、跟随系统。</summary>
public static class ThemePreference
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? value) => value switch
    {
        Light => Light,
        Dark => Dark,
        _ => System
    };
}

/// <summary>
/// 统一管理应用主题：对各窗口根元素设置 RequestedTheme。
/// 不可在窗口 XAML 加载前设置 Application.RequestedTheme（WinUI 3 会抛异常）。
/// </summary>
public static class ThemeService
{
    private static readonly HashSet<Window> Windows = new();
    private static UISettings? _uiSettings;
    private static string _preference = ThemePreference.System;

    /// <summary>实际生效的主题（跟随系统时已解析为 Light/Dark）。</summary>
    public static ElementTheme EffectiveTheme => ResolveEffective(_preference);

    /// <summary>实际主题变化时触发（含跟随系统时系统切换）。</summary>
    public static event EventHandler? EffectiveThemeChanged;

    public static void Initialize(string? preference)
    {
        _preference = ThemePreference.Normalize(preference);
        EnsureSystemWatcher();
        ApplyToAllWindows();
    }

    public static void SetPreference(string? preference)
    {
        _preference = ThemePreference.Normalize(preference);
        ApplyToAllWindows();
        EffectiveThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void Register(Window window)
    {
        if (!Windows.Add(window)) return;
        ApplyToWindow(window);
    }

    public static void Unregister(Window window) => Windows.Remove(window);

    private static void ApplyToAllWindows()
    {
        foreach (var window in Windows)
            ApplyToWindow(window);
    }

    private static void ApplyToWindow(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        root.RequestedTheme = EffectiveTheme;

        // RequestedTheme 只会替换 ThemeResource；桌面卡片的 Mica Tint 与半透明表面
        // 是代码生成的画刷，必须显式重算，否则从深色切到浅色仍会保留深色卡片。
        if (window is IUiStyleShell shell)
            shell.ApplyUiStyleSurfaces();
    }

    private static ElementTheme ResolveEffective(string preference) => preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => IsSystemDarkTheme() ? ElementTheme.Dark : ElementTheme.Light
    };

    private static bool IsSystemDarkTheme()
    {
        try
        {
            var bg = new UISettings().GetColorValue(UIColorType.Background);
            return bg.R + bg.G + bg.B < 384;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSystemWatcher()
    {
        _uiSettings ??= new UISettings();
        _uiSettings.ColorValuesChanged -= OnSystemColorsChanged;
        _uiSettings.ColorValuesChanged += OnSystemColorsChanged;
    }

    private static void OnSystemColorsChanged(UISettings sender, object args)
    {
        if (_preference != ThemePreference.System) return;
        ApplyToAllWindows();
        EffectiveThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}

