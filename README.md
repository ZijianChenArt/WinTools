# WinTools 维护手册

基于 .NET 10 + WinUI 3 的 Windows 桌面工具集：桌面分区卡片、悬浮搜索、悬浮暂存、
三指拖拽、按程序切换输入法、程序关联与点击桌面。

> **这是仓库里唯一的说明文件**（含 AI 协作者）。
> 改 UI 之前必须读第 5 节，改桌面分区之前必须读第 7 节，这两节的规则是硬约束，不是建议。
> 凡是写着"踩过的坑""别改回去"的条目，都是已经用实测推翻过一次的方案。

文档核对日期：2026-09-10。历史实测数据只用于解释设计决策，不代表本次版本已重新测量。
本文的「规范」是维护要求；「已知问题」是尚未完成的工作，不能当成已经支持的功能。

快速导航：[构建](#2-构建两步缺一不可) · [发布](#3-发布) ·
[界面规范](#5-界面规范改-ui-必读) · [桌面分区](#7-桌面分区卡片) ·
[技术债与升级方向](#已知的技术债与升级方向) · [主要文件](#11-主要文件)

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
│  │  ├─ MainWindow.*.cs           # Hotkeys / DesktopCards / Spotlight / Stash / Associations / Ime
│  │  ├─ FeaturePagesHost.xaml     # 全部功能子页面
│  │  ├─ SpotlightWindow.xaml/.cs  # Alt+Space 悬浮搜索窗口
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

```powershell
& 'C:/Program Files/Microsoft Visual Studio/2022/Community/MSBuild/Current/Bin/MSBuild.exe' src/WinToolsExplorerCommand/WinToolsExplorerCommand.vcxproj /p:Configuration=Debug /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.26100.0
```

```powershell
dotnet build src/WinTools/WinTools.csproj -c Debug -p:Platform=x64
```

第一步不做，第二步会以 `MSB3030 找不到 WinToolsExplorerCommand.dll` 失败。

命令在仓库根目录的 PowerShell 中执行。当前机器安装的是 Windows SDK `10.0.26100.0`，
原生项目默认指定 `10.0.22621.0`，因此必须显式覆盖。其它机器按已安装的 SDK 调整；
此覆盖只选择原生扩展的构建工具，不修改主程序的最低系统版本。

调试产物：`src/WinTools/bin/x64/Debug/net10.0-windows10.0.22621.0/WinTools.exe`

---

## 3. 发布

先关掉正在运行的 WinTools，否则文件被占用。脚本自己会先用 MSBuild 编原生扩展，
再 `dotnet publish` 到暂存目录，成功后删除旧发布目录并移入新目录：

```powershell
./scripts/Publish-Release.ps1 -Runtime win-x64 -WindowsSdkVersion 10.0.26100.0
```

**每次修改程序后都必须完成构建与发布，并重新打开
`artifacts/release/win-x64/WinTools.exe`。** 不要只留下 Debug 产物，也不要让用户手动寻找或
重启新版；发布前先关闭占用发布目录的旧进程，发布成功后立即启动正式版。

发布脚本生成自包含版本，随包携带 .NET 10 运行时与 Windows App Runtime，目标机无需另装。
**不要**开启 `PublishTrimmed` / `PublishReadyToRun`（WinUI 3 会崩）。

`WindowsSdkVersion` 可选；省略时使用原生项目默认值。脚本会检查替换路径位于本仓库发布目录，
但当前替换过程**不是原子操作，也没有自动回滚**。脚本不会自动退出或启动 WinTools，需由维护者完成。

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
| 列表行内边距 | `8,10`（行高 48） | `PageListRowStyle` |

层级靠间距体现：**64 / 40（表头 → 主体） > 32（二级小节） > 16（组内） > 4（同类卡片）**。
主体里的第一个工具条用 `PageToolbarFirstStyle`，避免和表头的间距叠加。

`DataTemplate` 内部同样不许写死 `Padding` / `FontSize`：列表行用 `PageListRowStyle` +
`PageListTitleTextStyle`，行首徽标用 `PageRowBadgeStyle` + `PageRowBadgeTextStyle`，
行尾强调按钮用 `PageRowChipButtonStyle`，分区磁贴用 `PageTileIconStyle` /
`PageTileTitleTextStyle` / `PageTileCaptionTextStyle`。

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

### 5.3.2 标题栏：高度与元素对齐

高度 **40dip，对齐文件资源管理器的标签栏**（2026-09-06 实测：资源管理器标签栏 50 物理像素
@125% = 40dip；`Tall` 的 48dip 太厚，`Standard` 的 32dip 又偏薄）。

- 高度写在 `MainWindow.xaml` 的 `AppTitleBar.Height`，`HookTitleBarPadding` 传
  **`syncHeight: false`**，只同步左右两侧的系统按钮占位；传 true 会把高度拉回系统的 32dip。
- 系统按钮仍是 `TitleBarHeightOption.Standard`（32dip，贴顶部绘制），**资源管理器也是这样**
  ——实测它的按钮高亮框只有 28.8dip，并没有占满 40dip 的标签栏。
- 顺带解决了另一个问题：开关按钮高 32dip，在 40dip 的标题栏里上下各留 4dip，
  悬停圆角底框不会再顶满整条标题栏。

三个元素（侧栏开关 / logo / “WinTools”）必须**墨迹中心落在同一条水平线**上。下面四条都是
2026-09-06 用截屏量出来的（物理像素，125% 缩放下 40dip 标题栏中心 = 25.0）：

| 元素 | 做法 | 为什么 |
| --- | --- | --- |
| StackPanel `Margin="6,0,0,0"` | 开关按钮宽 36 → 中心 6+18 = 24 | 侧栏 `CompactPaneLength` 是 48、图标列中心 24，原来写 4 时汉堡比下面的导航图标偏左 2dip |
| `FontIcon` `FontSize="16" Margin="0,2,0,0"` | 图标字体墨迹贴基线往上长，行盒居中会高出约 1.5px；居中元素的上边距只生效一半，2dip 正好压回 | 15dip 在 125% 下是 18.75px 卡半像素 |
| `TextBlock` `TextLineBounds="Tight"` | 默认 `Full` 把 ascent/descent 空白算进行盒，“WinTools”没有下伸部，居中行盒会把墨迹压低约 1.5px | 让行盒贴住墨迹，居中才是真居中 |
| `Image` `Width/Height="16"` | 18dip = 22.5 物理像素卡半像素，上下抗锯齿不对称 | 16dip = 20px 落在像素网格上 |

改完实测（40dip 标题栏）：汉堡 25.0、logo 25.5、文字 25.5，最大差 **0.5 物理像素**。
改之前三者是 19.5 / 21.0 / 21.5（相对当时 32dip 的栏），差 2 个物理像素，肉眼能看出汉堡偏高。

系统标题栏按钮（最小化 / 最大化 / 关闭）的悬停底色必须用**半透明**
（`ApplyTitleBarButtonColors` 里深色 `#0FFFFFFF`、浅色 `#09000000`，取自标准
`SubtleFillColorSecondary`）。原来写的是不透明灰 `#2D2D2D`，在 Mica 标题栏上就是一块
突兀的灰方块——外壳层还铺着不透明色时看不出来，改成纯 Mica（见 5.4）后一眼就能看见。

### 5.4 颜色分层：对齐 Windows 设置的标准比例

Mica 风格下**外壳层完全不铺色**（对齐 Windows 设置），只有右侧内容区留一层遮罩，让它比
标题栏 / 侧栏深一档。这是 2026-09-06 按 Windows 设置窗口的实测值改的：

| 层 | 来源 | Mica 风格下的值 |
| --- | --- | --- |
| 窗口材质 | `MicaBackdrop`（`ApplyWindowBackdrop`） | 随壁纸 |
| 外壳层：标题栏 + 侧栏 | `GetCodexShellBrush` | **`Transparent`**（纯 Mica） |
| 内容区背景 | `GetCodexContentBrush`，深度 `ContentDepthPercent` | **50 → alpha 91 的黑**（Mica 仍透 64%） |
| 卡片 | `PageStyles` 的 `CardBackgroundFillColorDefaultBrush` | 深色 `#0DFFFFFF`（白 5%） |

调内容区深浅**只改 `WindowHelper.ContentDepthPercent`**（0 = 全透 Mica，100 = 近不透明；
深色下映射到 alpha 32~150）。不要回头在外壳层重新铺不透明色——那正是下面那张表里
「1 个色阶」的由来。

实测依据（深色主题，同一张壁纸，窗口从屏幕最左移到最右取样导航栏）：

| | 左位 | 右位 | 变化 |
| --- | --- | --- | --- |
| Windows 设置 | (33,31,38) | (41,26,42) | 5~8 个色阶 |
| WinTools（改前，壳层 alpha 235） | (37,52,71) | (37,52,72) | **1 个色阶** |
| WinTools（改后，导航栏 = 纯 Mica） | (35,33,40) | (41,29,44) | 5~6 个色阶 |

内容区保留遮罩后实测：导航栏 (32,34,38) / 内容区 (18,21,24)，两档层次仍在，且内容区
仍有 64% 的 Mica 透过来。

改前壳层铺了 alpha 235，Mica 只剩 8%，所以换壁纸几乎看不出变化——用户报的「设置界面 Mica
不实时」就是这么来的，不是刷新逻辑有问题。反推 Windows 设置的卡片层：用它的导航栏（纯 Mica）
和内容卡片两处取样解方程得 alpha≈13/255，正好等于标准 `CardBackgroundFillColorDefault` 的 `#0D`。

- 标题栏和导航面板背景仍必须是 `Transparent`。
- **失焦变灰是标准行为，不是 bug**：内置 `MicaBackdrop` 在窗口失焦时切到 fallback 纯色。
  实测 Windows 设置失焦 (32,32,32)、WinTools 失焦 (34,34,34)，一致。想让主窗口失焦也保持
  材质，只能像桌面卡片那样换显式 `MicaController` + `IsInputActive = true`（见 7.4），
  那就偏离系统标准了，目前**没有**这么做。
- 非 Mica（「普通」）风格仍回落到不透明底色，那条分支不受本节影响。

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

### 7.1 数据流：原地不动，只是不显示

**卡片不搬文件。** 桌面上的东西永远待在 `桌面\xxx`，路径一个字节都不变；桌面之所以干净，
是因为程序关掉了系统的「显示桌面图标」。2026-09-07 换成这套机制，理由见本节末尾。

- **`Services/DesktopIconVisibilityService`**：桌面图标显隐。切换靠给 `SHELLDLL_DefView` 发
  `WM_COMMAND 0x7402`（就是右键菜单那条命令，**翻转**语义，所以要先读状态），当前状态读注册表
  `HKCU\...\Explorer\Advanced\HideIcons`。`SHELLDLL_DefView` 一般挂在 `Progman` 下，
  开壁纸动画 / 多屏时会跑到某个 `WorkerW` 下，找不到要遍历。
  **卡片是独立 HWND，不受这个开关影响**（实测）。
- **`DesktopCollectService`**：枚举用户桌面 + 公共桌面，跳过 `desktop.ini` 和用户自己设了
  隐藏/系统属性的项，按规则**实时分组**（`GroupByZone`）。桌面只枚举一次，9 张卡片共用同一份快照。
- **`Services/DesktopShellItems`**：补上**系统虚拟图标**（此电脑 / 回收站 / 网络 / 控制面板）。
  “用户的文件”会显示为当前账户名（例如“陈”），不加入分区。上述虚拟项在桌面文件夹里没有文件，
  枚举目录一个也看不到；桌面图标一关，不补进卡片
  用户就再也点不到了。用 Shell 解析名 `::{CLSID}` 当 Path，显示名和图标通过 PIDL 找 Shell 要
  （`SHParseDisplayName` + `SHGetFileInfo` 带 `SHGFI_PIDL`），打开走 `explorer.exe shell:::{CLSID}`。
  是否启用读注册表 `HideDesktopIcons\NewStartPanel`（值 1 = 用户在「桌面图标设置」里关掉了）。
- **`DesktopZoneMatcher`**：显式文件名 → 名称关键词 → 游戏协议 → 扩展名 / 文件夹 → 兜底分区，
  五级优先级。关键词以 `.` 开头视为扩展名；含 `steam://` 等协议的关键词优先匹配 `.url` 目标。
- **兜底不能丢**：桌面图标整体隐藏后，任何一项都必须落进某张卡片，否则它就彻底没有入口了。
  所以 `GroupByZone` 在规则没命中时兜底到**最后一个分区**，而不是像旧版那样跳过。
- **分区归属**只存一处：配置里每个分区的显式清单 `DesktopZone.Items`。拖动换区 =
  `PinItemToZone` 改这个清单，**不动文件**，也因此不会再有「移动失败 / 重名冲突 / 文件被占用」。

**为什么放弃旧的物理移动**：旧版把桌面项目搬进 `%USERPROFILE%\WinTools\<分区名>\`，用
`collected.json` 记账。它改变文件真实路径，还会和正在写盘的下载、正打开着文件的程序抢占用——
用户把图片下载到桌面、它立刻被搬走，这正是 2026-09-07 推翻这套机制的直接原因。
`MigrateLegacyManagedItems` 负责一次性把旧数据搬回桌面并把归属写进显式清单，
旧日志改名存档为 `collected.json.migrated-<时间戳>`，不删除。

**迁移踩过的坑（已修，别再犯）**：第一版按 `collected.json` 里的 `Source` 原路径还原，
结果 62 项里有 13 项的来源是早年从 DeskBox 导入的 `%USERPROFILE%\DeskBox\...`，
被送回了桌面**之外**——既不在桌面也不在卡片里，等于凭空消失。现在一律落到当前用户桌面。

**「双击桌面返回」会受牵连**：`DesktopClickService.IsDesktopBlankArea` 原本要求
`WindowFromPoint` 返回 `SysListView32`。图标隐藏后 Explorer 把 ListView 一起藏了，
拿到的是 `SHELLDLL_DefView` / `WorkerW` / `Progman`，判定直接失败、功能全废（用户当场就发现了）。
现在这三个类名一律视为空白处——图标都没了，本来也不存在「点到图标」这回事。

### 7.2 文件监视：只剩一个监视器

**整个程序只有一个 `FileSystemWatcher`**，在 `DesktopCardManager` 里盯着桌面目录，
650ms 防抖后 `SyncAsync()` → 重新枚举桌面 → 刷新所有卡片。

2026-09-07 之前是两个：桌面目录一个，外加**每张卡片各自监视自己的托管目录**。
后者随物理收纳机制一起删了——现在卡片的内容全部来自桌面这一个目录，再给 9 张卡片各挂一个
监视同一个目录的 watcher 纯属浪费，而且旧结构里卡片会在目录事件后**立即**
`RefreshContent()`、然后才通知管理器防抖同步，等于「管理器有防抖」但实际刷新没有防抖，
一次拖放要重复刷好几轮（当时列为技术债，现在随结构变化自然消失）。

拖放换区**不产生文件事件**（只改配置里的显式清单），所以卡片在 Drop 成功后要自己
`ContentChanged?.Invoke(...)`，不能等监视器。

图标拖动期间暂缓清空源卡片集合，拖动结束后再读取目录变化，以免破坏拖动状态。

`DesktopCardWindow.RefreshContent` 按路径复用旧的 `CardItem`，保留已加载的图标；图标解析
结果另有一层进程内缓存（键为路径 + 最后写入时间，上限 512 条）。桌面动一个文件不该让
整张卡片重新走一遍 Shell / `WScript.Shell` 图标解析（单个 `.lnk` 几十毫秒）。

### 7.3 分区归属：拖放换区与预设文件

- **分区内排序**：按住图标拖到同一卡片的目标位置，支持跨行插入，图标仍按网格排列。
  同卡片内完全交给 GridView 的 `CanReorderItems`，使用和设置页分区排序相同的 Fluent 插入间隙、
  实时让位与缓动；自定义 `DragOver` / `Drop` 只处理跨卡片移动，不能拦截同卡片事件。
  系统虚拟图标始终排在最前，文件夹集中为一组，普通文件优先按扩展名分组；同一组内部才使用
  用户拖动顺序，再按名称兜底。松手后将文件名顺序保存到
  `%LOCALAPPDATA%\WinTools\card-item-order.json`，
  刷新和重启按上述分组及组内顺序显示，不改文件名或归属规则。
  当前顺序按分区名保存，分区改名后尚不迁移这份顺序；预设也暂不包含它。
- **图标交互**：文件、文件夹与系统虚拟图标都必须双击打开，单击只用于选择或准备拖动。
  卡片的简化右键菜单保留「打开 / 删除到回收站」，并提供「显示系统右键菜单」入口；后者通过
  Shell `IContextMenu` 显示项目原生菜单，不能用跳转到资源管理器代替，否则打开方式、发送到、
  压缩扩展、属性等 Shell 功能会丢失。系统虚拟图标没有文件实体，不显示自定义删除结果。
- **悬停圆角**：图标容器显式引用 `PageInnerCornerRadius`（8px），文件名提示使用
  `PageDesktopToolTipStyle`（12px）。自定义样式分别继承 `DefaultGridViewItemStyle` 和
  `DefaultToolTipStyle`，避免退回旧模板；不要只改资源键或控件属性而忽略实际绘制模板。

- **分区工具栏**：常用区只保留「新建」；预设导入导出、手动同步、恢复默认和全部还原
  属于低频操作，统一放在右侧的「更多」菜单，不要再铺成一整排按钮。这里使用
  `PageToolbarButtonStyle` / `PageToolbarIconButtonStyle` 的普通按钮，不使用 `CommandBar`
  自动生成的 `MoreButton`；后者会采用直角、顶部对齐的系统内部模板。工具栏按钮常态下
  背景与边框必须透明，只显示文字 / 图标；鼠标悬浮或按下时才显示 10px 圆角底框。
- **托盘右键菜单**也有一个「同步桌面」，与「更多」菜单里的同名项走同一条路径
  （`MainWindow.SyncDesktopCardsFromTray`）。分区总开关没开时该项**置灰**，不弹对话框——
  托盘菜单没有可用的 `XamlRoot`，`ContentDialog` 弹不出来。两处文案必须保持一致。
- **分区编辑**：右键分区卡片只显示「重命名 / 删除」，不使用右侧编辑面板，也不在工具栏
  重复提供编辑和删除。私人使用模式不显示文件清单或自定义关键词规则；分区中的项目一律
  在桌面卡片之间直接拖动。重命名必须同时迁移
  `%USERPROFILE%\WinTools\<分区名>` 托管目录及 `collected.json` 中的记录，不能让文件失联。
- **分区列表布局**：设置页中的分区卡片不得固定宽度。`Zones_SizeChanged` 按列表可用宽度
  动态计算 2～4 列，并把宽度均分给 `ItemsWrapGrid`；窄窗口两列，空间增加后自动变为三、四列。
  计算时必须为滚动条、边框和像素取整预留安全宽度，并直接更新当前面板；不要把每次
  `SizeChanged` 都异步排队，否则拖动窗口边框时会因溢出或旧尺寸回放短暂闪成一列。
- **拖放换区**：卡片之间可以直接拖图标。`DesktopCardWindow.ItemsGrid_Drop` 只上报目标分区，
  由 `DesktopCardManager` 调 `DesktopCollectService.PinItemToZone` 把项目写进目标分区的
  `Items` 显式清单，并从其它分区清单摘掉；文件始终留在桌面原路径，不再调用物理移动逻辑。
  **显式清单这一步不能省**：它是 `DesktopZoneMatcher` 的第一优先级，否则下次同步会按关键词
  把用户刚拖过去的项目重新分回原分区。管理器随后刷新卡片并通知设置页重新载入分区列表，
  避免页面里的旧配置副本在下次保存时覆盖拖放结果。
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

### 7.8 分辨率 / 缩放变化后的重排

卡片的位置和尺寸都是**算成物理像素写死**上去的：`ApplyAutoSize` 用当时的
`RasterizationScale` 把 DIP 乘成像素再 `Resize`，`LayoutMonitorGroup` 按当时的
`GetWorkAreaDip()` 和 gap / margin 算位置。系统改了分辨率或缩放**不会**替我们更新这些值，
必须自己监听后重排。曾经完全没有这一步，表现就是「改完分辨率要手动同步一次才正常」。

链路：`MainWindow.HotkeyWndProc`（复用已有的窗口过程子类化，不必引入
`Microsoft.Win32.SystemEvents` 依赖）接住 `WM_DISPLAYCHANGE` / `WM_DPICHANGED` /
`WM_SETTINGCHANGE`+`SPI_SETWORKAREA` → `QueueDesktopCardRelayout()` 防抖 →
`DesktopCardManager.RelayoutForDisplayChange()`。三条硬性要求：

- **必须防抖，且不能太短（现为 700ms）**。改一次分辨率 Windows 会连发好几条消息，
  而且消息到达时 `XamlRoot.RasterizationScale` 往往还是旧值——立刻重排等于用旧缩放
  再算错一遍，白修。
- **必须先 `ApplyScaleAwareSize()` 再 `Layout()`**，顺序不能反。只调 `Layout()` 不够：
  窗口像素尺寸还是按旧缩放设的，`Layout` 却按 `DipWidth/DipHeight` 算间距，
  于是卡片大小和间距对不上，看起来就是「间距没跟着调整」。
- 这三条消息**只作通知**，处理完必须继续传给原窗口过程（WinUI 自己也要处理 DPI），
  不能像 `WM_HOTKEY` 那样直接 return。

### 7.9 卡片窗口的身份：工具窗口 + 空标题

一个分区一个顶层 HWND，九个分区就是九个 `WS_VISIBLE` 的顶层窗口。**`AppWindow.IsShownInSwitchers
= false` 不够**：它只在 Shell 层面挡住 Alt-Tab 和任务栏，不改扩展样式（实测卡片窗口的 ex style
是 `0x00000100`，只有 `WS_EX_WINDOWEDGE`）。凡是按 `EnumWindows` 找「应用窗口」的系统组件——
尤其是关机界面——照样把每张卡片当成一个独立应用，于是关机时列出九行同名的「桌面卡片」，
看起来像一堆进程。

因此 `ConfigureWindow` 里额外做两件事，**首次 `Show()` 之前**完成：

- `MarkAsToolWindow()` 加 `WS_EX_TOOLWINDOW`。它与 Mica **不**冲突（冲突的是 `WS_EX_LAYERED`，
  见 7.4），`SetLayered` 只翻 LAYERED 那一位，淡入淡出不受影响。
- `Title = string.Empty`。关机界面按窗口标题列条目，卡片不需要标题。

**没有**加 `WS_EX_NOACTIVATE`：卡片要接收 GridView 的拖放和点击，加这个标志的输入行为风险
远大于收益，而且它对上面的列举问题没有帮助。

`AppWindow.Closing` 里的 `e.Cancel = true`（卡片只隐藏不关闭）**必须先查 `App.IsShuttingDown`**，
见第 9 节；否则关机界面上的「结束任务」和不带 `/F` 的 taskkill 都关不掉这些窗口。

卡片窗口的外观配置走 `WindowHelper.ConfigureChromelessWindow`，**不要**改回
`ConfigurePopupTitleBar`：后者的 `ApplyWindowBackdrop`（违反 7.4，还要建一个系统背景控制器
再被卡片自己的 `MicaController` 拆掉）、`ExtendsContentIntoTitleBar` 和标题栏配色，对没有
标题栏元素的卡片全是纯开销，实测每张 87ms、九张 0.78 秒。

### 7.10 图标解析链路

`CardItem` 的图标解析跑在 **4 条后台 STA 线程**上（`IconWorkerCount`），每条线程复用自己的
`WScript.Shell` 实例（`[ThreadStatic]`，COM 单元规则决定了不能跨线程共享）。2026-09-06 实测
69 个托管项目：

| 项目 | 耗时 |
| --- | --- |
| `SHGetFileInfo` 取图标，单线程串行 | 490 ms（7.1 ms/项） |
| 同样的活，4 条 STA 线程 | **85 ms** |
| 图标转 PNG | 15 ms（可忽略，别在这里优化） |
| 51 个 `.lnk`：每项新建 `WScript.Shell` | 233 ms |
| 同上，复用一个实例 | **78 ms** |
| 69 个图标的 PNG 总大小 | 116 KB |

图标缓存目前**只在进程内**（键为路径 + 最后写入时间，上限 512 条），每次冷启动都要重跑一遍。
考虑到全部图标只有 116KB，落一份磁盘缓存是下一步的方向，但要先想清楚失效策略
（目标程序升级、系统主题与 DPI 变化）。

---

## 8. 其它功能要点

### 悬浮搜索

- 默认用 `Alt+Space` 呼出；关闭功能后立即注销快捷键，把组合键让回系统窗口菜单。
- `Services/AppSearchIndex.cs` 在后台 STA 线程扫描用户/公共开始菜单、桌面快捷方式和
  `shell:AppsFolder`，覆盖传统 Win32 与应用商店应用；索引缓存 10 分钟，也可在设置页手动重建。
- 支持程序名、中文全拼和拼音首字母匹配。`↑` / `↓` 选择，`Enter` 启动，`Ctrl+Enter`
  打开所在位置，`Esc` 或失去焦点关闭；没有匹配结果时，`Enter` 直接执行输入内容。
- 搜索窗口的图标解析复用桌面卡片的 `CardItem` 缓存和 STA 队列，不能另建一套同步 Shell 图标链路。

### 侧栏状态圆点

`UpdateNavStatusIndicators()` 里每个圆点**只反映该页的总开关**。不要再写"或者某个子功能还开着"
这类复合条件，避免页面关闭后圆点却还亮着。
实测：同一像素点从 `#4CC2FF`（开）变成 `#777A83`（关）。

### 托盘菜单

托盘图标贴着任务栏，右键菜单必须向上弹：`TrayIcon.BuildContextMenu` 里设
`Placement = FlyoutPlacementMode.TopEdgeAlignedRight`。实测菜单占 y=1098~1228，
点击点 y=1235，完全在上方。

**已知问题：位置对了但动画方向不对**。菜单弹在光标上方，入场动画却是从上往下展开，和系统托盘里
其它应用相反。框架按**它自己解析出来的**放置方向挑动画，而托盘这条路径（H.NotifyIcon 的
`ContextMenuMode.SecondWindow` + 光标定位）拿到的是「向下」那一套。

2026-09-06 试过两条路，**都失败并已回退**，别再重来一遍：

1. `AreOpenCloseAnimationsEnabled = false` + 给 `MenuFlyoutPresenterStyle` 挂
   `PopupThemeTransition { FromVerticalOffset = 40 }`：那条 Transition 在这条弹出路径上
   **根本不触发**，结果是彻底没有动画。
2. 同样关掉框架动画，改在 `Opened` 里用 `GetOpenPopupsForXamlRoot` 找到弹出层、
   自己跑合成动画（Translation + Opacity）：同样没有可见动画。

结论：**保留框架默认动画**（方向不对但至少有过渡），只保留 `Placement` 设置。真要修，
下一步应该先搞清楚 H.NotifyIcon 的 `ShowContextMenu` 到底用什么参数调 `ShowAt`，
而不是继续在动画这一层试。

`TaskbarIcon` 是 `FrameworkElement`，必须挂在可视树上，所以有一个专门装它的宿主窗口
（标题「WinTools Tray」）。**这个窗口缩不到 1×1**：WinUI 3 有最小窗口尺寸，`AppWindow.Resize(1,1)`
实测最终是 136×39。而真正把它显示出来的是 `EnsureShown()` 里的 `Activate()`，于是屏幕左上角
(156,156) 会冒出一个空白小方块。`IsShownInSwitchers = false` 只挡 Alt-Tab / 任务栏，挡不住它被画出来。
正确做法是 **`Activate()` 之后**重设 `IsShownInSwitchers`、尺寸，并 `Move` 到 (-32000,-32000)；
只在窗口首次显示**之前**设这些属性是不够的。

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

### 退出与会话结束（注销 / 关机）

**WinUI 3 收到 `WM_ENDSESSION` 不会自己退出。** 不主动退的后果是 Windows 等满
`WaitToKillAppTimeout`（默认 5s），然后把本进程**所有可见顶层窗口**列到「这些应用阻止关机」
那一屏上——分区卡片有几张就列几行同名条目。同时关机时进程由 csrss 结束，
`AppDomain.ProcessExit` 不保证执行，只靠它保存配置会丢改动。

链路：`MainWindow.HotkeyWndProc`（复用已有的子类化窗口过程）→

| 消息 | 处理 |
| --- | --- |
| `WM_QUERYENDSESSION` (0x0011) | `App.SaveBeforeSessionEnd()` 只落盘。会话仍可能被别的应用取消，**不能**在这里退出 |
| `WM_ENDSESSION` (0x0016) 且 wParam≠0 | `App.ShutdownForSessionEnd()`：落盘 → `Environment.Exit(0)` |

两条消息处理完都继续传给原窗口过程，`WM_QUERYENDSESSION` 的返回值交给 `DefWindowProc`（TRUE），
不要自己决定放不放行关机。

**这两条消息是 csrss 用 `SendMessage` 跨进程同步发过来的，处理期间禁止 COM 调出**
（`RPC_E_CANTCALLOUT_ININPUTSYNCCALL`）。实测把 `SaveConfigOnExit` 直接放进窗口过程，会在
`MainWindow.FlushAndSaveConfig` → `FindName` 读快捷键文本框那一步抛 `COMException`，
配置一点都存不上。所以：

- 窗口过程里只调 `App.FlushSettingsWithoutUi()`（`SettingsService.FlushNow` → 纯文件 I/O）；
- 需要读 XAML 的完整保存用 `DispatcherQueue.TryEnqueue` 排队，回到消息循环（两条消息之间）再跑；
- `WM_ENDSESSION` 路径**不摘托盘图标**——`TaskbarIcon` 是 XAML 元素，受同一限制，而注销 / 关机
  时整个 Shell 都会消失，不会留下幽灵图标。托盘「退出」走的 `App.ShutdownNow()` 是普通上下文，
  照常读 XAML、摘图标；
- `AppDomain.ProcessExit` 回调在 `IsShuttingDown` 时直接返回，避免再撞一次同样的 COM 限制。

`App.IsShuttingDown` 是这套流程的唯一开关，托盘「退出」也会置位。**所有「关闭时只隐藏」的
窗口**（主窗口、分区卡片、悬浮暂存、程序选择）在 `AppWindow.Closing` 里都必须先查它再决定
要不要 `e.Cancel = true`，否则这些窗口永远拒绝 `WM_CLOSE`。

### 错误日志与埋点

`Services/ErrorReporter.cs` 是统一入口，写 `%TEMP%\WinTools-error-yyyyMMdd.log`，进程内互斥。

`App.TraceStartup` 的启动埋点**默认关闭**：它是同步 `File.AppendAllText`，打开时连图标加载
这种每项目多次的热路径都会写盘，日志还会无限增长（实测 1.6MB / 13000 行）。需要排查启动
问题时设环境变量 `WINTOOLS_TRACE=1`，或在 `%TEMP%` 放一个 `WinTools-trace.on` 空文件。

---

## 10. 历史决策与基线

### 启动耗时基线

实测（发布版，冷启动，轮询窗口状态计时）：主窗口可见 **670~790ms**，九张卡片全部揭示
**2.6~2.8s**。原文写的「九张卡片 1.7~1.8s」和当前实现对不上，2026-09-06 已按重测结果更正。

用 `WINTOOLS_TRACE=1` 打出来的分段（九张卡片、59 个图标）：

| 阶段 | 耗时 |
| --- | --- |
| App + 主窗口首帧 | ~700 ms |
| 首帧后 `Task.Delay(80)` + `InitializeAsync` 进入 | ~270 ms |
| 后台收纳（Import + Reclassify + CollectAll） | **~10 ms**（不是瓶颈，别再优化这里） |
| **创建 9 个卡片窗口** | **~1130 ms（每张 ~100ms，串行在 UI 线程上）** |
| 等 XAML `Loaded` | ~130 ms |
| 等图标 | ~60 ms |
| 二次布局（9 张 Resize + Move） | ~110 ms |
| 揭示动画启动 | ~30 ms |

**瓶颈是「一分区一窗口」本身**：每张卡片约 100ms，其中 `OverlappedPresenter.SetBorderAndTitleBar`
一项就占 ~50ms，剩下是 WinUI 窗口基座（~12ms）和 XAML 构建（~6ms，很便宜）。想再往下压，
只能减少 HWND 数量（见第 7.9 节末尾的架构选项），不是继续抠单窗口的初始化。

已经做过、有实测结论的尝试：

- 功能页加 `x:Load="False"` 延迟实例化：两项指标**没有任何变化**，瓶颈在 WinUI 框架自身
  初始化，不在页面 XAML 构建，那次改动按实测结果回退了。
- 图标解析从 1 条 STA 线程改成 4 条 + 复用 `WScript.Shell`（见 7.10）：图标链路本身快 6 倍，
  但**端到端启动几乎没变**——图标解析和建窗口是重叠的，等图标只占 60ms。这条改动的价值
  在于项目数量涨上去以后不会退化，不是当前的 2.6s。
- 卡片改走 `ConfigureChromelessWindow` + 跳过必然算错的首次排版：每张卡片省 ~35ms，
  端到端约 **-200~300ms**（冷启动run 间抖动 ±150ms，这个量级要多跑几次才看得出来）。

### 已经删掉的东西（别再加回来）

- **物理收纳那一整套**：`CollectAll` / `RestoreAll` / `ReclassifyManagedItems` /
  `ImportLegacyDeskBoxItems` / `collected.json` 记账 / `%USERPROFILE%\WinTools\<分区名>\`
  托管目录（2026-09-07）。理由见 7.1：它改变文件真实路径，还会和正在写盘的程序抢占用。
  `MigrateLegacyManagedItems` 是唯一保留的遗留代码，只为把旧数据搬回桌面，跑完就没用了。
- **每张卡片自己的 `FileSystemWatcher`**：改成所有卡片共用管理器那一个桌面监视器。
  以前一次拖放会触发多轮重复刷新（README 曾把它列为技术债）。

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

### 已知的技术债与升级方向

- `struct POINT` 在 8 个文件里各定义一份，`GetCursorPos` / `GetModuleHandle` / `GetMessage`
  等 30 多个 `DllImport` 重复声明，钩子线程样板在 `DesktopClickService` 和 `GlobalDragWatcher`
  里各写一遍。收敛到统一的 interop 层是纯机械改动，但要动 8+ 个文件。
- 优先统一卡片目录事件的刷新调度，消除卡片即时刷新与管理器防抖刷新的重复工作。
- 图标顺序支持分区重命名迁移、恢复默认排序，以及随预设导入导出。
- 发布过程增加旧版备份与失败回滚，避免删除旧目录后移动失败导致正式版暂时不可用。
- 文件打开和部分刷新路径仍静默吞掉异常，可按需补错误日志和可操作的失败提示。
- `DesktopCardWindow.xaml` 仍有历史写死的间距和字号，需逐步归入样式资源；
  `FeaturePagesHost.xaml` 经本次检索已无这些内联值，唯一 `MaxWidth` 是第 5.3 节允许的列限宽，
  原文「DataTemplate 还有约 10 处」已过时。

---

## 11. 主要文件

| 文件 | 职责 |
| --- | --- |
| `App.xaml/.cs` | 入口、单实例、主窗口与后台服务初始化 |
| `MainWindow.xaml/.cs` | 外壳：标题栏、导航、主题与状态圆点 |
| `MainWindow.Hotkeys.cs` | 全局快捷键注册与窗口过程子类化 |
| `MainWindow.Spotlight.cs` / `SpotlightWindow.xaml/.cs` | 悬浮搜索设置、快捷键入口与搜索窗口 |
| `MainWindow.DesktopCards.cs` | 桌面分区页：分区列表、卡片布局、预设与同步 |
| `MainWindow.Stash.cs` / `.Associations.cs` / `.Ime.cs` | 对应功能页的页面逻辑 |
| `MainWindow.PageControls.cs` | 按名取子页控件 |
| `FeaturePagesHost.xaml/.cs` | 全部功能子页面；事件转发给 `MainWindow` |
| `Styles/PageStyles.xaml` | UI 令牌与页面样式（唯一真源） |
| `DesktopCardManager.cs` | 卡片集合、同步节奏、呼出 / 收起与自动收起监测 |
| `DesktopCardWindow.xaml/.cs` | 单张卡片：Mica、图标、拖放、淡入淡出 |
| `DesktopCollectService.cs` / `DesktopZoneMatcher.cs` | 桌面枚举与实时分组 / 分区匹配规则（**不再移动文件**） |
| `Services/DesktopIconVisibilityService.cs` | 桌面图标显隐（等价于「显示桌面图标」开关） |
| `Services/DesktopShellItems.cs` | 系统虚拟图标（此电脑 / 回收站 / 网络…）接入卡片 |
| `Services/CardLayoutCache.cs` | 多屏拓扑 → 卡片位置尺寸快照 |
| `Services/CardItemOrderStore.cs` | 分区内图标顺序、刷新恢复与独立持久化 |
| `Services/AppSearchIndex.cs` / `Pinyin.cs` / `PinyinData.cs` | 应用索引与中文拼音检索 |
| `Services/SettingsService.cs` / `Config.cs` | 设置中心（防抖写盘）/ 配置结构与读写 |
| `Services/ErrorReporter.cs` | 统一错误日志入口 |
| `FloatingStashWindow` | 文件悬浮暂存；关窗清空本次记录 |
| `GlobalDragWatcher` | 普通鼠标 + 三指全局拖动检测 |
| `ProgramDropWindow` | 拖放文件时的程序选择窗口 |
| `DesktopClickService` | 桌面空白双击切换「显示桌面 / 恢复窗口」 |
| `DesktopContextMenuService` | 清理历史「整理桌面」注册表菜单 |
| `ThreeFingerDragService` | 精确触控板三指接触合成原生拖动 |
| `ThreeFingerCalibrationWindow` | 全屏三档速度校准，仅在基础曲线 ±20% 内微调 |
| `WindowHelper.cs` | 窗口外观、标题栏、Mica 分层颜色、尺寸适配 |
| `UiStyleService.cs` / `ThemeService.cs` | Mica 风格 / 浅色深色跟随系统 |
| `AutostartService.cs` | 开机自启（HKCU Run） |

改文件结构、发布位置或关键窗口行为时，同步更新本文件。

## 12. 本次核对与验证（2026-09-10）

- 已核对构建脚本、当前 SDK、页面内联样式、目录监视链路和排序实现。
- 正式版构建与发布成功，已重新启动；Markdown 代码围栏与差异空白检查通过。
- 文件名圆角提示已在实际卡片界面确认。
- 独立临时目录中的顺序存储检查通过：首次读取、首尾换位、磁盘重载、新增追加、删除过滤、
  文件名大小写与分区隔离。未改写用户配置来运行这些检查。
- **拖动排序的界面验收待确认**：自动输入可触发拖动，但未观察到成功落下并保存。
  存储检查不等同于鼠标交互通过；需用真实鼠标复核分区内跨行拖动、跨分区移动、取消拖动和重启恢复。

后续完成界面复核后应更新此处，不要把未通过的检查写成「已实测支持」。
