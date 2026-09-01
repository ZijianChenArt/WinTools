# WinTools 维护手册

基于 .NET 10 + WinUI 3 的 Windows 桌面工具集：桌面分区卡片、悬浮暂存、三指拖拽、
按程序切换输入法、程序关联、点击桌面、快捷设置面板。

> **这是仓库里唯一的说明文件**（含 AI 协作者）。改 UI 之前必须读第 5 节，
> 那一节的规则是硬约束，不是建议。

---

## 1. 项目结构

```text
WinTools/
├─ WinTools.sln
├─ global.json                  # 锁定 .NET SDK 10.0.400
├─ README.md                    # 本文件：唯一的文档入口
├─ src/
│  ├─ WinTools/                 # 主程序
│  │  ├─ Styles/PageStyles.xaml # ★ 全部 UI 令牌与页面样式的唯一真源
│  │  ├─ MainWindow.xaml        # 外壳：标题栏 + 导航 + 内容框
│  │  ├─ FeaturePagesHost.xaml  # 全部功能子页面
│  │  ├─ Desktop*.cs            # 桌面分区：收纳 / 匹配 / 卡片 / 布局
│  │  └─ Services/              # 布局缓存、错误日志、配置、窗口注册表
│  └─ WinToolsExplorerCommand/  # C++ 资源管理器右键菜单扩展（IExplorerCommand）
├─ scripts/
│  ├─ Publish-Release.ps1       # 便携版发布
│  └─ Start-CodexWhenOnline.ps1 # 与本程序无关的网络辅助脚本
└─ artifacts/release/win-x64/   # 当前发布产物（约 82 MB）
```

`bin/`、`obj/` 是编译缓存。用户配置在 `%LOCALAPPDATA%\WinTools`，永远不要打进发布目录。

---

## 2. 构建（两步，缺一不可）

VS 2022 自带的 MSBuild 是 17.14，低于 .NET 10 SDK 要求的 18.0；而 `dotnet` CLI 又构建不了
C++ 项目。所以必须分两步，顺序不能反：

```bash
"C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe" src/WinToolsExplorerCommand/WinToolsExplorerCommand.vcxproj /p:Configuration=Debug /p:Platform=x64
```

```bash
dotnet build src/WinTools/WinTools.csproj -c Debug -p:Platform=x64
```

第一步不做，第二步会以 `MSB3030 找不到 WinToolsExplorerCommand.dll` 失败。

调试产物：`src/WinTools/bin/x64/Debug/net10.0-windows10.0.22621.0/WinTools.exe`

## 3. 发布

