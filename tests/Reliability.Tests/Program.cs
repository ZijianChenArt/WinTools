using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.UI.Dispatching;
using WinTools;
using WinTools.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
foreach (var invalid in new[] { "Ctrl+A+B", "A+A", "Ctrl+F1+F2", "Ctrl+@+A" })
    Require(HotkeyHelper.Parse(invalid) == null, "Multiple primary keys accepted: " + invalid);
Require(HotkeyHelper.Parse("Ctrl+Shift+F12") == (HotkeyModifiers.Control | HotkeyModifiers.Shift, (ushort)0x7B), "Valid hotkey changed");
Require(HotkeyHelper.ParseForSend("Ctrl Win Shift") == (HotkeyModifiers.Control | HotkeyModifiers.Win | HotkeyModifiers.Shift, (ushort)0), "Modifier-only send changed");
var settings = SettingsService.Instance;
var timer = (DispatcherQueueTimer)typeof(SettingsService).GetField("_saveTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!;
settings.Current.Change();
ConfigService.Fail = true;
timer.Fire();
Require(ConfigService.Attempts == 1 && ErrorReporter.Errors == 1 && timer.Started, "Failure did not schedule retry");
Require(timer.Interval == TimeSpan.FromSeconds(5), "Retry should back off");
ConfigService.Fail = false;
timer.Fire();
Require(ConfigService.Attempts == 2, "Retry lost dirty state");
settings.FlushNow();
Require(ConfigService.Attempts == 2, "Successful save stayed dirty");
settings.Current.Change();
ConfigService.Fail = true;
timer.Fire();
ConfigService.Fail = false;
settings.FlushNow();
Require(ConfigService.Attempts == 4, "Exit flush did not retry failed save");
Console.WriteLine("Hotkey rejection and settings failure/recovery/exit regression checks passed.");

var temp = Path.Combine(Path.GetTempPath(), "WinTools-preview-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var csv = Path.Combine(temp, "data.csv");
    File.WriteAllText(csv, "name,score\nalpha,10\nbeta,20\n");
    var csvText = StructuredPreviewExtractor.Extract(csv);
    Require(csvText?.Contains("name\tscore") == true && csvText.Contains("beta\t20"), "CSV preview failed");

    var json = Path.Combine(temp, "data.json");
    File.WriteAllText(json, "{\"name\":\"alpha\",\"items\":[1,2]}");
    var jsonText = StructuredPreviewExtractor.Extract(json);
    Require(jsonText?.Contains("\"items\": [") == true && jsonText.Contains("\n    1"), "JSON formatting failed");

    var zip = Path.Combine(temp, "archive.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        using var entry = new StreamWriter(archive.CreateEntry("docs/readme.txt").Open());
        entry.Write("preview");
    }
    var zipText = StructuredPreviewExtractor.Extract(zip);
    Require(zipText?.Contains("docs/readme.txt") == true, "ZIP listing failed");

    var pptx = Path.Combine(temp, "slides.pptx");
    using (var archive = ZipFile.Open(pptx, ZipArchiveMode.Create))
    {
        using var entry = new StreamWriter(archive.CreateEntry("ppt/slides/slide1.xml").Open());
        entry.Write("<p:sld xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:t>Roadmap</a:t><a:t>Next steps</a:t></p:sld>");
    }
    var pptText = OfficeTextExtractor.Extract(pptx);
    Require(pptText?.Contains("【第 1 页】") == true && pptText.Contains("Roadmap"), "PPTX outline failed");
    Console.WriteLine("PPTX, CSV, JSON and ZIP preview checks passed.");
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}
