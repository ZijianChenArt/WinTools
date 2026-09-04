# WinTools 维护手册

基于 .NET 10 + WinUI 3 的 Windows 桌面工具集：桌面分区卡片、悬浮暂存、三指拖拽、
按程序切换输入法、程序关联与点击桌面。

> **这是仓库里唯一的说明文件**（含 AI 协作者）。
> 改 UI 之前必须读第 5 节，改桌面分区之前必须读第 7 节，这两节的规则是硬约束，不是建议。
> 凡是写着"踩过的坑""别改回去"的条目，都是已经用实测推翻过一次的方案。

---

## 1. 项目结构

```text
WinTools/
├─ WinTools.sln
├─ global.json                     # 锁定 .NET SDK 10.0.400
├─ README.md                       # 本文件：唯一的文档入口
├─ src/
│  ├─ WinTools/                    # 主程序
│  │  ├─ Styles/PageStyles.xaml    # ★ 全部 UI 令牌与页面样式的唯一真源
│  │  ├─ MainWindow.xaml           # 外壳：标题栏 + 导航 + 内容框
│  │  ├─ MainWindow.xaml.cs        # 外壳逻辑；功能页逻辑在下面几个分部文件里
│  │  ├─ MainWindow.*.cs           # Hotkeys / DesktopCards / Stash / Associations / Ime
│  │  ├─ FeaturePagesHost.xaml     # 全部功能子页面
│  │  ├─ Desktop*.cs               # 桌面分区：收纳 / 匹配 / 卡片 / 点击桌面
│  │  └─ Services/                 # 布局缓存、错误日志、设置中心、窗口注册表
│  └─ WinToolsExplorerCommand/     # C++ 资源管理器右键菜单扩展（IExplorerCommand）
├─ scripts/
│  ├─ Publish-Release.ps1          # 便携版发布
│  └─ Start-CodexWhenOnline.ps1    # 与本程序无关的网络辅助脚本
└─ artifacts/release/win-x64/      # 当前自包含发布产物（约 225 MB）
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

---

## 3. 发布

先关掉正在运行的 WinTools，否则文件被占用。脚本自己会先用 MSBuild 编原生扩展，
再 `dotnet publish`，最后原子替换 `artifacts/release/win-x64`：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1 -Runtime win-x64
```

**每次修改程序后都必须完成构建与发布，并重新打开
`artifacts/release/win-x64/WinTools.exe`。** 不要只留下 Debug 产物，也不要让用户手动寻找或
重启新版；发布前先关闭占用发布目录的旧进程，发布成功后立即启动正式版。

发布脚本生成自包含版本，随包携带 .NET 10 运行时与 Windows App Runtime，目标机无需另装。
**不要**开启 `PublishTrimmed` / `PublishReadyToRun`（WinUI 3 会崩）。

---

## 4. 开机自启与全局快捷键

写在当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `WinTools` 值，
由 `AutostartService` 管理，内容是**当时那个 exe 的完整路径**（带引号）。

- 换了发布目录就必须重设，否则注册表指向不存在的旧路径，开机静默失败。
- 设置页的开关是拿注册表值和「当前正在运行的 exe」比对，所以从 `bin\Debug` 启动时会显示关闭；
  正式使用请从 `artifacts/release/win-x64/WinTools.exe` 启动后再开。

全局快捷键由 WinTools 进程通过 `RegisterHotKey` 监听（注册与窗口过程子类化在
`MainWindow.Hotkeys.cs`）：主窗口关闭到托盘后仍然有效，但从托盘选择「退出 WinTools」或
结束进程后不可能继续响应。需要登录后始终可用全局快捷键时，必须开启本节的开机自启让程序
常驻托盘；不要为此额外安装服务或常驻辅助进程。

手动修注册表：

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

- **设置型页面**（三指拖拽 / 悬浮暂存 / 点击桌面 / 设置）：主体是
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
命令栏切成 `DefaultLabelPosition="Collapsed"`（只留图标）。标题栏只保留侧栏开关、应用图标
与“WinTools”文字，不再放置搜索框，因此窄窗口无需额外的标题栏搜索降级逻辑。

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
- **子页面背景深度固定为 40%**，不再在设置页提供滑块；它只控制右侧功能页面叠在外壳背景
  上的遮罩浓度，不得改变 Mica 参数、桌面分区、弹窗或侧栏。
  浅色桌面分区固定使用白色半透明遮罩，不能沿用深色底色。

