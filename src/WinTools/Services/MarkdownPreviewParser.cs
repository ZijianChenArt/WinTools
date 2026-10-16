using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WinTools.Services;

internal sealed record MarkdownPreviewBlock(string Kind, string Text, int Level = 0);

/// <summary>A bounded, native reading preview. Unsupported Markdown remains readable text; HTML is never executed.</summary>
internal static class MarkdownPreviewParser
{
    internal const int MaxBlocks = 600;
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex List = new(@"^(\s*)([-+*]|\d+[.)])\s+(.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TableRule = new(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    internal static IReadOnlyList<MarkdownPreviewBlock> Parse(string source)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<MarkdownPreviewBlock>();
        var paragraph = new StringBuilder();
        var hardBreak = false;
        void Flush() { if (paragraph.Length > 0) { result.Add(new("paragraph", paragraph.ToString())); paragraph.Clear(); } hardBreak = false; }
        for (var i = 0; i < lines.Length; i++)
        {
            if (result.Count >= MaxBlocks - 1) { Flush(); result.Add(new("paragraph", "… 内容较长，请切换到源码查看剩余内容。")); break; }
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                Flush(); var fenceChar = trimmed[0]; var fenceLength = 0;
                while (fenceLength < trimmed.Length && trimmed[fenceLength] == fenceChar) fenceLength++;
                var code = new StringBuilder();
                for (++i; i < lines.Length; i++)
                {
                    var end = lines[i].Trim();
                    if (end.Length >= fenceLength && end.Trim(fenceChar).Length == 0) break;
                    if (code.Length > 0) code.Append('\n'); code.Append(lines[i]);
                }
                result.Add(new("code", code.ToString())); continue;
            }
            if (trimmed.Length == 0) { Flush(); continue; }
            var heading = Heading.Match(line);
            if (heading.Success) { Flush(); result.Add(new("heading", heading.Groups[2].Value, heading.Groups[1].Length)); continue; }
            if (i + 1 < lines.Length && line.Contains('|') && TableRule.IsMatch(lines[i + 1]))
            {
                Flush(); var table = new StringBuilder(line); i++;
                while (i + 1 < lines.Length && lines[i + 1].Contains('|') && lines[i + 1].Trim().Length > 0)
                    table.Append('\n').Append(lines[++i]);
                result.Add(new("table", table.ToString())); continue;
            }
            if (i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\s*(={3,}|-{3,})\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
            { Flush(); result.Add(new("heading", line, lines[++i].TrimStart()[0] == '=' ? 1 : 2)); continue; }
            if (trimmed is "---" or "***" or "___") { Flush(); result.Add(new("rule", "")); continue; }
            if (trimmed.StartsWith('>')) { Flush(); result.Add(new("quote", trimmed.TrimStart('>', ' '))); continue; }
            var list = List.Match(line);
            if (list.Success)
            {
                Flush(); var marker = list.Groups[2].Value;
                var text = list.Groups[3].Value;
                var prefix = char.IsDigit(marker[0]) ? marker + " " : "• ";
                if (text.StartsWith("[ ] ")) { prefix = "☐ "; text = text[4..]; }
                else if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) { prefix = "☑ "; text = text[4..]; }
                result.Add(new("list", prefix + text, Math.Min(4, list.Groups[1].Length / 2))); continue;
            }
            if (paragraph.Length > 0) paragraph.Append(hardBreak ? '\n' : ' ');
            paragraph.Append(line.TrimEnd());
            hardBreak = line.EndsWith("  ");
        }
        Flush();
        return result;
    }
}
