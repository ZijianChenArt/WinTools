using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace WinTools.Services;

/// <summary>格式化可在预览窗口中阅读的 JSON 和 XML。</summary>
internal static class StructuredPreviewExtractor
{
    private const int MaxBytes = 512 * 1024;

    internal static string? Extract(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return ExtractJson(path);
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return ExtractXml(path);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"StructuredPreviewExtractor({path})", ex);
        }

        return null;
    }

    private static string? ExtractJson(string path)
    {
        var text = ReadLimitedText(path);
        if (text == null) return null;
        using var document = JsonDocument.Parse(text);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string? ExtractXml(string path)
    {
        var text = ReadLimitedText(path);
        if (text == null) return null;
        var document = XDocument.Parse(text);
        return document.ToString(SaveOptions.None);
    }

    private static string? ReadLimitedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }

}