### 5.5 圆角

| 用途 | 值 | 来源 |
| --- | --- | --- |
| 内容区（唯一的大圆角，只有左上角） | 12 | `MainWindow.xaml` 的 `ContentFrame` |
| 卡片、列表容器、编辑面板 | 10 | `PageCardStyle` / `PageCardCornerRadius` |
| 卡片内部的小块（磁贴、标签、拖放区、行内小按钮） | 8 | `PageTileStyle` / `PageInnerCornerRadius` |
| 普通控件（按钮、输入框） | 10 | `App.xaml` 的 `ControlCornerRadius` |
| 浮层窗口 | 12 | `App.xaml` 的 `OverlayCornerRadius` |

所有可点击元素的悬浮、按下和焦点底框也必须服从同一层级：普通按钮、AppBar、菜单项、
下拉按钮和超链接用 10px；`ListViewItem`、`GridViewItem`、`ComboBoxItem` 等卡片内部选择项
用 8px；Tooltip、Flyout 等浮层用 12px。对应资源在 `App.xaml` 全局覆盖，禁止各页面复制模板
或留下 WinUI 默认的近方形 3～4px 圆角。仅设置 `ControlCornerRadius` 不足以覆盖
`AppBarButton`，还要给 `Button`、`AppBarButton`、`ToggleButton`、`DropDownButton` 与
`RepeatButton` 设置隐式 `CornerRadius` 样式。页面 `CommandBar` 里的命令项还必须显式使用
`PageAppBarButtonStyle`：`CommandBar` 会接管命令项的默认样式，只靠全局隐式样式会让“新建”等
按钮在悬浮时退回直角底框。

`NavigationViewContentGridCornerRadius` 必须保持 `0`：圆角只由 `ContentFrame` 裁一次，
裁两次会在拐角留下缝。侧栏左下角那个圆角是窗口自身的 DWM 圆角，不是控件画的。

桌面分区卡片同样只能有一层外圆角：由 DWM 绘制 12px 窗口轮廓；根 `CardSurface` 必须保持
`CornerRadius="0"`、`BorderThickness="0"`，不得再叠一层 XAML 圆角或描边。DWM 边缘颜色要与
卡片表面 RGB 一致，不能用透明边框，否则部分多屏 / DPI 组合会在顶部圆角端点留下亮点。
卡片内部磁贴仍用 8px。

### 5.5.1 快捷键与元素取舍

- **所有快捷键输入框都放在页面最外层的卡片里**，不要折叠进 `SettingsExpander`——
  快捷键是用户最常改的东西，藏一层就要多点一次。
- 每加一个控件都要能回答"没有它用户会怎样"。纯说明性的卡片、只显示不操作的状态文字，
  合并进页面副标题或工具条的计数里，不要单独占一张卡片。
- 数值设置只保留会改变布局结果的，并且**同一维度只给一个输入框**：卡片离屏幕边缘、卡片之间、
  每行图标数、暂存窗口水平 / 垂直偏移。

### 5.6 其它硬性约定

- 全部文案使用简体中文。
- 不要设 `Application.RequestedTheme`，主题走 `ThemeService` 应用到各窗口根元素。
- `ContentDialog` 必须指定 `XamlRoot`；每个窗口设置 `Assets\AppIcon.ico`。
- 新窗口实现 `IUiStyleShell` 并注册到 `UiStyleService`，用 `ApplyUiStyleSurfaces()` 刷新表面。
  **自己管理窗口背景的窗口（桌面卡片）只能用 `RegisterShell()`**，理由见 7.4。
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

## 7. 桌面分区卡片

### 7.1 数据流与收纳

- **`DesktopCollectService`**：扫描桌面，把非隐藏 / 非系统项按分区规则**物理移动**到
  `%USERPROFILE%\WinTools\<分区名>\`，日志写 `collected.json`，可「全部还原」原样送回。
  每移动一项立刻写一次日志——进程被强杀也不会留下无法还原的文件，这个代价是有意付的。
- **`DesktopZoneMatcher`**：显式文件名 → 名称关键词 → 游戏协议 → 扩展名 / 文件夹 → 兜底分区，
  五级优先级。关键词以 `.` 开头视为扩展名；含 `steam://` 等协议的关键词优先匹配 `.url` 目标。
