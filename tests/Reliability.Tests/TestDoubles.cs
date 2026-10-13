using System;
using System.ComponentModel;
namespace CommunityToolkit.Mvvm.ComponentModel { }
namespace Windows.Win32.UI.Input.KeyboardAndMouse
{
    [Flags] internal enum HOT_KEY_MODIFIERS : uint { MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8 }
}
namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue
    {
        public static DispatcherQueue GetForCurrentThread() => new();
        public DispatcherQueueTimer CreateTimer() => new();
    }
    public class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool Started { get; private set; }
        public event Action<object, object>? Tick;
        public void Start() => Started = true;
        public void Fire() { Started = false; Tick?.Invoke(this, new object()); }
    }
}
namespace WinTools
{
    public class Config : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Change() => PropertyChanged?.Invoke(this, new("Sample"));
        public object? EnableTaskbarInfo { get; set; }
        public object? TaskbarInfoAlignment { get; set; }
        public object? TaskbarInfoActions { get; set; }
        public object? TaskbarShowLibrary { get; set; }
        public object? TaskbarShowVoice { get; set; }
        public object? TaskbarShowAudio { get; set; }
        public object? Entries { get; set; }
        public object? Hotkeys { get; set; }
        public object? EnableDragStash { get; set; }
        public object? EnableProgramAssoc { get; set; }
        public object? EnableThreeFingerDrag { get; set; }
        public object? ThreeFingerLowSpeedGain { get; set; }
        public object? ThreeFingerHighSpeedGain { get; set; }
        public object? ThreeFingerAccelerationStart { get; set; }
        public object? ThreeFingerAccelerationEnd { get; set; }
        public object? ThreeFingerCalibrationUtc { get; set; }
        public object? StashWindowWidth { get; set; }
        public object? StashWindowHeight { get; set; }
        public object? AutoStart { get; set; }
        public object? AppTheme { get; set; }
        public object? AppUiStyle { get; set; }
        public object? EnableAutoUpdateCheck { get; set; }
        public object? ProgramOffsetX { get; set; }
        public object? ProgramOffsetY { get; set; }
        public object? WindowGap { get; set; }
        public object? EnablePerAppIme { get; set; }
        public object? EnableDesktopClickToShow { get; set; }
        public object? EnableExplorerPreview { get; set; }
        public object? EnableCardPreview { get; set; }
        public object? EnableVoiceBall { get; set; }
        public object? VoiceBallCustomOffset { get; set; }
        public object? VoiceBallOffsetX { get; set; }
        public object? VoiceBallOffsetY { get; set; }
        public object? DesktopZones { get; set; }
        public object? DesktopZoneSchemaVersion { get; set; }
        public object? EnableDesktopCard { get; set; }
        public object? DesktopCardGap { get; set; }
        public object? DesktopCardMargin { get; set; }
        public object? DesktopCardMaxColumns { get; set; }
        public object? DesktopCardAlignment { get; set; }
        public object? PerAppImeRules { get; set; }
        public object? ImeCategoryDefaultsInitialized { get; set; }
    }
    public static class ConfigService
    {
        public static int Attempts;
        public static bool Fail;
        public static Config Load() => new();
        public static Config GetDefault() => new();
        public static void Save(Config config) { Attempts++; if (Fail) throw new System.IO.IOException("Simulated locked config"); }
    }
}
namespace WinTools.Services
{
    public static class ErrorReporter { public static int Errors; public static void Log(string scope, Exception ex) => Errors++; }
}
