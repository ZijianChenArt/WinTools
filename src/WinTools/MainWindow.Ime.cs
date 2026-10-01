using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using WinTools.Services;

namespace WinTools;

/// <summary>按程序切换输入法页面。</summary>
/// <remarks>
/// 页面结构：上方是“未设置”的运行中应用（只显示图标），下方左右两栏 = 中文 / 英文。
/// 图标可以拖到目标栏，也可以点一下从菜单里选。规则的持久化仍是 <see cref="_imeRules"/>，
/// 三个 GridView 都只是它的视图（每次变更后整体重建）。
/// MainWindow 的分部实现，字段与壳层逻辑仍在 MainWindow.xaml.cs。
/// </remarks>
public sealed partial class MainWindow
{
    #region 输入法切换
    private const string ImeTargetPool = "pool";
    private const string ImeTargetChinese = "zh";
    private const string ImeTargetEnglish = "en";

    private readonly ObservableCollection<ImeAppTile> _imePoolTiles = new();
    private readonly ObservableCollection<ImeAppTile> _imeChineseTiles = new();
    private readonly ObservableCollection<ImeAppTile> _imeEnglishTiles = new();

    // 按进程名缓存图标块：重建视图时复用同一个实例，图标不会闪烁、也不用重复加载。
    private readonly Dictionary<string, ImeAppTile> _imeTiles = new(StringComparer.OrdinalIgnoreCase);
    private List<RunningAppInfo> _imeRunningApps = new();
    private ImeAppTile? _imeDragTile;
    private bool _imeGridsBound;

    private void LoadPerAppImeFromConfig()
    {

        _isLoadingDragStashSettings = true;

        PerAppImeToggle.IsOn = _config.EnablePerAppIme;

        _isLoadingDragStashSettings = false;

        LoadImeRules();


    }

    internal void PerAppImeToggle_Toggled(object sender, RoutedEventArgs e)
    {

        if (_isLoadingDragStashSettings) return;

        _config.EnablePerAppIme = PerAppImeToggle.IsOn;

        (App.Current as App)?.SetPerAppImeEnabled(_config.EnablePerAppIme);

        UpdateNavStatusIndicators();


    }

    private void LoadImeRules()
    {

        _imeRulesListInitialized = true;

        _imeRules.Clear();

        _config.PerAppImeRules ??= new List<ImeRuleEntry>();

        foreach (var rule in _config.PerAppImeRules)
        {

            _imeRules.Add(new ImeRuleEntry
            {

                ProcessName = rule.ProcessName,
                DisplayName = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.ProcessName : rule.DisplayName,
                UseChinese = rule.UseChinese,
                ExePath = rule.ExePath ?? ""
            }

);


        }

        if (!_imeGridsBound)
        {
            ImePoolGridView.ItemsSource = _imePoolTiles;
            ImeChineseGridView.ItemsSource = _imeChineseTiles;
            ImeEnglishGridView.ItemsSource = _imeEnglishTiles;
            _imeGridsBound = true;
        }

        _imeRunningApps = GetImeCandidateApps();
        RebuildImeTiles();


    }