- **`DesktopCardManager`**：一个分区一张 `DesktopCardWindow`。三个同步入口分工明确：
  `Sync()` 全量收纳 + 卡片增删，同步执行，供「立即同步」这类显式操作用；
  `SyncAsync()` 同样的流程但磁盘 I/O 放 `Task.Run`，监视器触发一律走它；
  `SyncContent()` 只刷卡片内容与集合，不做物理搬运。
- **默认分区**：当前架构版本 8，共 9 个大类；"开发与 AI"统一收纳开发工具及常见 AI 软件。
  旧版"开发与效率"升级时必须同步改名托管目录和 `collected.json`，不能只修改显示名称。
  主窗口和卡片创建前调用 `ReclassifyManagedItems`，让已经收纳的 AI 软件也立即迁入新分区；
  用户显式拖动指定的项目仍以 `Items` 规则优先，不覆盖用户选择。
- **还原到桌面**：日志来源若是公共桌面（`CommonDesktopDirectory`），普通权限通常无法写回，
  必须改为还原到当前用户桌面并处理重名；用户桌面及其它来源仍回到原路径。
- **DeskBox 旧数据**：首次同步导入 `%USERPROFILE%\DeskBox\我的桌面` 与
  `%USERPROFILE%\DeskBox\DeskBox` 顶层项目并写入 `collected.json`，「全部还原」仍能回原路径。

### 7.2 文件监视的职责划分（改之前先看这里）

两个监视器管两件事，**不要互换**：

| 监视对象 | 触发 | 做什么 |
| --- | --- | --- |
| 桌面目录 | 650ms 防抖 → `SyncAsync()` | 完整同步：扫描桌面 → 物理搬入托管分区 → 重建卡片 |
| 托管分区目录（每张卡片一个） | 250ms 防抖 → `SyncContent()` | 只刷新卡片内容与集合，不做物理搬运 |

曾经是反的：桌面新增文件只调 `SyncContent`（不搬文件，等于自动收纳失效），而托管目录一有
动静就在 UI 线程上跑一遍全量 `Sync()`，一次拖放会连着触发 Created / Renamed / Deleted
好几次全量收纳。

`DesktopCardWindow.RefreshContent` 按路径复用旧的 `CardItem`，保留已加载的图标；图标解析
结果另有一层进程内缓存（键为路径 + 最后写入时间，上限 512 条）。托管目录动一个文件不该让
整张卡片重新走一遍 Shell / `WScript.Shell` 图标解析（单个 `.lnk` 几十毫秒）。

### 7.3 分区归属：拖放换区与预设文件

- **分区工具栏**：常用区只保留「新建」；预设导入导出、手动同步、恢复默认和全部还原
  属于低频操作，统一放在右侧的「更多」菜单，不要再铺成一整排按钮。这里使用
  `PageToolbarButtonStyle` / `PageToolbarIconButtonStyle` 的普通按钮，不使用 `CommandBar`
  自动生成的 `MoreButton`；后者会采用直角、顶部对齐的系统内部模板。工具栏按钮常态下
  背景与边框必须透明，只显示文字 / 图标；鼠标悬浮或按下时才显示 10px 圆角底框。
- **分区编辑**：右键分区卡片只显示「重命名 / 删除」，不使用右侧编辑面板，也不在工具栏
  重复提供编辑和删除。私人使用模式不显示文件清单或自定义关键词规则；分区中的项目一律
  在桌面卡片之间直接拖动。重命名必须同时迁移
  `%USERPROFILE%\WinTools\<分区名>` 托管目录及 `collected.json` 中的记录，不能让文件失联。
- **分区列表布局**：设置页中的分区卡片不得固定宽度。`Zones_SizeChanged` 按列表可用宽度
  动态计算 2～4 列，并把宽度均分给 `ItemsWrapGrid`；窄窗口两列，空间增加后自动变为三、四列。
  计算时必须为滚动条、边框和像素取整预留安全宽度，并直接更新当前面板；不要把每次
  `SizeChanged` 都异步排队，否则拖动窗口边框时会因溢出或旧尺寸回放短暂闪成一列。
