using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace WinTools.Services;

/// <summary>为常见数据文件和压缩包生成适合在预览窗口阅读的文本。</summary>
internal static class StructuredPreviewExtractor
{
    private const int MaxBytes = 512 * 1024;
    private const int MaxLines = 10_000;
    private const int MaxArchiveEntries = 2_000;

    internal static string? Extract(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
                return ExtractDelimited(path, extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : null);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return ExtractJson(path);
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return ExtractXml(path);
            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return ExtractZip(path);
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
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        return document.ToString(SaveOptions.DisableFormatting) == text
            ? document.ToString(SaveOptions.None)
            : document.ToString(SaveOptions.None);
    }

    private static string? ExtractDelimited(string path, char? forcedDelimiter)
    {
        var text = ReadLimitedText(path);
        if (text == null) return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var delimiter = forcedDelimiter ?? DetectDelimiter(lines.FirstOrDefault() ?? "");
        var builder = new StringBuilder();
        var count = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0 && count == 0) continue;
            builder.Append(string.Join('\t', ParseDelimitedLine(line, delimiter))).Append('\n');
            if (++count >= MaxLines) break;
        }

        if (lines.Length > count) builder.Append("… 行数过多，仅显示前一部分\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static char DetectDelimiter(string header)
    {
        var candidates = new[] { ',', ';', '\t', '|' };
        return candidates.OrderByDescending(c => ParseDelimitedLine(header, c).Count).First();
    }

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == delimiter && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else value.Append(ch);
        }
        values.Add(value.ToString());
        return values;
    }

    private static string? ExtractZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var builder = new StringBuilder();
        builder.Append("压缩包内容（").Append(archive.Entries.Count).Append(" 项）\n\n");
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).Take(MaxArchiveEntries))
        {
            var isDirectory = entry.FullName.EndsWith('/') || entry.Length == 0 && string.IsNullOrEmpty(entry.Name);
            builder.Append(isDirectory ? "📁 " : "📄 ")
                .Append(entry.FullName)
                .Append(isDirectory ? "" : $"   {FormatSize(entry.Length)}")
                .Append('\n');
        }
        if (archive.Entries.Count > MaxArchiveEntries)
            builder.Append("… 项目过多，仅显示前 ").Append(MaxArchiveEntries).Append(" 项\n");
        return builder.ToString().TrimEnd('\n');
    }

    private static string? ReadLimitedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) return null;
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
    }
}
