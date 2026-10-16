using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinTools.Services;

internal sealed record PreviewTable(string Name, IReadOnlyList<string[]> Rows, bool Truncated = false);
internal sealed record PreviewFolderStats(long Bytes, long Directories, long Files);
internal sealed record PreviewEntry(string Name, string Path, bool IsDirectory, long? Bytes)
{
    public string Glyph => IsDirectory ? "\uE8B7" : "\uE8A5";
    public string Size => IsDirectory ? "文件夹" : Bytes is { } size ? PreviewDataReader.FormatSize(size) : "";
}

internal static class PreviewDataReader
{
    internal static PreviewFolderStats ReadFolderStats(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
            ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        long bytes = 0, directories = 0, files = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo) directories++;
            else if (entry is FileInfo file) { files++; bytes += file.Length; }
        }
        return new PreviewFolderStats(bytes, directories, files);
    }
    internal const int MaxRows = 1000;
    internal const int MaxColumns = 64;
    internal const int MaxEntries = 2000;
    private const int MaxChars = 256 * 1024;

    internal static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024
        ? $"{bytes / 1024d:0.#} KB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MB" : $"{bytes / (1024d * 1024 * 1024):0.#} GB";

    // Parse records, not physical lines: quoted newlines and escaped quotes belong to the same cell.
    internal static PreviewTable ReadDelimited(string path)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars + 1];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var cut = count > MaxChars;
        var text = new string(buffer, 0, Math.Min(count, MaxChars));
        var delimiter = path.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(text);
        return ParseDelimited(text, delimiter, cut);
    }

    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [','] = 0, [';'] = 0, ['\t'] = 0, ['|'] = 0 };
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') i++; else quoted = !quoted; }
            else if (!quoted && (c == '\n' || c == '\r')) break;
            else if (!quoted && counts.ContainsKey(c)) counts[c]++;
        }
        return counts.OrderByDescending(p => p.Value).First().Key;
    }

    internal static PreviewTable ParseDelimited(string text, char delimiter, bool inputTruncated = false)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var truncated = inputTruncated;
        void EndCell()
        {
            if (cells.Count < MaxColumns) cells.Add(cell.ToString()); else truncated = true;
            cell.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && (quoted || cell.Length == 0))
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (!quoted && c == delimiter) EndCell();
            else if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndCell(); rows.Add(cells.ToArray()); cells.Clear();
                if (rows.Count >= MaxRows) { truncated |= i + 1 < text.Length; break; }
            }
            else cell.Append(c);
        }
        if (rows.Count < MaxRows && (cell.Length > 0 || cells.Count > 0))
        {
            // Do not present a partial final record as a complete record when reading a bounded prefix.
            if (!inputTruncated) { EndCell(); rows.Add(cells.ToArray()); }
        }
        if (quoted && !inputTruncated) throw new FormatException("CSV 引号没有闭合，可切换到原文查看。");
        return new PreviewTable("数据", rows, truncated);
    }

    internal static IReadOnlyList<PreviewEntry> ReadZip(string path, CancellationToken cancellation)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = new Dictionary<string, PreviewEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            // Display only; no archive entry is ever extracted or executed.
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var prefix = "";
            for (var i = 0; i < parts.Length && entries.Count < MaxEntries; i++)
            {
                var directory = i < parts.Length - 1 || name.EndsWith('/');
                var full = prefix + parts[i] + (directory ? "/" : "");
                entries.TryAdd(full, new PreviewEntry(parts[i], full, directory, directory ? null : entry.Length));
                prefix = full;
            }
            if (entries.Count >= MaxEntries) break;
        }
        return entries.Values.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<PreviewEntry> ReadFolder(string path, CancellationToken cancellation)
    {
        var entries = new List<PreviewEntry>();
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new PreviewEntry(info.Name, info.FullName, directory, info is FileInfo file ? file.Length : null));
            if (entries.Count >= MaxEntries) break;
        }
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