- **拖放换区**：卡片之间可以直接拖图标。`DesktopCardWindow.ItemsGrid_Drop` 调
  `DesktopCollectService.MoveItemToZone` 做物理移动 + 更新 `collected.json`，
  然后抛 `ItemMovedIn`，由 `DesktopCardManager.Card_ItemMovedIn` 把这次归属写进
  **目标分区的 `Items` 显式清单**（并从其它分区的清单里摘掉）。
  **这一步不能省**：`ReclassifyManagedItems` 会按关键词重新归类，没写进显式清单的话，
  用户拖过去的图标下次同步就自己跑回来了（`DesktopZoneMatcher` 的第一优先级就是显式文件名）。
  管理器随后抛 `ZonesChangedExternally`，设置页重新载入分区列表，
  否则页面里的旧副本会在下次保存时把这次拖放覆盖掉。
- **预设文件**：分区工具条上的「保存到预设」把当前分区列表（name / keywords / items）
  导出成独立 JSON；「从预设恢复」读回来替换当前分区，并用 `DesktopZoneMatcher`
  把已收纳项目按新分区重新分配。与「恢复默认分区」的区别：后者用的是内置推荐分类。

### 7.4 卡片窗口的材质（Mica）

卡片用的是**显式 `MicaController`**，不是 `Window.SystemBackdrop`：系统默认的 `MicaBackdrop`
在窗口失焦后会变成不透明灰，而分区卡片永远不激活（所有 `SetWindowPos` 都带 `SWP_NOACTIVATE`），
用默认背景等于永远是灰的。由此派生两条硬性规则：

- 任何人只要给卡片窗口设一次 `SystemBackdrop = new MicaBackdrop()`，WinUI 就会抢走合成目标，
  卡片的 Mica **直接消失**。所以卡片只用 `UiStyleService.RegisterShell()` 订阅样式刷新，
  **不能**用 `Register(Window)`——后者会调 `ApplyWindowBackdrop` 替它设背景。
- `SystemBackdrop = null` 只能在首次创建并连接显式 `MicaController` 之前执行一次。控制器已经连接后
  再清空会让合成目标失效，但 `_micaAttached` 仍保持为真；此后切换主题时卡片会直接露出黑色底层。
- `WS_EX_LAYERED` 与 Mica **不能共存**：常驻分层样式会让卡片彻底失去材质（整窗变成纯色）。
  淡入淡出要用分层透明度，就只能在动画期间临时加、动画一结束立刻摘（见 7.5）。

### 7.5 出现与收起动画

启动时**不要**边加载边显示，否则用户会看到一批黑方块逐个变成卡片。正确顺序在
`DesktopCardManager.SyncCards`：

1. 每张卡片 `PresentCloaked()`——窗口 Show 但保持 DWM Cloak，布局 / Mica / 图标都在后台合成；
2. 等待每张卡片的 XAML `Loaded`，让多屏 DPI 信息就绪，重新应用尺寸后再 `Layout(...)`；
3. 等待全部卡片的图标任务（单卡最多 1.5s 超时）后**一起**揭示，再等两帧解除 Cloak 并淡入。

不要再逐张错开揭示，那是旧版"逐个加载"的观感。快捷键呼出与启动揭示共用同一个
`PlayRevealAnimation`，两处观感必须完全一致。

设置页关闭后再次开启分区时，复用的旧窗口也必须调用 `PresentCloaked()`：先临时进入
`WS_EX_LAYERED` 并把整窗 alpha 压到 0，再 `Show()`，随后与新窗口一起走
`FinalizeInitialLayoutAndRevealAsync`。关闭功能时先播放同一套 200ms 整窗渐隐，完成后才
`AppWindow.Hide()`；不得直接 Show/Hide。用 transition version 防止用户快速反向切换时，
上一轮淡出在最后误把新一轮窗口隐藏。

动画本身：**淡入 300ms 减速（`1-(1-t)³`），收起 200ms 加速（`t³`）**，做的是**窗口级透明度**
（`WS_EX_LAYERED` + `SetLayeredWindowAttributes`，16ms 的 `DispatcherQueueTimer` 推 alpha），
收起动画播完才取消置顶并降到后台，不能先改 Z 序再播。踩过的坑：