先关掉正在运行的 WinTools，否则文件被占用。脚本自己会先用 MSBuild 编原生扩展，
再 `dotnet publish`，最后原子替换 `artifacts/release/win-x64`：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1 -Runtime win-x64
```

框架依赖版本，目标机需要 .NET 10 运行时与 Windows App SDK。**不要**开启
`PublishTrimmed` / `PublishReadyToRun`（WinUI 3 会崩）。
「整理桌面」右键菜单依赖 MSIX 包身份，便携版没有该菜单，其余功能不受影响。

## 4. 开机自启

写在当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `WinTools` 值，
由 `AutostartService` 管理，内容是**当时那个 exe 的完整路径**（带引号）。

- 换了发布目录就必须重设，否则注册表指向不存在的旧路径，开机静默失败。
- 设置页的开关是拿注册表值和「当前正在运行的 exe」比对，所以从 `bin\Debug` 启动时会显示关闭；
  正式使用请从 `artifacts/release/win-x64/WinTools.exe` 启动后再开。

手动修：

```bash
powershell -NoProfile -Command "Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name WinTools -Value '\"C:\Users\orang\Desktop\WInTools\artifacts\release\win-x64\WinTools.exe\"'"
```

---

## 5. 界面规范（改 UI 必读）

间距、圆角、字号、卡片外观的**唯一真源**是 `src/WinTools/Styles/PageStyles.xaml`
（在 `App.xaml` 里全局合并）。页面 XAML 里不允许写死 `Margin` / `Padding` /
`CornerRadius` / `FontSize`，也不允许出现 `MaxWidth`；C# 动态建 UI 时从
`Application.Current.Resources["PageXxx"]` 取样式，不要在代码里 new `Style`。

### 5.1 子页面骨架（固定表头 + 撑满主体）

```xml
<Grid x:Name="ContentXxx" Style="{StaticResource PageRootStyle}" Visibility="Collapsed">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>   <!-- 表头，不随内容滚动 -->
        <RowDefinition Height="*"/>      <!-- 主体，占满剩余高度 -->
    </Grid.RowDefinitions>

    <Grid Grid.Row="0" Style="{StaticResource PageHeaderStyle}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <StackPanel Grid.Column="0" Style="{StaticResource PageHeaderTextStackStyle}">
            <TextBlock Text="页面名" Style="{StaticResource PageTitleTextStyle}"/>
            <TextBlock Text="一句话说明这个页面在做什么。" Style="{StaticResource PageDescriptionTextStyle}"/>
        </StackPanel>
        <ToggleSwitch Grid.Column="1" OnContent="已启用" OffContent="已禁用" VerticalAlignment="Center"/>
    </Grid>

    <!-- 主体二选一 -->
    <ScrollViewer Grid.Row="1" Style="{StaticResource PageBodyScrollStyle}">…</ScrollViewer>
    <Grid         Grid.Row="1" Style="{StaticResource PageBodyGridStyle}">…</Grid>
