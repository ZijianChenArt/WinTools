namespace WinTools;

/// <summary>界面统一使用 Mica；保留旧配置值的兼容入口。</summary>
public static class UiStylePreference
{
    public const string Mica = "mica";

    public static string Normalize(string? value) => Mica;

    public static bool IsMica(string? value) => true;
}