- 动 `ShellRoot.Opacity` / Storyboard **完全没用**。卡片屏幕上的颜色主要由窗口的 Mica 背景
  画出来，它在 XAML 内容之下，XAML 透明度碰不到它；把 ShellRoot 淡到 0，屏幕上仍是一个完整的
  Mica 矩形，肉眼就是"动画失效"。同理缩放也只缩内容，Mica 外框尺寸不变，看起来像卡片在固定
  边框里"往里缩"。
- 抬升 / 解除 Cloak **之前**先把 alpha 压到 0（`PrepareFadeIn`）。反过来窗口会先以不透明状态
  露一帧，观感是"按下快捷键先闪一下，然后才慢慢淡入"。
- 分层样式加在动画开始、摘在动画结束：淡入在动画完成时摘，收起在**降完 Z 序之后**再摘，
  否则摘掉的瞬间会闪一下完整卡片。
- 如果以后要改回 XAML 动画：用 `SplineDoubleKeyFrame` 复刻 `cubic-bezier` 时不要插
  `DiscreteDoubleKeyFrame` 当起点，离散帧会把整条动画降级成 dependent animation 被直接跳过。

实测曲线（截屏采样卡片区域平均亮度）：呼出 `48 → 16.5 → 29.1 → 39.9 → 46.5 → 48`（约 280ms），
收起 `50.3 → 48.9 → 45.1 → 40.3 → 33.3`（约 250ms）。

### 7.6 呼出、置顶与自动收起

桌面分区快捷键（默认 `Ctrl+Alt+Shift+F12`，避免占用常见的单键 F1～F12；加载配置时只把历史
默认值 `Ctrl+Alt+D` 迁移到新值，不覆盖用户自定义）无论当前在桌面还是其它应用都响应：

- 第一次按用 `HWND_TOPMOST` 呼出并**保持置顶**；第二次先 `HWND_NOTOPMOST` 取消置顶，
  再 `HWND_BOTTOM` 降回后台。调用均带 `SWP_NOACTIVATE`，不抢输入焦点。
  不能在第一次调用里立即取消置顶，否则当前前台窗口会马上重新盖住卡片，视觉上等于没响应。
- `RaiseToFront` 置顶后必须**回读 `WS_EX_TOPMOST` 自检**：长时间运行后个别卡片窗口会进入
  「SetWindowPos 返回成功但扩展样式设不上」的状态（外部进程调用同样失败，重启程序才恢复），
  表现为**某一个分区一直不出现**。自检失败时先 NOTOPMOST 再 TOPMOST，仍失败就 Hide/Show
  一次复位，最后还失败才写错误日志。
- **自动收起**：呼出时记录当时的前台 HWND，并以 60ms 周期观察，满足其一即收起——前台窗口
  换成了别的窗口，或者用户在**卡片以外**的任何位置按下了鼠标（`GetAsyncKeyState` 的
  "自上次查询以来按下过"标志 + 光标命中测试）。只比前台 HWND 是不够的：呼出后最常见的动作
  就是点回呼出时那个窗口，前台 HWND 根本没变，卡片会一直盖着收不起来。反过来，前台切到
  卡片自身（点卡片空白、拖图标）不算"点到别处"。进入监测前要先把遗留的点击标志读掉，
  否则第一个 tick 就把刚呼出的卡片收回去。再次按快捷键仍可手动收起，计时器必须同步停止。

### 7.7 多屏拓扑记忆（`Services/CardLayoutCache.cs`）

每种屏幕组合（主屏标记 + OuterBounds）存一份「分区名 → 位置 / 尺寸」快照到
`%LOCALAPPDATA%\WinTools\card-layout-cache.json`；命中就直接 `PlaceAt`，未命中走默认算法。

- `TopologyKey.Get()` 从 Windows 主显示器的 `DisplayArea` 拼 key（避免
  `DisplayArea.FindAll()` 的 `InvalidCastException`）。分区卡片固定在系统设置标记的主显示器，
  不得按新建窗口偶然落到的显示器选择布局目标。
- 写入用**无 BOM** UTF-8（`System.Text.Json` 不接受 BOM），先写 `*.tmp` 再 `File.Replace`。

---

## 8. 其它功能要点

### 侧栏状态圆点