</Grid>
```

- **设置型页面**（快捷设置 / 三指拖拽 / 悬浮暂存 / 点击桌面 / 设置）：主体是
  `PageBodyScrollStyle` + `PageCardStackStyle` 卡片堆。
- **列表型页面**（输入法切换 / 程序关联 / 桌面分区）：主体是 `PageBodyGridStyle`，
  行 0 是 `PageToolbarFirstStyle` 工具条，行 1 是 `PageWorkAreaStyle` 双栏工作区
  （列表 + 右侧编辑面板），**列表高度由行撑满，不写 MinHeight / MaxHeight**。
- 每个列表都要有空状态提示：列表容器里叠一个 `PageEmptyHintTextStyle` 的 TextBlock，
  在 `UpdateXxxCount()` 里跟着条数切换可见性。

### 5.2 间距（4px 栅格）

| 场景 | 值 | 来源 |
| --- | --- | --- |
| 页面四周留白 | `40,24,40,32`（窄窗口 `20,16,20,20`） | `PageRootStyle` / `FeaturePagesHost.ApplyDensity` |
| 内容列最大宽度 | `1000`，窗口更宽时**居中** | `FeaturePagesHost` 的列定义 + `PageColumns_SizeChanged` |
| 标题 → 副标题 | 6 | `PageHeaderTextStackStyle` |
| 表头 → 主体（主体**第一块**就是小节标题 + 列表） | 40 | `PageHeaderStyle` |
| 表头 → 主体（主体第一块是卡片） | 64 | `PageHeaderLooseStyle` |
| 二级小节之间 | 32 | `PageToolbarStyle` / `PageSectionHeaderTextStyle` |
| 小节标题 → 内容 | 12 | 同上 |
| 卡片之间 | 4 | `PageCardStackStyle` |
| 表单字段组之间 | 16 | `PageFormStackStyle` |
| 字段标签 → 输入框 | 6 | `PageFieldStackStyle` |
| 一行里的控件之间 | 8 | `PageInlineControlsStyle` |

层级靠间距体现：**64 / 40（表头 → 主体） > 32（二级小节） > 16（组内） > 4（同类卡片）**。
主体里的第一个工具条用 `PageToolbarFirstStyle`，避免和表头的间距叠加。

看的是**主体第一块内容**，不是整页有没有列表：桌面分区页有分区列表，但列表上面先放了
「呼出快捷键」「卡片布局」两张卡片，所以它和纯卡片页一样用 64。

表头与小节标题的对齐方式不同，别改混：
- 页面表头（标题 + 副标题）**顶对齐**（`PageHeaderTextStackStyle` 的 `VerticalAlignment=Top`），
  和下面第一个模块的上边缘成一条线；
- 小节标题（"分区""关联""规则"）在工具条里**上下居中**，因为它要和右侧命令栏对齐。

### 5.3 页面宽度与左边距（踩过的坑，别改回去）

限宽写在 **`FeaturePagesHost` 根 Grid 的列定义**里：列 0 是左侧留白（代码在 `SizeChanged`
里算 `(可用宽度 - 1000) / 2`，让内容在宽窗口下居中），列 1 是内容列 `Width="*" MaxWidth="1000"`。
页面样式只负责铺满内容列。实测数据（最大化 2576px 窗口）：

- 页面上写 `HorizontalAlignment="Stretch" + MaxWidth`：WinUI 按**元素自身内容的宽度**算
  居中偏移，各页标题左边距变成 807 / 883 / 889 / 910 / 942 / 946 / 1013，**最多差 206px**。
- 改成 `HorizontalAlignment="Left" + MaxWidth`：左边距统一了，但页面宽度缩到内容宽度
  （308 ~ 720 不等），卡片右边缘参差不齐。
- 用 Grid 列限宽：列宽由栅格算出、从左边排，与内容无关。八个页面实测左边距全部相同。

窄窗口会掩盖这个问题，**验证时必须最大化窗口再逐页切换**。

### 5.3.1 窄窗口（紧凑模式）

`FeaturePagesHost.PageColumns_SizeChanged` 在宽度 < 760 时切换紧凑排版：页面留白收到 20，
命令栏切成 `DefaultLabelPosition="Collapsed"`（只留图标）。标题栏两级降级
（`MainWindow.ApplyTitleBarDensity`）：< 720 收起"WinTools"文字，< 620 搜索框收成一个放大镜
按钮（点一下展开成输入框），否则标识、搜索框、窗口按钮会叠在一起。

侧栏由 NavigationView 自己在 640 / 840 两个阈值收放，收起态有两个坑：
- 导航项内容不要写 `MinWidth`，否则面板收起时标签被裁掉；
- 图标居中靠的是 `App.xaml` 里那行 **`<Thickness x:Key="NavigationViewItemButtonMargin">0,2</Thickness>`**。
  WinUI 模板的 `LayoutRoot` 自带 `Margin="4,2"`，那 4px 会把收起态的图标整体推右；
  清掉水平部分后图标正好落在面板正中，药丸的内缩改由 `NavigationViewItem` 样式的 `Margin="4,2"` 提供
  （那个 Margin 只挪药丸，不挪图标）。
  实测（收起态，面板 x=8~56、中心 32）：改之前图标中心 35.5，改之后 31.5。
- 别在这三个地方浪费时间：改导航项 `Margin`、改 `CompactPaneLength`（48→56 时图标跟着平移，
  偏移量不变）、覆盖 `NavigationViewCompactPaneLength` 资源——三者都试过，图标位置纹丝不动。
- `CompactPaneLength` 保持 48、导航项 `Margin` 保持 `4,2`。曾经改成 58 / `8,2`，
  项目盒子比图标列还窄，选中药丸会被裁。

### 5.4 颜色分层：全窗口只有两种颜色

| 层 | 来源 | 深色实测值 |
| --- | --- | --- |
| 窗口材质 | `MicaBackdrop`（`ApplyWindowBackdrop`） | 随壁纸 |
| 外壳层：标题栏 + 侧栏 + 内容区背后 | `GetCodexShellBrush`，由 `ShellRoot` 统一铺满 | `#1C222F` |
| 内容层：子页面 | `GetCodexContentBrush`，叠在外壳层上的半透明黑 | `#13161E` |

- 内容层是**盖在外壳层上的一层半透明黑**（浅色主题是白），所以它永远等于「外壳色再压暗
  一档」，色相一致、Mica 依旧透得上来。调深浅只改 `WindowHelper.ContentOverlayDarkAlpha`。