    /// <summary>正在运行、有可见窗口的应用（去掉 WinTools 自己）。</summary>
    private static List<RunningAppInfo> GetImeCandidateApps()
    {
        string self;
        try { self = Process.GetCurrentProcess().ProcessName; }
        catch { self = ""; }

        return RunningProcessHelper.GetRunningApps()
            .Where(a => !string.Equals(a.ProcessName, self, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private ImeAppTile GetImeTile(string processName, string displayName, string exePath)
    {
        if (_imeTiles.TryGetValue(processName, out var tile))
        {
            tile.DisplayName = displayName;
            if (string.IsNullOrEmpty(tile.ExePath) && !string.IsNullOrEmpty(exePath)) tile.ExePath = exePath;
            return tile;
        }

        tile = new ImeAppTile(processName, displayName, exePath);
        _imeTiles[processName] = tile;
        return tile;
    }

    /// <summary>按当前规则 + 运行中的应用重排三个区域，并异步补齐图标。</summary>
    private void RebuildImeTiles()
    {
        _imePoolTiles.Clear();
        _imeChineseTiles.Clear();
        _imeEnglishTiles.Clear();

        var all = new List<ImeAppTile>();

        var runningPaths = _imeRunningApps
            .GroupBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().ExePath, StringComparer.OrdinalIgnoreCase);
        foreach (var rule in _imeRules)
        {
            runningPaths.TryGetValue(rule.ProcessName, out var runningPath);
            var tile = GetImeTile(rule.ProcessName, rule.DisplayName,
                string.IsNullOrEmpty(rule.ExePath) ? runningPath ?? "" : rule.ExePath);
            tile.Mode = rule.UseChinese ? ImeTargetChinese : ImeTargetEnglish;
            tile.RetryIfStale();
            all.Add(tile); // 没装的也要参与图标 / 路径解析，装上之后才会出现
            if (!tile.IsInstalled) continue; // 本机没有这个软件：不显示，规则仍保留在配置里
            (rule.UseChinese ? _imeChineseTiles : _imeEnglishTiles).Add(tile);
        }

        var assigned = new HashSet<string>(_imeRules.Select(r => r.ProcessName), StringComparer.OrdinalIgnoreCase);
        foreach (var app in _imeRunningApps)
        {
            if (assigned.Contains(app.ProcessName)) continue;

            var tile = GetImeTile(app.ProcessName, app.DisplayName, app.ExePath);
            tile.Mode = ImeTargetPool;
            _imePoolTiles.Add(tile);
            all.Add(tile);
        }

        UpdateImeCount();
        _ = LoadImeIconsAsync(all);
    }

    private async Task LoadImeIconsAsync(List<ImeAppTile> tiles)
    {
        var wasInstalled = tiles.ToDictionary(tile => tile, tile => tile.IsInstalled);
        var pending = tiles.Select(async tile =>
        {
            await tile.EnsureIconAsync();
            return SyncImeExePath(tile);
        }).ToList();

        var changed = await Task.WhenAll(pending);

        // 刚解析出路径的规则（新装的软件，或首次探测完成）补进列表。
        if (tiles.Any(tile => !wasInstalled[tile] && tile.IsInstalled)) RebuildImeTiles();

        // 第一次解析出路径的规则存进配置，下次即使应用没在运行也能显示图标。
        if (changed.Any(c => c) && _imeRulesListInitialized) SaveImeRules();
    }

    private bool SyncImeExePath(ImeAppTile tile)
    {
        if (string.IsNullOrEmpty(tile.ExePath)) return false;

        var rule = _imeRules.FirstOrDefault(r =>
            string.Equals(r.ProcessName, tile.ProcessName, StringComparison.OrdinalIgnoreCase));
        if (rule == null || string.Equals(rule.ExePath, tile.ExePath, StringComparison.OrdinalIgnoreCase))
            return false;

        rule.ExePath = tile.ExePath;
        return true;
    }

    private void UpdateImeCount()
    {

        ImeCountText.Text = _imeRules.Count > 0 ? $"已设置 {_imeRules.Count} 个" : "";

        ImeChineseCountText.Text = _imeChineseTiles.Count > 0 ? $"{_imeChineseTiles.Count}" : "";
        ImeEnglishCountText.Text = _imeEnglishTiles.Count > 0 ? $"{_imeEnglishTiles.Count}" : "";

        ImePoolEmptyHint.Visibility = _imePoolTiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ImeChineseEmptyHint.Visibility = _imeChineseTiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        ImeEnglishEmptyHint.Visibility = _imeEnglishTiles.Count > 0 ? Visibility.Collapsed : Visibility.Visible;


    }

    private void SaveImeRules()
    {

        _config.PerAppImeRules = _imeRules.Select(r => new ImeRuleEntry
        {

            ProcessName = r.ProcessName,
            DisplayName = r.DisplayName,
            UseChinese = r.UseChinese,
            ExePath = r.ExePath
        }

).ToList();

        ConfigService.Save(_config);

        (App.Current as App)?.UpdatePerAppImeRules(_config.PerAppImeRules);

        UpdateImeCount();


    }

    /// <summary>把一个应用放到指定区域：pool = 取消设置，zh / en = 设置输入语言。</summary>
    private void SetImeTarget(ImeAppTile tile, string target)
    {
        if (tile.Mode == target) return;

        var existing = _imeRules.FirstOrDefault(r =>
            string.Equals(r.ProcessName, tile.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (target == ImeTargetPool)
        {
            if (existing != null) _imeRules.Remove(existing);
        }
        else
        {
            var useChinese = target == ImeTargetChinese;
            if (existing != null)
            {
                existing.UseChinese = useChinese;
            }
            else
            {
                _imeRules.Add(new ImeRuleEntry
                {
                    ProcessName = tile.ProcessName,
                    DisplayName = tile.DisplayName,
                    UseChinese = useChinese,
                    ExePath = tile.ExePath
                });
            }
        }

        SaveImeRules();
        RebuildImeTiles();
    }

    internal void Ime_RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        _imeRunningApps = GetImeCandidateApps();
        RebuildImeTiles();
    }

    internal void Ime_RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {

        var rules = _imeRules.ToList();

        ConfigService.MergeRecommendedImeRules(rules);

        _imeRules.Clear();

        foreach (var rule in rules) _imeRules.Add(rule);

        _config.ImeCategoryDefaultsInitialized = true;

        SaveImeRules();

        RebuildImeTiles();


    }

    /// <summary>点击图标：弹出菜单，选择中文 / 英文 / 取消设置。</summary>
    internal void ImeGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ImeAppTile tile || sender is not GridView grid) return;

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = tile.DisplayName, IsEnabled = false });
        flyout.Items.Add(new MenuFlyoutSeparator());

        void AddItem(string text, string target)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => SetImeTarget(tile, target);
            flyout.Items.Add(item);
        }

        if (tile.Mode != ImeTargetChinese) AddItem("使用中文", ImeTargetChinese);
        if (tile.Mode != ImeTargetEnglish) AddItem("使用英文", ImeTargetEnglish);
        if (tile.Mode != ImeTargetPool) AddItem("取消设置", ImeTargetPool);

        flyout.ShowAt(grid.ContainerFromItem(tile) as FrameworkElement ?? grid);
    }

    internal void ImeGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not ImeAppTile tile) return;

        _imeDragTile = tile;
        e.Data.SetText(tile.ProcessName);
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    internal void ImeGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) => _imeDragTile = null;

    internal void ImePanel_DragOver(object sender, DragEventArgs e)
    {
        if (_imeDragTile == null) return;

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
    }

    internal void ImePanel_Drop(object sender, DragEventArgs e)
    {
        if (_imeDragTile is not { } tile || sender is not FrameworkElement { Tag: string target }) return;

        _imeDragTile = null;
        e.Handled = true;
        SetImeTarget(tile, target);
    }

    #endregion
}