`UpdateNavStatusIndicators()` 里每个圆点**只反映该页的总开关**。不要再写"或者某个子功能还开着"
这类复合条件，避免页面关闭后圆点却还亮着。
实测：同一像素点从 `#4CC2FF`（开）变成 `#777A83`（关）。

### 托盘菜单

托盘图标贴着任务栏，右键菜单必须向上弹：`TrayIcon.BuildContextMenu` 里设
`Placement = FlyoutPlacementMode.TopEdgeAlignedRight`。实测菜单占 y=1098~1228，
点击点 y=1235，完全在上方。

### 启动路径

`App.OnLaunchedCoreAsync` 不串行 await 后台服务：`InitPerAppIme` / `InitDesktopClick` /
`DesktopContextMenuService.SetEnabled` 丢 `Task.Run`；必须在 UI 线程的 `EnsureTrayIcon` /
`InitFloatingStash` 用 `TryEnqueue` 排队。桌面卡片由 `App` 在主窗口首帧完成后才初始化，
避免多窗口创建抢占首屏。

---

## 9. 跨模块硬性规则

### 低级鼠标钩子的红线

`WH_MOUSE_LL` 回调是全系统同步的：这里每多花 1ms，所有应用的每次点击就慢 1ms，超过
`LowLevelHooksTimeout`（默认 300ms）Windows 会直接丢弃这个钩子的事件。所以
`DesktopClickService.HookCallback` 里**只记坐标**，"是否点在桌面空白处"（要 `OpenProcess` /
`WriteProcessMemory` / 跨进程发消息给 Explorer）一律扔线程池。跨进程发消息必须用
`SendMessageTimeout` + `SMTO_ABORTIFHUNG`——`SendMessage` 在 Explorer 卡住时会无限阻塞，
而这条链路以前跑在钩子线程上，等于整个系统的鼠标输入一起卡死。

### 配置读写

- `ConfigService.Save` 先写 `config.json.tmp` 再 `File.Replace`，避免写一半被杀进程留下半截
  JSON；解析失败时先把坏文件另存为 `config.json.corrupt-<时间戳>` 再退回默认值，否则下一次
  保存就把默认配置盖上去、用户的分区规则再也找不回来。
- `SettingsService` 的 400ms 防抖保存直接调 `FlushNow()`，**不要**在 Tick 里先清
  `_pendingSave`——那样 `FlushNow` 一进门就返回，防抖保存一次都不会真正写盘，配置只能靠退出
  路径兜底，进程被强杀就全丢。
- `SettingsService.Instance.Current` 是内存里的唯一实例；`ConfigService.Load()` 每次都重新
  读盘反序列化，拿到的是**另一个实例**，看不到未保存的改动。新代码优先用前者。

### 错误日志与埋点

`Services/ErrorReporter.cs` 是统一入口，写 `%TEMP%\WinTools-error-yyyyMMdd.log`，进程内互斥。

`App.TraceStartup` 的启动埋点**默认关闭**：它是同步 `File.AppendAllText`，打开时连图标加载
这种每项目多次的热路径都会写盘，日志还会无限增长（实测 1.6MB / 13000 行）。需要排查启动
问题时设环境变量 `WINTOOLS_TRACE=1`，或在 `%TEMP%` 放一个 `WinTools-trace.on` 空文件。

---

## 10. 历史决策与基线

### 启动耗时基线

实测（发布版，冷启动三次）：主窗口可见 **760~900ms**，九张卡片全部就绪 **1.7~1.8s**。
试过给功能页加 `x:Load="False"` 延迟实例化，两项指标**没有任何变化**——瓶颈在 WinUI 框架
自身初始化，不在页面 XAML 构建，所以那次改动按实测结果回退了。要再优化启动，方向是减少
首帧之前的窗口数量和 Mica 合成，不是拆页面。

### 已经删掉的东西（别再加回来）

- `DesktopOrganizeService` / `DesktopIconPositionService` / `DesktopFolderView` 共约 1270 行，
  是旧版"桌面整理"（直接操作资源管理器桌面 ListView 排图标）的实现，功能早已被桌面分区卡片
  取代，代码里只剩一句文档注释引用它们。配套的死配置项 `enableDesktopOrganize`、
  `desktopOrganizeFromRight`、`desktopZoneGap`、`desktopIconSpacingX/YAdjustment` 一并移除
  （config.json 里的旧字段会在下次保存时自然消失）。
  **注意**：页面 `ContentDesktopOrganize` 和导航项 `NavItemDesktopOrganize` 是**桌面分区页**
  的历史命名，不是这个服务，别一起删。「整理桌面」右键菜单随之取消，
  `DesktopContextMenuService` 现在只负责清理历史注册表项。