- 标题栏和导航面板背景必须是 `Transparent`，由 `ShellRoot` 一层铺满。
  **不这么做，内容区左上角的圆角缺口就会露出第三种颜色**（曾经是一块近黑的楔形）。
- 不要把内容区改回写死的近黑色（`#181818` / `#171717`），那会和侧栏彻底割裂。

### 5.5 圆角

| 用途 | 值 | 来源 |
| --- | --- | --- |
| 内容区（唯一的大圆角，只有左上角） | 12 | `MainWindow.xaml` 的 `ContentFrame` |
| 卡片、列表容器、编辑面板 | 10 | `PageCardStyle` / `PageCardCornerRadius` |
| 卡片内部的小块（磁贴、标签、拖放区、行内小按钮） | 8 | `PageTileStyle` / `PageInnerCornerRadius` |
| 普通控件（按钮、输入框） | 10 | `App.xaml` 的 `ControlCornerRadius` |
| 浮层窗口 | 12 | `App.xaml` 的 `OverlayCornerRadius` |

`NavigationViewContentGridCornerRadius` 必须保持 `0`：圆角只由 `ContentFrame` 裁一次，
裁两次会在拐角留下缝。侧栏左下角那个圆角是窗口自身的 DWM 圆角，不是控件画的。

### 5.5.1 快捷键与元素取舍

- **所有快捷键输入框都放在页面最外层的卡片里**，不要折叠进 `SettingsExpander`——
  快捷键是用户最常改的东西，藏一层就要多点一次。
- 每加一个控件都要能回答"没有它用户会怎样"。纯说明性的卡片、只显示不操作的状态文字，
  合并进页面副标题或工具条的计数里，不要单独占一张卡片。
- 数值设置只保留会改变布局结果的，并且**同一维度只给一个输入框**：卡片离屏幕边缘、卡片之间、
  每行图标数、暂存窗口水平 / 垂直偏移、快捷面板水平 / 垂直留白。
  快捷面板的留白配置里仍是四个方向（`QuickPanelPaddingTop/Bottom/Left/Right`），
  界面上只暴露水平 / 垂直两个，写回时左右、上下各取同一个值。

### 5.6 其它硬性约定

- 全部文案使用简体中文。
- 不要设 `Application.RequestedTheme`，主题走 `ThemeService` 应用到各窗口根元素。
- `ContentDialog` 必须指定 `XamlRoot`；每个窗口设置 `Assets\AppIcon.ico`。
- 新窗口实现 `IUiStyleShell`、注册到 `UiStyleService`，用 `ApplyUiStyleSurfaces()` 刷新表面。
- 必须以普通用户权限运行：管理员进程收不到资源管理器的文件拖放。

### 5.7 改完 UI 的检查清单

- [ ] 页面用的是 `PageRootStyle` + `PageHeaderStyle`（表头 Auto，主体 `*`）
- [ ] 页面里没有写死的 `Margin` / `Padding` / `CornerRadius` / `FontSize`，也没有 `MaxWidth`
- [ ] 列表撑满主体高度，且有空状态提示
- [ ] 圆角：内容区 12（仅左上）、卡片 10、内部小块 8
- [ ] **窗口最大化后逐页切换**，标题左边距、顶部位置不跳动
- [ ] 内容区颜色只比外壳深一档，圆角缺口没有第三种颜色

---

## 6. WinUI 3 注意事项

- 显示前用 DWM Cloak，渲染完两帧再解除，避免首帧白闪；同时关掉系统过渡动画。
- 无标题栏弹窗用扩展内容标题栏，保留 DWM 边框与圆角。
- 贴合内容的小窗口用 Win32 调尺寸，并处理最小跟踪尺寸。
- 自适应尺寸先 `Measure` 再用 `DesiredSize`，不要拿当前 `ActualWidth/Height` 当期望尺寸。
- 标题栏左右系统占位统一由 `WindowHelper.HookTitleBarPadding` 处理。
- 主窗口用原生标题栏 + DWM 非客户区拖动，自绘标题栏会掉帧。
- 拖动过程中不要在光标正下方新建 / 显示窗口，系统会切换拖放目标并重绘预览。

