using System;
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
