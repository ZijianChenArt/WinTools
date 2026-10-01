using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using WinTools.Services;
// Windows.System 里也有同名类型（引它是为了 VirtualKey）。
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace WinTools;

/// <summary>
/// 图标右键菜单与快捷键。结构和文案对齐 Windows 11 资源管理器的新式右键菜单，
/// 另加一组桌面分区自己的功能。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>用 <see cref="CommandBarFlyout"/>：顶部一排图标（剪切 / 复制 / 重命名 / 共享 / 删除），
///         下面是列表，最后是「显示更多选项」——和 Windows 11 一样。</item>
///   <item>文案取自系统资源（2026-09-24 核对 Windows.UI.FileExplorer.dll.mui、shell32.dll.mui、
///         windows.storage.dll.mui、explorerframe.dll.mui），不要自己另起说法。</item>
///   <item>每次右键**现建**一个菜单：项目随类型（快捷方式 / 文件夹 / 普通文件 / 系统图标）变化，
///         「固定到“开始”」的文字要实时问系统。菜单关闭即释放，不常驻内存。</item>
///   <item>**不能**先枚举整套系统菜单再挑项：第三方扩展太多，一个 .lnk 实测每次约 0.95 秒。
///         只有「固定到“开始”」「共享」借用系统扩展，而且只创建那一个处理器。</item>
/// </list>
/// </remarks>
public sealed partial class DesktopCardWindow
{
    private ShellContextMenu.HandlerMenu? _shareHandler;

    private static string Glyph(int code) => char.ConvertFromUtf32(code);

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void ItemsGrid_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var container = FindItemContainer(e.OriginalSource as DependencyObject);
        if (container == null || ItemsGrid.ItemFromContainer(container) is not CardItem item) return;
        e.Handled = true;
        // 和 Windows 一样，右键的同时选中它（管理器会清掉其它卡片的选中）。
        ItemsGrid.SelectedItem = item;

        var fromKeyboard = !e.TryGetPosition(container, out var point);
        // Windows 11 里 Shift+F10 直接出完整的系统菜单（新式菜单的「显示更多选项」旁就标着它），
        // 菜单键才出新式菜单。
        if (fromKeyboard && IsKeyDown(VirtualKey.Shift) && IsKeyDown(VirtualKey.F10))
        {
            ShowSystemMenu(item, ScreenPointBelow(container));
            return;
        }