---

## 7. 功能实现要点

### 桌面分区（`src/WinTools/Desktop*.cs`）

- **`DesktopCollectService`**：扫描桌面，把非隐藏 / 非系统项按分区规则**物理移动**到
  `%USERPROFILE%\WinTools\<分区名>\`，日志写 `collected.json`，可「全部还原」原样送回。
- **`DesktopZoneMatcher`**：显式文件名 → 名称关键词 → 游戏协议 → 扩展名 / 文件夹 → 兜底分区，
  五级优先级。关键词以 `.` 开头视为扩展名；含 `steam://` 等协议的关键词优先匹配 `.url` 目标。
- **`DesktopCardManager`**：一个分区一张 `DesktopCardWindow`。`Sync` = 全量收纳 + 卡片增删；
  `SyncContent` 只刷内容（FileSystemWatcher 触发，防抖动重复 CollectAll）。
- **`DesktopCardWindow`**：Mica、可拖动缩放，`PlaceAt` 收 DIP 坐标。图标走 `SHGetFileInfo`
  + `BitmapImage`；快捷方式先用 `WScript.Shell` 解析目标——统一在单条后台 STA 队列里做。
- **默认分区**：架构版本 3 的 10 个大类。旧 7 类只迁移一次，用户自建分类不覆盖。
- **DeskBox 旧数据**：首次同步导入 `%USERPROFILE%\DeskBox\我的桌面` 与
  `%USERPROFILE%\DeskBox\DeskBox` 顶层项目并写入 `collected.json`，「全部还原」仍能回原路径。

### 分区归属：拖放换区与预设文件

- **拖放换区**：卡片之间可以直接拖图标。`DesktopCardWindow.ItemsGrid_Drop` 调
  `DesktopCollectService.MoveItemToZone` 做物理移动 + 更新 `collected.json`，
  然后抛 `ItemMovedIn`，由 `DesktopCardManager.Card_ItemMovedIn` 把这次归属写进
  **目标分区的 `Items` 显式清单**（并从其它分区的清单里摘掉）。
  **这一步不能省**：`Sync()` 里的 `ReclassifyManagedItems` 会按关键词重新归类，
  没写进显式清单的话，用户拖过去的图标下次同步就自己跑回来了
  （`DesktopZoneMatcher` 的第一优先级就是显式文件名）。
  管理器随后抛 `ZonesChangedExternally`，设置页重新载入分区列表，
  否则页面里的旧副本会在下次保存时把这次拖放覆盖掉。
- **预设文件**：分区工具条上的「保存到预设」把当前分区列表（name / keywords / items）
  导出成独立 JSON；「从预设恢复」读回来替换当前分区，并用 `DesktopZoneMatcher`
  把已收纳项目按新分区重新分配。与「恢复默认分区」的区别：后者用的是内置推荐分类。

### 多屏拓扑记忆（`Services/CardLayoutCache.cs`）

每种屏幕组合（主屏标记 + OuterBounds）存一份「分区名 → 位置 / 尺寸」快照到
`%LOCALAPPDATA%\WinTools\card-layout-cache.json`；命中就直接 `PlaceAt`，未命中走默认算法。

- `TopologyKey.Get()` 从主窗口 `DisplayArea` 拼 key（避免 `DisplayArea.FindAll()` 的
  `InvalidCastException`）。
- 写入用**无 BOM** UTF-8（`System.Text.Json` 不接受 BOM），先写 `*.tmp` 再 `File.Replace`。

### 桌面卡片的出现方式

启动时**不要**边加载边显示，否则用户会看到一批黑方块逐个变成卡片。正确顺序在
`DesktopCardManager.SyncCards`：

