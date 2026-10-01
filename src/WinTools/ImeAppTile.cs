using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinTools.Services;

namespace WinTools;

/// <summary>
/// 输入法切换页里的一个应用图标块。三个区域（未设置 / 中文 / 英文）共用同一个类型；
/// 同一个应用在整个页面生命周期里只有一个实例（由 MainWindow 缓存），图标只加载一次。
/// </summary>
public sealed class ImeAppTile : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private string _displayName;
    private string _exePath;
    private string _mode = "pool";
    private bool _iconRequested;

    public ImeAppTile(string processName, string displayName, string exePath)
    {
        ProcessName = processName;
        _displayName = string.IsNullOrWhiteSpace(displayName) ? processName : displayName;
        _exePath = exePath ?? "";
    }

    public string ProcessName { get; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _displayName) return;
            _displayName = value;
            Raise(nameof(DisplayName));
            Raise(nameof(Tooltip));
            Raise(nameof(Letter));
        }
    }

    public string ExePath
    {
        get => _exePath;
        set => _exePath = value ?? "";
    }

    /// <summary>本机能找到这个应用的 exe。预设 / 已保存的规则只有在这里为 true 时才显示，没装的软件不占位。</summary>
    public bool IsInstalled
    {
        get
        {
            try { return !string.IsNullOrEmpty(_exePath) && File.Exists(_exePath); }
            catch { return false; }
        }
    }

    private DateTime _attemptedAt = DateTime.MinValue;

    /// <summary>没找到 exe 的应用隔一阵重新探测，这样之后装上软件它就会出现。</summary>
    public void RetryIfStale()
    {
        if (IsInstalled || !_iconRequested) return;
        if (DateTime.UtcNow - _attemptedAt < TimeSpan.FromSeconds(30)) return;
        _iconRequested = false;
    }

    /// <summary>"pool"（未设置）/ "zh"（中文）/ "en"（英文）。</summary>
    public string Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public string Tooltip => string.Equals(_displayName, ProcessName, StringComparison.OrdinalIgnoreCase)
        ? _displayName
        : $"{_displayName}\n{ProcessName}";

    /// <summary>没有图标时的占位字母。</summary>
    public string Letter
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(_displayName) ? ProcessName : _displayName;
            return source.Length == 0 ? "?" : source[..1].ToUpperInvariant();
        }
    }

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            Raise(nameof(Icon));
            Raise(nameof(IconVisibility));
            Raise(nameof(LetterVisibility));
        }
    }

    public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LetterVisibility => _icon != null ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>解析 exe 路径并加载图标。只会真正执行一次；必须在 UI 线程调用。</summary>
    public async Task EnsureIconAsync()
    {
        if (_iconRequested) return;
        _iconRequested = true;
        _attemptedAt = DateTime.UtcNow;

        try
        {
            var known = _exePath;
            var name = ProcessName;
            var display = _displayName;
            var path = await Task.Run(() => AppIconLoader.ResolveExePath(name, known, display));
            if (string.IsNullOrEmpty(path)) return;

            _exePath = path;
            var icon = await AppIconLoader.LoadAsync(path);
            if (icon != null) Icon = icon;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"ImeAppTile.EnsureIconAsync({ProcessName})", ex);
        }
    }
}

/// <summary>按进程名找 exe、再取 Shell 图标。</summary>
internal static class AppIconLoader
{
    private static async Task<BitmapImage?> ExtractIconAsync(string exePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var picture = icon.ToBitmap();
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var bytes = new MemoryStream())
            {
                picture.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
                var buffer = bytes.ToArray();
                using var writer = new Windows.Storage.Streams.DataWriter(stream);
                writer.WriteBytes(buffer);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"AppIconLoader.ExtractIcon({exePath})", ex);
            return null;
        }
    }

    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已知路径优先；否则找正在运行的同名进程；再不行查注册表 App Paths。
    /// 权限不够读不到 MainModule（如管理员进程）时静默跳过。
    /// </summary>
    public static string? ResolveExePath(string processName, string knownPath, string displayName = "")
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(knownPath) && File.Exists(knownPath)) return knownPath;
            if (string.IsNullOrWhiteSpace(processName)) return null;

            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { processes = Array.Empty<Process>(); }

            string? found = null;
            foreach (var process in processes)
            {
                try
                {
                    if (found == null)
                    {
                        var file = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(file) && File.Exists(file)) found = file;
                    }
                }
                catch
                {
                    // 拒绝访问 / 进程已退出
                }
                finally
                {
                    process.Dispose();
                }
            }
            if (found != null) return found;

            var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                    var value = key?.GetValue(null) as string;
                    if (string.IsNullOrEmpty(value)) continue;
                    value = value.Trim('"');
                    if (File.Exists(value)) return value;
                }
                catch
                {
                    // 忽略单个注册表根的错误
                }
            }

            // 预设规则里的应用多半没在运行、也没有 App Paths：用开始菜单 / 桌面快捷方式索引按文件名、显示名找。
            var fromIndex = ResolveFromStartMenu(processName, displayName);
            if (fromIndex != null) return fromIndex;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"AppIconLoader.ResolveExePath({processName})", ex);
        }
        return null;
    }

    /// <summary>在已安装程序索引里找：快捷方式目标的文件名等于进程名，或快捷方式名等于显示名。</summary>
    private static string? ResolveFromStartMenu(string processName, string displayName)
    {
        try
        {
            var entries = AppSearchIndex.GetAsync().GetAwaiter().GetResult();
            var exeName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
            string? byName = null;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.IconPath) || !File.Exists(entry.IconPath)) continue;
                if (!entry.IconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(Path.GetFileNameWithoutExtension(entry.IconPath), exeName, StringComparison.OrdinalIgnoreCase))
                    return entry.IconPath;
                if (byName == null && !string.IsNullOrWhiteSpace(displayName)
                    && string.Equals(entry.Name, displayName, StringComparison.OrdinalIgnoreCase))
                    byName = entry.IconPath;
            }
            return byName;
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"AppIconLoader.ResolveFromStartMenu({processName})", ex);
            return null;
        }
    }

    /// <summary>取 exe 的 Shell 图标。必须在 UI 线程调用（BitmapImage 要在 UI 线程创建）。</summary>
    public static async Task<BitmapImage?> LoadAsync(string exePath)
    {
        if (Cache.TryGetValue(exePath, out var cached)) return cached;

        BitmapImage? bitmap = null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(exePath);
            var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 64);
            if (thumb == null || thumb.Size == 0)
                thumb = await file.GetThumbnailAsync(ThumbnailMode.ListView, 64);
            if (thumb != null && thumb.Size > 0)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                bitmap = bmp;
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"AppIconLoader.LoadAsync({exePath})", ex);
        }

        // 缩略图取不到（拒绝访问、没有缩略图提供者）时，退回到直接从 exe 提取图标。
        bitmap ??= await ExtractIconAsync(exePath);

        // 只缓存成功的结果：失败缓存成 null 会让这个应用在整个会话里都没有图标。
        if (bitmap != null) Cache[exePath] = bitmap;
        return bitmap;
    }
}