        var menu = BuildItemMenu(item);
        var options = new FlyoutShowOptions { ShowMode = FlyoutShowMode.Standard };
        if (!fromKeyboard) options.Position = point;
        menu.ShowAt(container, options);
    }

    private static GridViewItem? FindItemContainer(DependencyObject? node)
    {
        while (node != null && node is not GridViewItem) node = VisualTreeHelper.GetParent(node);
        return node as GridViewItem;
    }

    private CommandBarFlyout BuildItemMenu(CardItem item)
    {
        var isShellItem = item.IsShellItem;
        var isFolder = !isShellItem && Directory.Exists(item.Path);
        var extension = isShellItem || isFolder ? string.Empty : Path.GetExtension(item.Path);
        var isShortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        var isInternetShortcut = extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
        var shortcutTarget = isShortcut ? ShellItemActions.ResolveShortcutTarget(item.Path) : null;
        var canRunAsAdministrator = !isShellItem && !isFolder
            && ShellItemActions.IsRunnable(isShortcut ? shortcutTarget : item.Path);
        // 与系统登记的范围一致：文件夹、程序、快捷方式和系统图标；.url 在资源管理器里也没有这一项。
        var canPinToStart = isShellItem || isFolder || isShortcut
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        var isPlainFile = !isShellItem && !isFolder && !isShortcut && !isInternetShortcut
            && !ShellItemActions.IsRunnable(item.Path);

        var menu = new CommandBarFlyout { AlwaysExpanded = true };
        var disposables = new List<IDisposable>();
        menu.Closed += (_, _) =>
        {
            foreach (var disposable in disposables) disposable.Dispose();
            disposables.Clear();
        };

        // deferred：先收起菜单再执行。会弹对话框 / 另一个浮层 / UAC 的动作必须这样，
        // 否则菜单还开着就进了对话框，或者第二个浮层被正在关闭的菜单吞掉。
        // 要用到本菜单里系统处理器的动作（固定到“开始”、打开方式里的应用）必须立即执行：
        // 菜单关闭时处理器就释放了。
        AppBarButton Command(string label, int glyph, Action action, bool deferred = true,
            string? accelerator = null, string? toolTip = null)
        {
            var button = new AppBarButton
            {
                Label = label,
                Icon = new FontIcon { Glyph = Glyph(glyph) },
                // README 5.5：菜单项悬停底框 10px；CommandBarFlyout 会接管按钮样式，隐式样式管不到。
                CornerRadius = new CornerRadius(10),
            };
            if (accelerator != null) button.KeyboardAcceleratorTextOverride = accelerator;
            ToolTipService.SetToolTip(button, toolTip ?? label);
            button.Click += (_, _) =>
            {
                if (!deferred) RunMenuAction(action);
                menu.Hide();
                if (deferred) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => RunMenuAction(action));
            };
            return button;
        }

        AppBarButton SubMenu(string label, int glyph, MenuFlyout flyout)
        {
            flyout.Placement = FlyoutPlacementMode.RightEdgeAlignedTop;
            return new AppBarButton
            {
                Label = label,
                Icon = new FontIcon { Glyph = Glyph(glyph) },
                Flyout = flyout,
                CornerRadius = new CornerRadius(10),
            };
        }

        // —— 顶部图标行：剪切 / 复制 / 重命名 / 共享 / 删除 ——
        if (!isShellItem)
        {
            menu.PrimaryCommands.Add(Command("剪切", 0xE8C6, () => _ = PutOnClipboardAsync(item, cut: true),
                deferred: false, toolTip: "剪切(Ctrl+X)"));
            menu.PrimaryCommands.Add(Command("复制", 0xE8C8, () => _ = PutOnClipboardAsync(item, cut: false),
                deferred: false, toolTip: "复制(Ctrl+C)"));
            menu.PrimaryCommands.Add(Command("重命名", 0xE8AC, () => BeginRename(item), toolTip: "重命名(F2)"));
            if (!isFolder)
                menu.PrimaryCommands.Add(Command("共享", 0xE72D, () => ShareItem(item)));
            menu.PrimaryCommands.Add(Command("删除", 0xE74D, () => DeleteItem(item), toolTip: "删除(Delete)"));
        }

        // —— 系统功能：与 Windows 11 的顺序一致 ——
        menu.SecondaryCommands.Add(Command("打开", 0xE8E5, () => OpenItem(item), accelerator: "Enter"));

        if (isPlainFile)
            menu.SecondaryCommands.Add(SubMenu("打开方式", 0xE7AC, BuildOpenWithMenu(item, menu, disposables)));

        if (canRunAsAdministrator)
            menu.SecondaryCommands.Add(Command("以管理员身份运行", 0xEA18, () =>
            {
                if (!ShellItemActions.RunAsAdministrator(item.Path, _hwnd))
                    ShowTransientToast("无法以管理员身份运行。");
            }));

        if (shortcutTarget != null)
            menu.SecondaryCommands.Add(Command("打开文件所在的位置", 0xE838, () =>
            {
                if (!ShellItemActions.ShowInExplorer(shortcutTarget))
                    ShowTransientToast("找不到快捷方式指向的位置。");
            }));

        if (canPinToStart)
        {
            // 文字由系统处理器当场给出：没固定时是「固定到“开始”」，已固定时是「从“开始”菜单取消固定」。
            var pin = ShellContextMenu.QueryHandler(ShellItemActions.StartPinHandler, item.Path, _hwnd);
            if (pin != null)
            {
                disposables.Add(pin);
                foreach (var entry in pin.Entries)
                {
                    var unpin = entry.Verb.StartsWith("unpin", StringComparison.OrdinalIgnoreCase)
                        || entry.Text.Contains("取消固定", StringComparison.Ordinal);
                    menu.SecondaryCommands.Add(Command(entry.Text, unpin ? 0xE77A : 0xE718, () =>
                    {
                        if (!pin.Invoke(entry, _hwnd))
                            ShowTransientToast(unpin ? "取消固定失败。" : "固定到“开始”失败。");
                    }, deferred: false));
                }
            }
        }

        if (string.Equals(item.Path, ShellItemActions.RecycleBin, StringComparison.OrdinalIgnoreCase))
            menu.SecondaryCommands.Add(Command("清空回收站", 0xE74D, () => ShellItemActions.EmptyRecycleBin(_hwnd)));

        if (!isShellItem)
        {
            menu.SecondaryCommands.Add(Command("压缩为 ZIP 文件", 0xF012, () => _ = CompressItemAsync(item)));
            menu.SecondaryCommands.Add(Command("复制文件地址", 0xE71B, () => CopyItemPath(item),
                deferred: false, accelerator: "Ctrl+Shift+C"));
        }

        menu.SecondaryCommands.Add(Command("属性", 0xE90F, () =>
        {
            if (!ShellItemActions.ShowProperties(item.Path, _hwnd))
                ShowTransientToast("无法打开属性。");
        }, accelerator: "Alt+Enter"));

        if (isFolder && ShellItemActions.CanOpenInTerminal)
            menu.SecondaryCommands.Add(Command("在终端中打开", 0xE756, () =>
            {
                if (!ShellItemActions.OpenInTerminal(item.Path))
                    ShowTransientToast("无法打开终端。");
            }));

        // —— 桌面分区自己的功能 ——
        menu.SecondaryCommands.Add(new AppBarSeparator());
        menu.SecondaryCommands.Add(SubMenu("移动到分区", 0xE8DE, BuildMoveToZoneMenu(item, menu)));
        if (!isShellItem)
            menu.SecondaryCommands.Add(Command("在桌面文件夹中显示", 0xEC50, () =>
            {
                if (!ShellItemActions.ShowInExplorer(item.Path))
                    ShowTransientToast("找不到这个项目，可能已被移走。");
            }));
        var resetOrder = Command("恢复默认排序", 0xE8CB, ResetItemOrder);
        resetOrder.IsEnabled = CardItemOrderStore.HasCustomOrder(_zoneName);
        menu.SecondaryCommands.Add(resetOrder);
        menu.SecondaryCommands.Add(Command("刷新", 0xE72C, RefreshCard));
        menu.SecondaryCommands.Add(Command("分区设置", 0xE713,
            () => (Application.Current as App)?.OpenDesktopZoneSettings()));

        // —— 最后一项永远是完整的系统菜单 ——
        menu.SecondaryCommands.Add(new AppBarSeparator());
        menu.SecondaryCommands.Add(Command("显示更多选项", 0xE8A7, () => ShowSystemMenu(item),
            accelerator: "Shift+F10"));

        return menu;
    }

    private void RunMenuAction(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            ErrorReporter.Log("DesktopCard.ItemMenu", ex);
            ShowTransientToast("操作失败。");
        }
    }

    /// <summary>「打开方式」子菜单：系统推荐的应用 + 「选择其他应用」。</summary>
    private MenuFlyout BuildOpenWithMenu(CardItem item, CommandBarFlyout owner, List<IDisposable> disposables)
    {
        var flyout = new MenuFlyout();
        var apps = ShellItemActions.GetOpenWithApps(item.Path);
        disposables.AddRange(apps);
        foreach (var app in apps)
        {
            var entry = new MenuFlyoutItem { Text = app.Name, Icon = CreateAppIcon(app) };
            entry.Click += (_, _) =>
            {
                // 应用处理器随菜单释放，所以先启动再收起。
                if (!app.Launch(item.Path, _hwnd)) ShowTransientToast($"无法用「{app.Name}」打开。");
                owner.Hide();
            };
            flyout.Items.Add(entry);
        }
        if (apps.Count > 0) flyout.Items.Add(new MenuFlyoutSeparator());
        var other = new MenuFlyoutItem { Text = "选择其他应用" };
        other.Click += (_, _) =>
        {
            owner.Hide();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => ShellItemActions.ShowOpenWithDialog(item.Path, _hwnd));
        };
        flyout.Items.Add(other);
        return flyout;
    }

    /// <summary>应用图标：普通程序从 exe / dll 里取；商店应用的 <c>@{...}</c> 先解析成图片路径。取不到就不放图标。</summary>
    private static IconElement? CreateAppIcon(ShellItemActions.OpenWithApp app)
    {
        if (string.IsNullOrWhiteSpace(app.IconPath)) return null;
        try
        {
            if (app.IconPath.StartsWith("@", StringComparison.Ordinal))
            {
                var image = ShellItemActions.ResolveIndirectString(app.IconPath);
                return image != null && File.Exists(image)
                    ? new ImageIcon { Source = new BitmapImage(new Uri(image)) }
                    : null;
            }
            var bytes = CardItem.ReadExtractedIconPng(Environment.ExpandEnvironmentVariables(app.IconPath), app.IconIndex);
            if (bytes == null) return null;
            var bitmap = new BitmapImage();
            _ = SetBitmapSourceAsync(bitmap, bytes);
            return new ImageIcon { Source = bitmap };
        }
        catch
        {
            return null;
        }
    }

    private static async Task SetBitmapSourceAsync(BitmapImage bitmap, byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
        }
        catch { /* 图标失败只是少个图标 */ }
    }

    /// <summary>「移动到分区」子菜单：列出全部分区，当前分区打勾。效果与把图标拖到另一张卡片相同。</summary>
    private MenuFlyout BuildMoveToZoneMenu(CardItem item, CommandBarFlyout owner)
    {
        var flyout = new MenuFlyout();
        foreach (var zone in SettingsService.Instance.Current.DesktopZones ?? new List<DesktopZone>())
        {
            var zoneName = zone.Name;
            var entry = new RadioMenuFlyoutItem
            {
                Text = zoneName,
                GroupName = "WinTools.MoveToZone",
                IsChecked = string.Equals(zoneName, _zoneName, StringComparison.Ordinal),
            };
            entry.Click += (_, _) =>
            {
                owner.Hide();
                MoveItemToZone(item, zoneName);
            };
            flyout.Items.Add(entry);
        }
        return flyout;
    }

    private void MoveItemToZone(CardItem item, string zoneName)
    {
        if (string.Equals(zoneName, _zoneName, StringComparison.Ordinal)) return;
        // 系统图标没有文件，按显示名归属（DesktopZoneMatcher 对显式清单就是按名字比的），不用查文件。
        if (!item.IsShellItem)
        {
            var result = DesktopCollectService.CanAssignToZone(item.Path, zoneName);
            if (result is not (DesktopCollectService.MoveZoneResult.Success or DesktopCollectService.MoveZoneResult.SameZone))
            {
                ShowTransientToast("移动失败：项目不存在或目标分区无效。");
                return;
            }
        }
        // 与拖放换区走同一条路：把名字记进目标分区的显式清单，文件不动。
        ItemMovedIn?.Invoke(this, new CardItemMovedEventArgs(item.DisplayName, zoneName));
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ResetItemOrder()
    {
        if (!CardItemOrderStore.Reset(_zoneName))
        {
            ShowTransientToast("恢复默认排序失败，请重试。");
            return;
        }
        RefreshContent();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「刷新」：重新向系统取这张卡片的图标，并重新同步桌面。</summary>
    private void RefreshCard()
    {
        foreach (var cardItem in _items)
        {
            CardItem.ForgetCachedIcon(cardItem.Path);
            _iconTasks.Add(cardItem.LoadIconAsync());
        }
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「剪切」「复制」：放到剪贴板，在资源管理器里粘贴即可移动 / 复制，和在资源管理器里操作一样。</summary>
    private async Task PutOnClipboardAsync(CardItem item, bool cut)
    {
        if (item.IsShellItem) return;
        try
        {
            IStorageItem storageItem = Directory.Exists(item.Path)
                ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                : await StorageFile.GetFileFromPathAsync(item.Path);
            var package = new DataPackage
            {
                RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy,
            };
            package.SetStorageItems(new[] { storageItem });
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopCard.Clipboard({item.Path})", ex);
            ShowTransientToast(cut ? "剪切失败：项目可能已被移走。" : "复制失败：项目可能已被移走。");
        }
    }

    /// <summary>「复制文件地址」：带引号的完整路径，和资源管理器一致。</summary>
    private void CopyItemPath(CardItem item)
    {
        if (item.IsShellItem) return;
        try
        {
            var package = new DataPackage();
            package.SetText('"' + item.Path + '"');
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopCard.CopyPath({item.Path})", ex);
            ShowTransientToast("复制文件地址失败。");
        }
    }

    /// <summary>「共享」：借用系统的共享扩展弹出 Windows 共享面板。</summary>
    private void ShareItem(CardItem item)
    {
        // 共享面板在处理器执行完之后才出现，处理器留到下次共享或卡片关闭时再释放。
        ReleaseShareHandler();
        _shareHandler = ShellContextMenu.QueryHandler(ShellItemActions.ShareHandler, item.Path, _hwnd);
        if (_shareHandler != null)
        {
            GetCursorPos(out var cursor);
            foreach (var entry in _shareHandler.Entries)
            {
                if (!entry.Verb.Equals("Windows.ModernShare", StringComparison.OrdinalIgnoreCase)) continue;
                if (_shareHandler.Invoke(entry, _hwnd, (cursor.X, cursor.Y))) return;
            }
        }
        ShowTransientToast("无法共享这个项目。");
    }

    private void ReleaseShareHandler()
    {
        _shareHandler?.Dispose();
        _shareHandler = null;
    }

    /// <summary>「压缩为 ZIP 文件」：后台压缩，完成后新文件会自动出现在对应的卡片里。</summary>
    private async Task CompressItemAsync(CardItem item)
    {
        var task = ShellItemActions.CompressToZipAsync(item.Path);
        // 小文件一眨眼就好，只有超过 0.8 秒才提示「正在压缩」，免得两条提示连着闪。
        if (await Task.WhenAny(task, Task.Delay(800)) != task)
            ShowTransientToast($"正在压缩「{item.DisplayName}」…", InfoBarSeverity.Informational);
        try
        {
            var zip = await task;
            ShowTransientToast($"已压缩为「{Path.GetFileName(zip)}」。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ErrorReporter.Log($"DesktopCard.Compress({item.Path})", ex);
            ShowTransientToast("压缩失败：文件可能正在使用，或没有权限。");
        }
    }

    /// <summary>
    /// 选中图标后的快捷键，与资源管理器一致：Enter 打开、F2 重命名、Delete 删除、Ctrl+C / Ctrl+X 复制 / 剪切、
    /// Ctrl+Shift+C 复制文件地址、Alt+Enter 属性。菜单键 / Shift+F10 由 <see cref="ItemsGrid_ContextRequested"/> 处理。
    /// </summary>
    /// <remarks>用 PreviewKeyDown：GridView 自己会把 Enter 标成已处理（用于 ItemClick）。</remarks>
    private void ItemsGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not CardItem item) return;
        var control = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var alt = e.KeyStatus.IsMenuKeyDown || IsKeyDown(VirtualKey.Menu);

        switch (e.Key)
        {
            case VirtualKey.Enter when alt && !control && !shift:
                if (!ShellItemActions.ShowProperties(item.Path, _hwnd)) ShowTransientToast("无法打开属性。");
                break;
            case VirtualKey.Enter when !alt && !control && !shift:
                OpenItem(item);
                break;
            case VirtualKey.F2 when !alt && !control && !shift:
                BeginRename(item);
                break;
            case VirtualKey.Delete when !alt && !control && !shift:
                DeleteItem(item);
                break;
            case VirtualKey.C when control && shift && !alt:
                CopyItemPath(item);
                break;
            case VirtualKey.C when control && !shift && !alt:
                _ = PutOnClipboardAsync(item, cut: false);
                break;
            case VirtualKey.X when control && !shift && !alt:
                _ = PutOnClipboardAsync(item, cut: true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>元素左下角的屏幕坐标（物理像素），给键盘呼出的系统菜单定位。</summary>
    private (int X, int Y)? ScreenPointBelow(FrameworkElement element)
    {
        try
        {
            var origin = element.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, element.ActualHeight));
            var scale = element.XamlRoot?.RasterizationScale ?? CurrentScale;
            var point = new POINT { X = (int)Math.Round(origin.X * scale), Y = (int)Math.Round(origin.Y * scale) };
            return ClientToScreen(_hwnd, ref point) ? (point.X, point.Y) : null;
        }
        catch
        {
            return null;
        }
    }
}