1. 每张卡片 `PresentCloaked()`——窗口 Show 但保持 DWM Cloak，布局 / Mica / 图标都在后台合成；
2. `Layout(...)` 排好位置；
3. `RevealAsync(i * 40)` 依次揭示：先 `await` 该卡片的图标任务（最多 1.5s 超时），
   再等两帧后解除 Cloak，最后跑 180ms 的淡入 + 0.96→1 放大动效。

实测（9 张卡片）：t=4s 时 8 张 `visible=True cloaked=1`（后台加载中，屏幕上看不到），
t=8s 时全部 `cloaked=0`。

### 总开关的语义

页面表头的总开关 = **整页功能的开关**，它必须真的把下面所有东西都关掉。
`快捷设置` 踩过这个坑：总开关只注销了面板热键，`RegisterAllHotkeys` 里的功能热键
（网络切换 / 声音控制）还照常注册，于是"已关闭"的页面在后台仍然响应 Ctrl+Win+N。
现在总开关关闭时会跳过功能热键注册，并把功能区置灰（`SyncQuickFeaturesEnabledState`）。
新增带总开关的页面时，照这个标准检查一遍。

### 侧栏状态圆点

`UpdateNavStatusIndicators()` 里每个圆点**只反映该页的总开关**。不要再写"或者某个子功能还开着"
这类复合条件——关掉「快捷设置」后面板整个不响应，圆点却还亮着，用户会以为没关掉。
实测：同一像素点从 `#4CC2FF`（开）变成 `#777A83`（关）。

### 托盘菜单

托盘图标贴着任务栏，右键菜单必须向上弹：`TrayIcon.BuildContextMenu` 里设
`Placement = FlyoutPlacementMode.TopEdgeAlignedRight`。实测菜单占 y=1098~1228，
点击点 y=1235，完全在上方。

### 启动路径

`App.OnLaunchedCoreAsync` 不再串行 await 后台服务：`InitPerAppIme` / `InitDesktopClick` /
`DesktopContextMenuService.SetEnabled` 丢 `Task.Run`；必须在 UI 线程的 `EnsureTrayIcon` /
`InitFloatingStash` 用 `TryEnqueue` 排队。启动完成从 2.55s 降到约 0.83s。

### 错误日志

`Services/ErrorReporter.cs` 是统一入口，写 `%TEMP%\WinTools-error-yyyyMMdd.log`，进程内互斥。

---

## 8. 主要文件

| 文件 | 职责 |
| --- | --- |
| `App.xaml/.cs` | 入口、单实例、主窗口与后台服务初始化 |
| `MainWindow.xaml/.cs` | 外壳、导航、全局快捷键；`MainWindow.PageControls.cs` 按名取子页控件 |
| `FeaturePagesHost.xaml/.cs` | 全部功能子页面；事件转发给 `MainWindow` |
| `Styles/PageStyles.xaml` | UI 令牌与页面样式（唯一真源） |
| `QuickSettingsPopupWindow` | 快捷设置悬浮面板 |
| `FloatingStashWindow` | 文件悬浮暂存；关窗清空本次记录 |
| `GlobalDragWatcher` | 普通鼠标 + 三指全局拖动检测 |
| `ProgramDropWindow` | 拖放文件时的程序选择窗口 |
| `DesktopClickService` | 桌面空白单击切换「显示桌面 / 恢复窗口」 |
| `DesktopIconPositionService` | 图标正常参与拖放，结束后恢复分区位置 |
| `DesktopContextMenuService` | 处理「整理桌面」启动参数、清理旧注册表菜单 |
| `ThreeFingerDragService` | 精确触控板三指接触合成原生拖动 |
| `ThreeFingerCalibrationWindow` | 全屏三档速度校准，仅在基础曲线 ±20% 内微调 |
| `Config.cs` | 配置结构与读写 |
| `WindowHelper.cs` | 窗口外观、标题栏、Mica 分层颜色、尺寸适配 |
| `UiStyleService.cs` / `ThemeService.cs` | Mica 风格 / 浅色深色跟随系统 |
| `AutostartService.cs` | 开机自启（HKCU Run） |

改文件结构、发布位置或关键窗口行为时，同步更新本文件。
