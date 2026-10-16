using System.IO.Compression;
using WinTools.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var csv = PreviewDataReader.ParseDelimited("name,note\r\nalpha,\"one\r\ntwo,\"\"quoted\"\"\"\r\nbeta,\r\n", ',');
Require(csv.Rows.Count == 3 && csv.Rows[1][1] == "one\r\ntwo,\"quoted\"" && csv.Rows[2][1] == "", "Quoted CSV records or trailing empty cells lost");
var incomplete = PreviewDataReader.ParseDelimited("a,b\n\"unfinished", ',', true);
Require(incomplete.Truncated && incomplete.Rows.Count == 1, "Truncated record presented as complete");
try { PreviewDataReader.ParseDelimited("a,\"broken", ','); throw new Exception("Invalid CSV accepted"); }
catch (FormatException) { }
var many = PreviewDataReader.ParseDelimited(string.Join('\n', Enumerable.Range(0, 2000)), ',');
Require(many.Rows.Count == 1000 && many.Truncated, "Row budget not enforced");
var blocks = MarkdownPreviewParser.Parse("# Title\n\nParagraph with **bold**\nwrap here\n\n```cs\n# not a heading\n```\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n- [x] done\n> quote\n");
Require(blocks.Count == 6 && blocks[0].Kind == "heading" && blocks[1].Text.Contains("wrap here")
    && blocks[2].Kind == "code" && blocks[2].Text == "# not a heading" && blocks[3].Kind == "table"
    && blocks[4].Text.StartsWith("☑"), "Markdown block parsing failed");