- `WindowHelper` 里 `SizeToContent` / `CenterOnScreen` / `PlayShowAnimation` /
  `ApplyBorderlessPopupChrome` / `GetContentFillBrush` / `GetFrameBorderBrush` /
  `GetContentCornerRadius`，是已删除的独立设置窗和快捷设置弹窗留下的，无任何调用方。
- 独立 `SettingsWindow`：设置已并入主窗口 `MainNav.NavItemAppSettings`，
  入口是托盘 / 全局快捷键 → `App.OpenSettingsWindow()` → `MainWindow.NavigateToSettings()`。

### 已知的技术债（还没动）

- `struct POINT` 在 8 个文件里各定义一份，`GetCursorPos` / `GetModuleHandle` / `GetMessage`
  等 30 多个 `DllImport` 重复声明，钩子线程样板在 `DesktopClickService` 和 `GlobalDragWatcher`
  里各写一遍。收敛到统一的 interop 层是纯机械改动，但要动 8+ 个文件。
- `MainWindow.xaml.cs` 的排版被自动格式化搞坏（满屏空行、`).ToList();` 单独成行）。
- `FeaturePagesHost.xaml` 的 DataTemplate 里还有约 10 处写死的 `FontSize` / `Padding` /
  `Margin`，违反第 5 节。

---

## 11. 主要文件

| 文件 | 职责 |
| --- | --- |
| `App.xaml/.cs` | 入口、单实例、主窗口与后台服务初始化 |
| `MainWindow.xaml/.cs` | 外壳：标题栏、导航、主题与状态圆点 |
| `MainWindow.Hotkeys.cs` | 全局快捷键注册与窗口过程子类化 |
| `MainWindow.DesktopCards.cs` | 桌面分区页：分区列表、卡片布局、预设与同步 |
| `MainWindow.Stash.cs` / `.Associations.cs` / `.Ime.cs` | 对应功能页的页面逻辑 |
| `MainWindow.PageControls.cs` | 按名取子页控件 |
| `FeaturePagesHost.xaml/.cs` | 全部功能子页面；事件转发给 `MainWindow` |
| `Styles/PageStyles.xaml` | UI 令牌与页面样式（唯一真源） |
| `DesktopCardManager.cs` | 卡片集合、同步节奏、呼出 / 收起与自动收起监测 |
| `DesktopCardWindow.xaml/.cs` | 单张卡片：Mica、图标、拖放、淡入淡出 |
| `DesktopCollectService.cs` / `DesktopZoneMatcher.cs` | 物理收纳与还原 / 分区匹配规则 |
| `Services/CardLayoutCache.cs` | 多屏拓扑 → 卡片位置尺寸快照 |
| `Services/SettingsService.cs` / `Config.cs` | 设置中心（防抖写盘）/ 配置结构与读写 |
| `Services/ErrorReporter.cs` | 统一错误日志入口 |
| `FloatingStashWindow` | 文件悬浮暂存；关窗清空本次记录 |
| `GlobalDragWatcher` | 普通鼠标 + 三指全局拖动检测 |
| `ProgramDropWindow` | 拖放文件时的程序选择窗口 |
| `DesktopClickService` | 桌面空白单击切换「显示桌面 / 恢复窗口」 |
| `DesktopContextMenuService` | 清理历史「整理桌面」注册表菜单 |
| `ThreeFingerDragService` | 精确触控板三指接触合成原生拖动 |
| `ThreeFingerCalibrationWindow` | 全屏三档速度校准，仅在基础曲线 ±20% 内微调 |
| `WindowHelper.cs` | 窗口外观、标题栏、Mica 分层颜色、尺寸适配 |
| `UiStyleService.cs` / `ThemeService.cs` | Mica 风格 / 浅色深色跟随系统 |
| `AutostartService.cs` | 开机自启（HKCU Run） |

改文件结构、发布位置或关键窗口行为时，同步更新本文件。
