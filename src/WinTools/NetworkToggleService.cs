using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Windows.Devices.Radios;

namespace WinTools;

/// <summary>提供 WiFi 与以太网（宽带）的开关切换。</summary>
public static class NetworkToggleService
{
    /// <summary>用于查找物理 WiFi 适配器的 WMI 查询。</summary>
    private const string WifiAdapterQuery =
        "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True " +
        "AND (Name LIKE '%Wi-Fi%' OR Name LIKE '%Wireless%' OR Name LIKE '%WLAN%' OR Name LIKE '%无线%' OR Name LIKE '%WiFi%') " +
        "AND NetConnectionID IS NOT NULL";

    /// <summary>用于查找物理以太网适配器的 WMI 查询。</summary>
    private const string EthernetAdapterQuery =
        "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True " +
        "AND (AdapterTypeID=0 OR Name LIKE '%Ethernet%' OR Name LIKE '%乙太%' OR Name LIKE '%以太%') " +
        "AND NetConnectionID IS NOT NULL";

    /// <summary>切换 WiFi 开关：若当前为开则关闭，否则开启。需 Radio 权限。</summary>
    public static async Task<bool> ToggleWifiAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;

            var radios = await Radio.GetRadiosAsync();
            var wifiRadio = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            if (wifiRadio == null) return false;

            var newState = wifiRadio.State == RadioState.On ? RadioState.Off : RadioState.On;
            await wifiRadio.SetStateAsync(newState);
            return true;
        }
        catch { return false; }
    }

    /// <summary>切换以太网（宽带）接口：若已启用则停用，否则启用。可能需要管理员权限。</summary>
    public static bool ToggleEthernet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(EthernetAdapterQuery);
            var target = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            if (target == null) return false;

            var enabled = (bool)(target["NetEnabled"] ?? false);
            target.InvokeMethod(enabled ? "Disable" : "Enable", null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>面板按钮文案：当前主要使用 WiFi 显示「WiFi」，否则显示「宽带」（完整双态标签见 QuickSettingActions）。</summary>
    public static async Task<string> GetCurrentNetworkLabelAsync()
    {
        try
        {
            return await IsWifiOnAsync() ? "Wi-Fi" : "宽带";
        }
        catch
        {
            return "网络";
        }
    }

    /// <summary>一键切换：在宽带与 WiFi 之间切换（关闭当前使用的、开启另一个）。</summary>
    public static async Task<bool> SwitchWifiAndEthernetAsync()
    {
        try
        {
            var wifiOn = await IsWifiOnAsync();
            var ethernetOn = IsEthernetOn();

            if (wifiOn)
            {
                // 当前用 WiFi → 关 WiFi、开宽带
                await ToggleWifiAsync();
                if (!ethernetOn) ToggleEthernet();
            }
            else
            {
                // 当前用宽带或 WiFi 关着 → 关宽带、开 WiFi
                if (ethernetOn) ToggleEthernet();
                await ToggleWifiAsync();
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>当前是否为 WiFi 模式；无法识别时返回 null（面板据此不做状态区分）。</summary>
    public static async Task<bool?> IsWifiModeActiveAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access == RadioAccessStatus.Allowed)
            {
                var radios = await Radio.GetRadiosAsync();
                var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                if (wifi != null)
                    return wifi.State == RadioState.On;
            }
        }
        catch { /* fallback */ }

        return TryGetWifiEnabledViaWmi();
    }

    /// <summary>检查 WiFi 是否开启（无法识别按未开启处理，供切换逻辑使用）。</summary>
    public static async Task<bool> IsWifiOnAsync() => await IsWifiModeActiveAsync() ?? false;

    /// <summary>WMI 读取 WiFi 适配器状态；未找到适配器或查询失败返回 null（无法识别）。</summary>
    private static bool? TryGetWifiEnabledViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WifiAdapterQuery);
            var found = false;
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                found = true;
                if (obj["NetEnabled"] is bool enabled && enabled)
                    return true;
            }
            return found ? false : null;
        }
        catch { return null; }
    }

    /// <summary>检查以太网是否启用。</summary>
    public static bool IsEthernetOn()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(EthernetAdapterQuery);
            var target = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            return target != null && (bool)(target["NetEnabled"] ?? false);
        }
        catch { return false; }
    }
}
