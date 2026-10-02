using System;
using System.Linq;
using System.Threading;

namespace WinTools.Services;

/// <summary>
/// 把默认扬声器 / 麦克风维持在用户在音频设备弹窗里选过的设备上。
/// Windows 常在耳机插拔、蓝牙重连、睡眠唤醒后把默认设备改掉；这里每 5 秒读一次，
/// 发现被改了且记住的设备在线，就切回去。设备不在线或没记住时什么都不做。
/// </summary>
internal static class AudioDeviceKeeper
{
    private static Timer? _timer;
    private static int _running;
    private static string _lastLogged = "";

    internal static void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(5));
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var config = SettingsService.Instance.Current;
            // 「记住所选设备」恒开启，没有开关。
            var wanted = new[] { (Flow: 0, Id: config.AudioPreferredOutputId), (Flow: 1, Id: config.AudioPreferredInputId) }
                .Where(item => !string.IsNullOrEmpty(item.Id)).ToList();
            if (wanted.Count == 0) return;

            var devices = AudioDeviceService.Read();
            foreach (var (flow, id) in wanted)
            {
                var target = devices.FirstOrDefault(device => device.Flow == flow && device.Id == id);
                if (target == null || (target.Default && target.Communications)) continue;
                AudioDeviceService.Select(target);
                var message = $"{(flow == 0 ? "扬声器" : "麦克风")}被系统改动，已切回「{target.Name}」";
                if (message != _lastLogged) { _lastLogged = message; ErrorReporter.Log("AudioKeeper", message); }
            }
        }
        catch (Exception ex) { ErrorReporter.Log("AudioKeeper", ex); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
