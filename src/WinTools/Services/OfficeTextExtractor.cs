using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace WinTools.Services;

/// <summary>
/// 不依赖 Office，直接从 docx / xlsx 的 zip 包里读出文字和表格，用于文件预览。
/// docx 是 <c>word/document.xml</c>，xlsx 是 <c>xl/worksheets/*.xml</c> 加共享字符串表。
/// 只提取正文文字与单元格内容，不还原样式、图片和公式结果。
/// </summary>
internal static class OfficeTextExtractor
{
    /// <summary>输出超过这个长度就截断，避免一个大表格把文本框撑爆。</summary>
    private const int MaxChars = 200_000;

    /// <summary>xlsx 每个工作表最多读取的行数。</summary>
    private const int MaxRowsPerSheet = 5_000;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>提取文本；不是支持的格式或解析失败时返回 null。</summary>
    public static string? Extract(string path)
    {
        var extension = Path.GetExtension(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase)) return ExtractDocx(zip);
            if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase)) return ExtractXlsx(zip);
            return null;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"OfficeTextExtractor({path})", ex);
            return null;
        }
    }

    #region docx

    private static string? ExtractDocx(ZipArchive zip)
    {
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return null;

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        var body = document.Root?.Element(W + "body");
        if (body == null) return null;

        // 按文档顺序遍历：段落换行、表格单元格用制表符分隔，文字、制表位、换行符各自对应。
        var builder = new StringBuilder();
        foreach (var element in body.Descendants())
        {
            if (element.Name == W + "t")
            {
                builder.Append(element.Value);
            }
            else if (element.Name == W + "tab")
            {
                builder.Append('\t');
            }
            else if (element.Name == W + "br")
            {
                builder.Append('\n');
            }
            else if (element.Name == W + "tc")
            {
                builder.Append('\t');
            }
            else if (element.Name == W + "p" && !element.Ancestors(W + "tc").Any())
            {
                builder.Append('\n');
            }

            if (builder.Length > MaxChars)
            {
                builder.Append("\n\n… 内容过长，仅显示前一部分");
                break;
            }
        }

        return builder.ToString().Trim('\n');
    }

    #endregion

    #region xlsx

    private static string? ExtractXlsx(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml");
        if (workbookEntry == null) return null;

        var sharedStrings = ReadSharedStrings(zip);
        var relationships = ReadRelationships(zip, "xl/_rels/workbook.xml.rels");

        using var workbookStream = workbookEntry.Open();
        var workbook = XDocument.Load(workbookStream);

        var builder = new StringBuilder();
        foreach (var sheet in workbook.Descendants(S + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "工作表";
            var relationId = (string?)sheet.Attribute(OfficeRel + "id");
            if (relationId == null || !relationships.TryGetValue(relationId, out var target)) continue;

            var sheetEntry = zip.GetEntry(ResolvePartPath("xl/", target));
            if (sheetEntry == null) continue;

            builder.Append('【').Append(name).Append("】\n");
            AppendSheet(sheetEntry, sharedStrings, builder);
            builder.Append('\n');

            if (builder.Length > MaxChars)
            {
                builder.Append("… 内容过长，仅显示前一部分");
                break;
            }
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>把一个工作表的单元格按行写出，列之间用制表符分隔，列号以 A、B… 的引用定位，空缺的列补空。</summary>
    private static void AppendSheet(ZipArchiveEntry entry, IReadOnlyList<string> sharedStrings, StringBuilder builder)
    {
        using var stream = entry.Open();
        var sheet = XDocument.Load(stream);

        var rowCount = 0;
        foreach (var row in sheet.Descendants(S + "row"))
        {
            if (++rowCount > MaxRowsPerSheet)
            {
                builder.Append("… 行数过多，仅显示前一部分\n");
                return;
            }

            var cells = new List<string>();
            foreach (var cell in row.Elements(S + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r"));
                while (cells.Count < column) cells.Add("");
                cells.Add(CellText(cell, sharedStrings));
            }

            builder.Append(string.Join('\t', cells)).Append('\n');
            if (builder.Length > MaxChars) return;
        }
    }

    private static string CellText(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(S + "t").Select(t => t.Value));

        var value = cell.Element(S + "v")?.Value ?? "";
        if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count)
            return sharedStrings[index];
        return value;
    }

    /// <summary>A1 → 0，B3 → 1，AA1 → 26；没有引用时返回 0。</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        var column = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            column = column * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return column - 1;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null) return result;

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        foreach (var item in document.Descendants(S + "si"))
            result.Add(string.Concat(item.Descendants(S + "t").Select(t => t.Value)));
        return result;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip, string partPath)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = zip.GetEntry(partPath);
        if (entry == null) return result;

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        foreach (var relationship in document.Descendants(PackageRel + "Relationship"))
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id != null && target != null) result[id] = target;
        }
        return result;
    }

    /// <summary>关系里的目标可能是相对路径（worksheets/sheet1.xml）或包内绝对路径（/xl/worksheets/sheet1.xml）。</summary>
    private static string ResolvePartPath(string basePath, string target) =>
        target.StartsWith('/') ? target.TrimStart('/') : basePath + target;

    #endregion
}