Require(MarkdownPreviewParser.Parse("Heading\n===")[0].Kind == "heading", "Setext heading failed");
Require(MarkdownPreviewParser.Parse("first  \nsecond")[0].Text == "first\nsecond", "Markdown hard break lost");
foreach (var work in new[] { (360d, 240d), (1536d, 824d), (2560d, 1400d) })
{
    var fit = PreviewLayoutPolicy.Fit(1000, 2000, work.Item1, work.Item2);
    Require(fit.Width > 0 && fit.Height > 0 && fit.Width < work.Item1 && fit.Height < work.Item2, "Preview exceeds work area");
}
Require(PreviewLayoutPolicy.ListHeight(3) < 340 && PreviewLayoutPolicy.ListHeight(2000) < 620, "Small ZIP or long list height wrong");
var root = Path.Combine(Path.GetTempPath(), "WinTools-preview-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var zipPath = Path.Combine(root, "sample.zip");
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("docs/sub/readme.txt").Open()); writer.Write("hello");
    }
    var entries = PreviewDataReader.ReadZip(zipPath, CancellationToken.None);
    Require(entries.Count == 3 && entries.Count(e => e.IsDirectory) == 2 && entries.Single(e => !e.IsDirectory).Bytes == 5, "Implicit ZIP directories lost");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { PreviewDataReader.ReadZip(zipPath, cancellation.Token); throw new Exception("ZIP cancellation ignored"); } catch (OperationCanceledException) { }
    var folderStats = PreviewDataReader.ReadFolderStats(root, CancellationToken.None);
    Require(folderStats.Files == 1 && folderStats.Directories == 0 && folderStats.Bytes == new FileInfo(zipPath).Length, "Folder summary changed");
    try { PreviewDataReader.ReadFolderStats(root, cancellation.Token); throw new Exception("Folder scan cancellation ignored"); } catch (OperationCanceledException) { }
    var xlsx = Path.Combine(root, "book.xlsx");
    using (var zip = ZipFile.Open(xlsx, ZipArchiveMode.Create))
    {
        void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Add("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Sheet A' r:id='s1'/><sheet name='Empty' r:id='s2'/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='s1' Target='worksheets/sheet1.xml'/><Relationship Id='s2' Target='/xl/worksheets/sheet2.xml'/></Relationships>");
        Add("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row><c r='A1' t='inlineStr'><is><t>left</t></is></c><c r='C1'><v>42</v></c><c r='XFD1'><v>ignored</v></c></row></sheetData></worksheet>");
        Add("xl/worksheets/sheet2.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData/></worksheet>");
    }
    var workbook = OfficeTextExtractor.ReadWorkbook(xlsx, CancellationToken.None);
    Require(workbook.Count == 2 && workbook[0].Rows[0].SequenceEqual(new[] { "left", "", "42" }) && workbook[0].Truncated
        && workbook[1].Rows.Count == 0, "Workbook sheets, sparse cells, or column budget failed");
    var pptx = Path.Combine(root, "slides.pptx");
    var docx = Path.Combine(root, "sample.docx");
    using (var zip = ZipFile.Open(docx, ZipArchiveMode.Create))
    {
        using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
        writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Heading</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r><w:r><w:t> B</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:r><w:t>Foot</w:t></w:r></w:p></w:body></w:document>");
    }
    Require(OfficeTextExtractor.Extract(docx) == "Heading\nA B\tC\nFoot", "Word paragraph or table row order lost");
    using (var zip = ZipFile.Open(pptx, ZipArchiveMode.Create))
    {
        void Add(string name, string text) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(text); }
        Add("ppt/presentation.xml", "<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId r:id='b'/><p:sldId r:id='a'/></p:sldIdLst></p:presentation>");
        Add("ppt/_rels/presentation.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='a' Target='slides/slide1.xml'/><Relationship Id='b' Target='slides/slide2.xml'/></Relationships>");
        Add("ppt/slides/slide1.xml", "<s xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:t>First XML</a:t></s>");
        Add("ppt/slides/slide2.xml", "<s xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:t>Second XML</a:t></s>");
    }
    var outline = OfficeTextExtractor.Extract(pptx)!;
    Require(outline.IndexOf("Second XML", StringComparison.Ordinal) < outline.IndexOf("First XML", StringComparison.Ordinal), "PPTX presentation ordering ignored");
    if (args.Length == 1)
    {
        var destination = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(root)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        File.WriteAllText(Path.Combine(destination, "sample.md"), "# 文件预览规范\n\n这是一段需要**自动换行**的说明文字。" + string.Concat(Enumerable.Repeat("Markdown 应当以阅读方式显示，长段落不应该撑宽整个窗口。", 8)) + "\n\n## 支持的内容\n\n- [x] 段落与列表\n- 引用与代码\n\n> 这是引用。\n\n```csharp\nvar value = \"" + new string('x', 180) + "\";\n```\n\n| 文件 | 方式 |\n| --- | --- |\n| MD | 阅读 |\n| ZIP | 列表 |\n\n[链接](https://example.com) 和 ![示意图](missing.png)");
        File.WriteAllText(Path.Combine(destination, "sample.txt"), "普通文件说明\n\n" + string.Concat(Enumerable.Repeat("长行文字应当在窗口中自动换行。", 40)));
        File.WriteAllText(Path.Combine(destination, "empty.txt"), "");
        File.WriteAllText(Path.Combine(destination, "data.csv"), "name,note,score\nalpha,\"first\nsecond, quoted\",42\nbeta,\"a \"\"quote\"\"\",100\n");
        File.WriteAllText(Path.Combine(destination, "data.json"), "{\"title\":\"示例\",\"items\":[1,2,3]}");
        File.WriteAllText(Path.Combine(destination, "broken.json"), "{not valid JSON");
        File.WriteAllText(Path.Combine(destination, "large.json"), "[\"" + new string('x', 600000) + "\"]");
        File.WriteAllText(Path.Combine(destination, "sample.html"), "<!doctype html><html lang='zh'><meta charset='utf-8'><style>body{font:16px sans-serif;background:#202020;color:#eee;padding:24px}</style><h1>网页预览</h1><input placeholder='输入框快捷键应保留'><p>测试页面</p></html>");
        File.WriteAllBytes(Path.Combine(destination, "sample.blend"), new byte[] { 0, 1, 2 });
        Directory.CreateDirectory(Path.Combine(destination, "sample-folder"));
        File.WriteAllText(Path.Combine(destination, "sample-folder", "readme.txt"), "hello");
        foreach (var name in new[] { "small.zip", "empty.zip" })
        {
            var fixture = Path.Combine(destination, name);
            if (File.Exists(fixture)) File.Delete(fixture);
            using var zip = ZipFile.Open(fixture, ZipArchiveMode.Create);
            if (name == "small.zip") foreach (var filename in new[] { "readme.txt", "photo.png", "very-long-file-name-that-should-not-expand-the-entire-preview-window.json" })
                { using var writer = new StreamWriter(zip.CreateEntry(filename).Open()); writer.Write("test"); }
        }
    }
}
finally { Directory.Delete(root, true); }
Console.WriteLine("Preview data, Markdown, sizing, cancellation and Office regression checks passed.");

namespace WinTools.Services { internal static class ErrorReporter { internal static void Log(string source, Exception ex) { } } }
