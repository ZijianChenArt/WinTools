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

### 3.1 版本号与安装包（每次改动都要走）

每次改动都要**递增版本号**并生成安装包，用一键脚本（它先改 `WinTools.csproj` 里 `Version` /
`AssemblyVersion` / `FileVersion` / `InformationalVersion`，再调 `Publish-Release.ps1`，最后用
Inno Setup 6 打 `installer/WinTools.iss`）：

```powershell
./scripts/Release.ps1            # 默认 patch：1.2.3 -> 1.2.4；功能性更新用 -Bump minor；只重新打包用 -Bump none
```

- 产物在 `artifacts/dist/`：`WinTools-Setup-<版本>.exe` 与 `.sha256`（应用内在线更新会校验后者）。
- 需要 Inno Setup 6（`winget install JRSoftware.InnoSetup`）；运行前先退出正在运行的 WinTools。
- 验收：**运行生成的 Setup.exe 重新安装**，启动安装后的 WinTools，确认「设置」里显示的版本号等于新版本，
  并实际打开本次改动涉及的界面确认效果。
- 想让软件能在线更新，还要把提交打 tag `v<版本>` 推到 GitHub 并上传上述两个文件（脚本末尾会打印命令）。

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
    <ScrollViewer Grid.Row="1" Style="{StaticResource PageBodyScrollStyle}">
        <StackPanel>
            <TextBlock Text="常规" Style="{StaticResource PageSectionHeaderFirstTextStyle}"/>
            <StackPanel Style="{StaticResource PageCardStackStyle}">…卡片…</StackPanel>
            <TextBlock Text="使用方法" Style="{StaticResource PageSectionHeaderTextStyle}"/>
            <StackPanel Style="{StaticResource PageCardStackStyle}">…卡片…</StackPanel>
        </StackPanel>
    </ScrollViewer>
    <Grid         Grid.Row="1" Style="{StaticResource PageBodyGridStyle}">…</Grid>
</Grid>
```

**所有页面结构一致**（2026-09-25 统一）：表头只用 `PageHeaderStyle`；主体第一块永远是一个
40 高的小节标题行，卡片组上方用 `PageSectionHeaderFirstTextStyle` / `PageSectionHeaderTextStyle`，
列表上方用 `PageToolbarFirstStyle` / `PageToolbarStyle`。两者高度都是 40、文字落在同一条线上，
所以切页时第一块内容的位置不会跳。

小节命名固定：**常规**（功能自身的设置）→ 具体分组（如「额度账户」「分区」「规则」）→
**使用方法**（固定放最后，平铺成卡片，不用 `SettingsExpander` 折叠）。按键提示用
`PageKeyCapStyle` + `PageKeyCapTextStyle` 画成键帽；鼠标操作（"单击小球"）用 `PageCaptionTextStyle`。

列表工具条三页同一写法：`[新建] [编辑] [删除] … [⋯ 更多]`。主要命令是 `PageToolbarButtonStyle`
按钮（内容固定为 `FontIcon PageToolbarIconStyle` + `TextBlock PageToolbarLabelTextStyle`，
必须写 ToolTip），次要命令（导入 / 导出 / 恢复默认 / 刷新）一律放进 `PageToolbarIconButtonStyle`
的「更多」菜单。**不再使用 `CommandBar`**（其 MoreButton 是直角顶对齐模板，还会接管命令项样式）。

卡片右侧的输入控件用统一宽度样式：快捷键框 `PageHotkeyBoxStyle`（200，右侧跟「应用」按钮）、
`PageSettingNumberBoxStyle` / `PageSettingComboBoxStyle`（160）。

- **设置型页面**（三指拖拽 / 悬浮暂存 / 点击桌面 / 设置）：主体是
  `PageBodyScrollStyle` + `PageCardStackStyle` 卡片堆。
- **列表型页面**（输入法切换 / 程序关联 / 桌面分区）：主体是 `PageBodyGridStyle`，
  行 0 是 `PageToolbarFirstStyle` 工具条，行 1 是 `PageWorkAreaStyle` 双栏工作区
  （列表 + 右侧编辑面板），**列表高度由行撑满，不写 MinHeight / MaxHeight**。
- 每个列表都要有空状态提示：列表容器里叠一个 `PageEmptyHintTextStyle` 的 TextBlock，
  在 `UpdateXxxCount()` 里跟着条数切换可见性。
- **输入法切换页是例外**：不用列表，改成图标看板。上方是“未设置”的运行中应用（只显示图标，
  名称在悬停提示里），下方左右两栏 = 中文 / 英文。图标可拖到目标栏（拖回上方 = 取消设置），
  也可点一下在菜单里选。图标由 `ImeAppTile` / `AppIconLoader` 按进程名取 exe 的 Shell 图标；
  规则里多存了一个 `exePath`，应用没运行时也能显示图标，取不到就退回首字母占位。

### 5.2 间距（4px 栅格）

| 场景 | 值 | 来源 |
| --- | --- | --- |
| 页面四周留白 | `40,24,40,32`（窄窗口 `20,16,20,20`） | `PageRootStyle` / `FeaturePagesHost.ApplyDensity` |
| 内容列最大宽度 | `1000`，窗口更宽时**居中** | `FeaturePagesHost` 的列定义 + `PageColumns_SizeChanged` |
| 标题 → 副标题 | 6 | `PageHeaderTextStackStyle` |
| 表头 → 主体（所有页面） | 40 | `PageHeaderStyle` |
| 小节标题行高度（卡片组标题 / 列表工具条） | 40 | `PageSectionHeaderTextStyle` 的 `Padding 0,8` / `PageToolbarStyle` 的 `MinHeight` |
| 二级小节之间 | 32 | `PageToolbarStyle` / `PageSectionHeaderTextStyle` |
| 小节标题 → 内容 | 12 | 同上 |
| 卡片之间 | 4 | `PageCardStackStyle` |
| 表单字段组之间 | 16 | `PageFormStackStyle` |
| 字段标签 → 输入框 | 6 | `PageFieldStackStyle` |
| 一行里的控件之间 | 8 | `PageInlineControlsStyle` |
| 列表行内边距 | `8,10`（行高 48） | `PageListRowStyle` |

层级靠间距体现：**40（表头 → 主体） > 32（二级小节） > 16（组内） > 4（同类卡片）**。
主体里的第一个小节标题 / 工具条用 `…First…` 样式，避免和表头的间距叠加。

`DataTemplate` 内部同样不许写死 `Padding` / `FontSize`：列表行用 `PageListRowStyle` +
`PageListTitleTextStyle`，行首徽标用 `PageRowBadgeStyle` + `PageRowBadgeTextStyle`，
行尾强调按钮用 `PageRowChipButtonStyle`，分区磁贴用 `PageTileIconStyle` /
`PageTileTitleTextStyle` / `PageTileCaptionTextStyle`。

原来卡片页用 64、列表页用 40（`PageHeaderLooseStyle`），切页时第一块内容上下跳 24px；
现在每页主体都以小节标题行开头，由它接住表头，统一 40，`PageHeaderLooseStyle` 已删除。

表头与小节标题的对齐方式不同，别改混：
- 页面表头（标题 + 副标题）**顶对齐**（`PageHeaderTextStackStyle` 的 `VerticalAlignment=Top`），
  和下面第一个模块的上边缘成一条线；
- 小节标题（"分区""关联""规则"）在工具条里**上下居中**，因为它要和右侧命令按钮对齐；
  卡片组上方的独立小节标题用 `Padding 0,8` 撑到同样 40 高，效果相同。

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
列表工具条按钮的文字收起（只留图标，靠 ToolTip 说明）。标题栏只保留侧栏开关、应用图标
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
`RepeatButton` 设置隐式 `CornerRadius` 样式。页面里不再使用 `CommandBar`（它会接管命令项的默认样式，
让“新建”等按钮在悬浮时退回直角底框），列表工具条统一用 `PageToolbarButtonStyle`。

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
  每行图标数。

### 5.6 其它硬性约定

- 全部文案使用简体中文。
- 不要设 `Application.RequestedTheme`，主题走 `ThemeService` 应用到各窗口根元素。
- `ContentDialog` 必须指定 `XamlRoot`；每个窗口设置 `Assets\AppIcon.ico`。
- 新窗口实现 `IUiStyleShell` 并注册到 `UiStyleService`，用 `ApplyUiStyleSurfaces()` 刷新表面。
  **自己管理窗口背景的窗口（桌面卡片）只能用 `RegisterShell()`**，理由见 7.4。
- 必须以普通用户权限运行：管理员进程收不到资源管理器的文件拖放。

### 5.7 改完 UI 的检查清单

- [ ] 页面用的是 `PageRootStyle` + `PageHeaderStyle`（表头 Auto，主体 `*`）
- [ ] 主体第一块是小节标题行（`PageSectionHeaderFirstTextStyle` 或 `PageToolbarFirstStyle`），帮助内容在最后的「使用方法」小节
- [ ] 列表工具条是 `PageToolbarButtonStyle` 按钮 + 「更多」菜单，没有 `CommandBar`
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
  **取图标必须两条路都试**：`SHGFI_PIDL | SHGFI_ICON` 在本程序进程里对控制面板 / 回收站 /
  网络**都不返回句柄**（错误日志里有记录，但同样的调用在普通测试进程里是成功的），所以要退到
  `SHGFI_SYSICONINDEX` + `ImageList_GetIcon` 从系统图像列表取。
  **绝对不要再拿 `SHGetStockIconInfo` 兜底**：2026-09-12 之前那版就是这么写的，而且 SIID 给错了
  ——控制面板写成 22（`SIID_FIND`，放大镜）、此电脑写成 15（`SIID_SERVER`，服务器机箱），
  Shell 里**根本没有**「控制面板」对应的库存图标。结果卡片上的控制面板变成一个放大镜，
  用户一眼就看出不对。取不到就宁可空着，也别画一个错的。
- **`DesktopZoneMatcher`**：显式文件名 → 名称关键词 → 游戏协议 → 扩展名 / 文件夹 → 兜底分区，
  五级优先级。关键词以 `.` 开头视为扩展名；含 `steam://` 等协议的关键词优先匹配 `.url` 目标。
  **目录例外**：对文件夹，「文件夹」规则排在名称关键词之前。2026-09-12 用户反馈：桌面上
  一个叫「AI Project Unity」的工程目录因为名字含 `Unity` 被分进了「三维与引擎」；对文件夹
  来说，名字里出现软件名多半说明它是那个软件的工程 / 数据目录，不是软件本身。想让某个
  文件夹归到软件分区，拖过去即可——显式清单优先级最高，不受这条影响。
  **压缩包例外**：`.zip .rar .7z .tar .gz .iso .cab` 的扩展名规则同样排在名称关键词之前
  （前提是某个分区配了该扩展名）。2026-10-03 用户反馈：桌面上的「AI Project Unity.rar」
  因名字含 `Unity` 被分进「三维与引擎」，没进「文件夹与文件」。
- **同名去重按名称，不按路径**：安装程序常常同时往「当前用户桌面」和「公共桌面」写同名
  快捷方式（例如极空间，两个 `.lnk` 指向同一个 exe），按路径去重会让卡片里出现两个一模一样
  的图标。`DesktopFolders()` 把用户桌面排在前面，先到先得 = 保留用户桌面那一个。
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

它只听 `FileName | DirectoryName`（增删、改名），**不要加回 `Size` / `Changed`**：分组只看名字和
属性，已加载的图标按路径复用，文件内容变了卡片不会有任何变化。以前带着 `Size`，桌面上的文件
一写盘（下载、导出、Office 自动保存）就每块写入都往 UI 线程投一次事件，写完还要白同步一遍。

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
- **右键菜单**（2026-09-24 重做，`DesktopCardWindow.ItemMenu.cs`）：结构和文案对齐 Windows 11 新式菜单，
  外加一组卡片功能。
  - `CommandBarFlyout`（`AlwaysExpanded`）：顶部图标行是剪切 / 复制 / 重命名 / 共享（仅文件）/ 删除；
    列表按 Windows 的顺序排：打开、打开方式（普通文件）、以管理员身份运行（exe / bat / cmd / com / msc
    及指向它们的快捷方式）、打开文件所在的位置（快捷方式）、固定到“开始”、清空回收站（仅回收站）、
    压缩为 ZIP 文件、复制文件地址、属性、在终端中打开（文件夹，装了 Windows 终端时）。分隔线后是卡片功能：
    移动到分区（与拖到另一张卡片等效，系统图标按显示名归属）、在桌面文件夹中显示、恢复默认排序、刷新
    （重取本卡图标并同步）、分区设置。最后一项永远是「显示更多选项」，弹出 Shell `IContextMenu` 原生菜单，
    不能用跳转到资源管理器代替，否则发送到、各种第三方扩展会丢失。
  - **文案取自系统资源**（`Windows.UI.FileExplorer.dll.mui`、`shell32.dll.mui`、`windows.storage.dll.mui`、
    `explorerframe.dll.mui`），例如「复制文件地址」「固定到“开始”」「显示更多选项」「剪切(Ctrl+X)」，不要自己改说法。
  - 快捷键与资源管理器一致：Enter 打开、F2、Delete、Ctrl+C / Ctrl+X、Ctrl+Shift+C、Alt+Enter；
    菜单键出新式菜单，Shift+F10 直接出完整系统菜单（新式菜单里就标着它）。挂在 `ItemsGrid` 上，
    键盘发起的请求从获得焦点的 `GridViewItem` 冒泡，挂在模板元素上收不到。
  - **不要先枚举整套系统菜单再挑项**：第三方扩展太多，桌面 .lnk 实测冷启动 1.6 秒、之后每次约 0.95 秒。
    只有「固定到“开始”」「共享」借用系统扩展，且只创建那一个处理器（`ShellContextMenu.QueryHandler`，
    首次约 90ms、之后 1–15ms）；固定状态由处理器当场给出文字（已固定时是「从“开始”菜单取消固定」）。
    其余动作直接调 Shell API（`Services/ShellItemActions.cs`）。剪切 / 复制用 WinRT 剪贴板放入文件
    并标明移动 / 复制，资源管理器粘贴时照此处理。
  - 做不到、留给「显示更多选项」的：固定到任务栏（系统只允许资源管理器自己调用）、
    固定到快速访问 / 添加到收藏夹（取消固定的命令是动态提供的，拿不到可靠状态）。
  - 菜单**每次右键现建**，关闭即释放。**不要放回 DataTemplate**：每个图标各带一份菜单，
    71 个图标实测多占约 4MB。
- **重命名**（2026-09-18）：弹出一个 240dip 宽的输入框（回车确认、Esc 取消），不做资源管理器式
  的就地编辑——格子只有 76dip 宽看不全名字，按下去还会先被 GridView 当成拖动起手。编辑的是
  **不含扩展名**的名字（与卡片显示一致），提交时拼回原扩展名，`.lnk` 不会被改丢。实际改名走
  `SHFileOperation(FO_RENAME)`，但非法字符、重名、以空格/点结尾都先在托管侧校验，这样能给出
  不同的提示。改完要调 `CardItemOrderStore.Rename` 换掉顺序表里的旧文件名（顺序表按文件名记，
  不换的话图标会掉到同类末尾），再通知管理器同步——改名后可能按关键字匹配到别的分区了。
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
- **任务栏弹窗（悬浮暂存、音频设备）同样要保持 Mica**（2026-10-02）：点开后焦点常被任务栏抢回，窗口一失焦
  默认 `MicaBackdrop` 就退成灰色。这两个窗口在构造时调 `WindowHelper.EnablePersistentMica(this)`，此后
  `ApplyWindowBackdrop` 自动走显式 `MicaController` + 恒 `IsInputActive`；不要再手动给它们设 `SystemBackdrop`。
  遮罩要薄，否则 Mica 看不出来：窗口外层 `GetMicaPopupBrush` 只铺约 22% 黑，窗口里的内容面板用
  `GetMicaPanelBrush` / `CardBackgroundFillColorDefaultBrush`（白 5%），不要再铺不透明的 `CodexSurfaceBrush`。
- **任务栏弹窗动画不掉帧的约定**（2026-10-02，`PopupAnimator`）：动画由 `CompositionTarget.Rendering` 逐帧驱动
  （16ms 的 `DispatcherQueueTimer` 实际约 15.6ms 精度，帧间隔 16 / 31ms 交替会掉帧；33ms 定时器只做兜底）；
  窗口要在首次点击前预热（音频弹窗由信息条首次显示后 `WarmAudioPicker` 在空闲时创建并屏幕外初始化）；
  动画进行中不要改控件内容（音频弹窗等动画走完才填设备列表）；尺寸没变就别调 `AppWindow.Resize`。
- 音频弹窗的下拉框**不要先置灰再恢复**：预热时就读好设备列表，弹出时直接可选；后台刷新只在设备列表真的变了才原地更新（`_shownSignature`），否则会看到「灰色 → 突然可选」的跳变。
- **弹窗高度要以 DWM 可见边界为准**（2026-10-02 实测）：窗口矩形（`AppWindow.Size` / `GetWindowRect`）底部有一圈约 8 像素的不可见边框，
  按矩形高度对齐内容会让最下面的留白被裁掉。`FitHeightToContent` 用 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` 核对并向上加高。
  验证别靠肉眼：截图后沿窗口中线取像素，灰框底边下面应有约 8~10px 的窗口底色（深色下 28,30,33），而不是直接变成窗外的颜色。
- 音频弹窗布局：窗口标题 14 粗体 > 内层淡卡片面板（`CardBackgroundFillColorDefaultBrush`，层级靠它体现，别去掉）> 分组小标题（`PageCaptionTextStyle`，12 次要色，不能与标题同字号）> 下拉框。窗口高度由 `MeasureHeight` 按内容测量，状态文字与「更多声音设置」同一行（不改变高度）。
- 任务栏的「音频设备」入口恒显示、「记住所选设备」恒开启，设置页与弹窗里都没有对应开关。
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
- **"点到别处"不能只用卡片矩形判定**（2026-09-18 修）。九张卡片是九个独立 HWND，按矩形判
  会把这些明明还在操作卡片的动作误判成"点走了"，用户感受就是"刚呼出来，一点就整批消失"：
  - 卡片**之间的缝隙**（默认 gap 16dip）落在所有矩形之外——A/B 实测，旧逻辑点一下缝隙，
    置顶卡片数直接 9 → 0；
  - 右键菜单、工具提示是**独立顶层窗口**，位置大多超出卡片矩形，点菜单项 = 点到别处。

  判定改成三层：卡片矩形 → `WindowFromPoint` + 属主链（认出卡片自己的弹出窗口）→
  整批卡片的包围盒。前台窗口的判定同样要认弹出窗口（`IsOwnCardOrPopup`）。

### 7.6.1 图标的选中态

`GridView` 用 `SelectionMode="Single"`，单击选中并保留高亮，双击打开（`DoubleTapped`），
和 Windows 桌面一致。此前是 `SelectionMode="None"`，只有 hover 有反馈，鼠标一移开就什么都
看不出来，用户无法判断自己选中了哪个。三个配套细节：

- 选中底色必须自己覆盖 `GridViewItemBackgroundSelected` 系列主题资源（系统主题色 35% 透明）。
  默认值在 Mica 表面上几乎看不见。
- 九张卡片是九个独立 `GridView`，互不知情。卡片选中后要通过 `ItemSelected` 事件通知管理器
  把**其余卡片的选中清掉**，否则会同时亮好几个。收起整批卡片时一并清空。
- "点空白处取消选中"要用 **`PointerPressed` + `AddHandler(handledEventsToo: true)`**，
  不能用 `Tapped`：落在 `GridView` 空白区域的点击会被它内部的 ScrollViewer 当成平移手势吃掉，
  `Tapped` 一次都不会触发（实测埋点里只有点在图标上时才有记录）。

### 7.6.2 卡片尺寸：四边等距与按行自适应行高

2026-09-18 一次性修了三个互相牵连的尺寸问题，数字都是实测的：

- **窗口矩形 ≠ 可见客户区。** Win11 的窗口矩形外面还有一圈透明拖拽边框（125% 下左右各 9px、
  底部 9px），`AppWindow.Move/Resize` 操作的是窗口矩形。以前把 DIP 尺寸直接当窗口尺寸下发，
  等于内容区被吃掉一圈：最后一行的选中框底边被裁掉，卡片之间的可见间距也比设置值大了 18px。
  现在 `TargetWindowRect` 用 `GetWindowRect` / `GetClientRect` / `ClientToScreen` 量出这圈边框，
  把矩形**外扩**后下发，让客户区正好等于布局算出来的 DIP 矩形。
- **四边等距。** 宽度公式原来每列多给 `ItemMarginDip * 2`（8dip），可 `ItemContainerStyle` 早把
  GridViewItem 的 Margin/Padding 清零了，容器实测就是 76dip。多出的 24dip 因为面板左对齐全堆在
  右边——左留白 8、右留白 32。现在宽度 = 列数 × 76 + 2 × 8，XAML 的内边距四边统一为 8。
- **按行自适应行高**（`CardTilesPanel`）。`ItemsWrapGrid` 是均匀网格，所有格子必须按"两行
  文件名"留高度，单行的行下面就空一截。改用自定义面板：列宽固定，**每行高度取这一行最高的格子**，
  和 Windows 桌面一致。卡片高度因此算不出来，只能等面板量完再读（`ApplyMeasuredContentHeight`），
  读到后发 `SizeChangedByContent` 让管理器**只重排位置**——不能走 `ContentChanged`，那条路会重新
  刷内容 → 重新测量 → 又报高度变化，两边互相喂事件停不下来。WinUI 会把每个格子的高度向上取整到
  整像素（86dip@125% = 107.5px → 实占 108px），测量值天然包含这点，不会再裁最后一行。

换面板的代价是**卡片内拖动排序必须自己做**：内置重排只认 `ItemsWrapGrid` / `ItemsStackPanel`，
换成自定义面板后拖得起来、松手不插入（实现 `IInsertionPanel` 也不够）。现在在 Drop 里用
`CardTilesPanel.GetInsertionIndex` 算插入点、直接 `_items.Move`。**`CanReorderItems` 仍须保持
True**——设成 False 后拖动会话不再把 DragOver/Drop 发回源列表（埋点只剩 Starting →
Completed(None)），自己处理也收不到。合成鼠标输入能驱动这条链路，可以用来做回归测试
（旧版作对照组时同一套拖动能重排）。

### 7.6.3 放不下时逐级收紧（`DesktopCardLayout.Fit`）

分辨率 / 缩放变小后，所有卡片可能排不进工作区。以前只会"一列排不下就新开一列"，列数用完后卡片
重叠或挤出屏幕。现在每一级只在上一级放不下时才启用，空间够了自动回到设置值（设置本身不改写）：

1. 严格按顺序排（原行为，放得下就一点不变）；
2. 见缝插针：后面的卡片优先塞进前面某一列底部的空位；
3. 间距与边距收到 12、再收到 8（取 `min(设置值, 档位)`，不会比设置值更大）；
4. 卡片限高：从一整列高度起每档 40 DIP 往下压（最矮 `MinCardHeight`=160），直到放得下；被压矮的卡片
   在内部滚动（`GridView` 的 `VerticalScrollBarVisibility=Auto`）。任何一级下，高于一整列的卡片都会被压到一列高。

- 排版按 `DesktopCardWindow.NaturalDipHeight`（内容完全展开的高度）算，限高通过 `SetHeightCap`
  记在卡片上，`DipHeight = min(自然高度, 限高)`，随后由 `PlaceAt` 和位置一起落到窗口上，
  所以仍然是一次 `MoveAndResize`，显示变化重排的「一张一让步」流程不受影响。
- 测量值（`ApplyMeasuredContentHeight`）只更新自然高度。`GridView` 的滚动条设为 Auto 而不是
  Disabled：Disabled 会让面板按视口高度测量，限高后读到的"内容高度"就被压缩了。
- 回归：`dotnet run --project tests/DesktopCardLayout.Tests`（含 `Fit` 各级与不重叠 / 不出屏断言）。
  2026-10-03 用 900×600 DIP 的模拟工作区实测：限高卡片可滚轮滚动，正常分辨率排版与旧版一致。

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
`DesktopCardManager.RelayoutForDisplayChangeAsync()`。四条硬性要求：

- **必须防抖，且不能太短（现为 900ms）**。改一次分辨率 Windows 会连发好几条消息，
  远程控制软件（ToDesk / 向日葵之类）改分辨率时更是分好几步落定；防抖太短就会把一次
  改动拆成好几轮重排。
- **必须一张卡片一让步（`await Task.Yield()`），不能一口气排完。** 分辨率刚变完的那
  几秒里，一次 `MoveAndResize` 要同步走完 DWM 合成 + XAML 重排，实测 9 张 Mica 卡片
  连着做会占住 UI 线程 **11.6 秒**（2026-09-17 实测，`WinTools-startup-trace.txt`）。
  消息泵一停，Windows 就判定进程「未响应」并结束它——事件查看器里是 `AppHangB1`，
  用户看到的是「改完分辨率程序就闪退」。这是本节最重要的一条。
- **缩放只认 `GetDpiForMonitor`（主显示器有效 DPI）。** 另外两个来源在过渡期都会骗人：
  `XamlRoot.RasterizationScale` 要等 WinUI 下一帧；`GetDpiForWindow` 是**每个窗口各自**
  处理完 `WM_DPICHANGED` 才更新，于是同一轮排版里前几张卡片读到旧缩放、后几张读到新的，
  卡片被分成两组按两种尺寸摆。用错缩放算出来的像素尺寸会被永久写死在窗口上，表现就是
  卡片变窄、每行少一个图标。
- 位置和尺寸合并成一次 `AppWindow.MoveAndResize`，且目标值与当前值相同时直接跳过；
  排完一遍后再比一次显示签名（工作区 + 缩放），稳定了还要补跑一遍收尾——过渡态的尺寸
  不能留在窗口上。
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

### 语音小球

文本框处于输入状态时，在**输入光标正下方**（放不下就正上方）显示麦克风小球，点一下模拟按下设置里的
语音快捷键（默认 `Win+H`，可填输入法的组合，允许只有修饰键，如 `Ctrl+Win+Shift`），再点一下再按一次。默认关闭。

- 全部在 `VoiceBallService` 的一条 MTA 后台线程：WinEvent（焦点 / 前台 / 拖动窗口）防抖 80ms 判定；
  隐藏时每 500ms 轮询前台有没有输入光标（浏览器页面内换输入框不一定发焦点事件）；显示时每 150ms 跟随光标。
  同一行内光标移动不超过三个小球宽度不跟，避免打字时小球每个字跳一下。
- **正在听（小球点亮）时不跟随光标**：语音转写持续往框里写字，跟着跑既晃眼又点不准。
- **拖动记位置**：未点亮时按住拖动（超过系统拖动阈值才算拖），松手把「小球中心相对光标底端」的偏移
  （DIP，按 100% 缩放计）写进配置 `voiceBallCustomOffset / voiceBallOffsetX / voiceBallOffsetY`；之后按该偏移摆放，
  下方超出屏幕时整体翻到光标上方。设置页「小球位置」卡片用 1/2 比例示意图标出位置，「重置」恢复默认。
  拖动回调来自小球后台线程，`App` 用启动时记下的 UI 线程 `DispatcherQueue` 切回，不能在后台线程访问 `Window.DispatcherQueue`。
- **判定依据是「有输入光标」，UIA 只负责排除**（密码框、只读、禁用）。踩过的坑：第一版只认 UIA 的 Edit /
  可写 Value+Text，结果几乎只有 Claude 能出小球——2026-09-14 实测 ChatGPT 桌面版输入框在 UIA 里是 Group，
  没有文本模式。光标来源按精度：UIA TextPattern2.GetCaretRange → MSAA `OBJID_CARET` → `GetGUIThreadInfo`
  系统光标（`Services/FocusedTextProbe`）。记事本走 UIA，Edge 地址栏 / ChatGPT 走 MSAA，均实测可用。
- **小球必须是纯 Win32 分层窗口**（`WS_EX_NOACTIVATE` + `MA_NOACTIVATE`），不要换成 WinUI 窗口：
  点击时焦点必须留在原文本框，快捷键才会发到正确的程序。实测记事本点击后前台不变，`Win+H` 面板正常弹出。
- **Mica 底色是算出来的**（`Services/MicaColorSampler`）：系统 Mica 只在窗口激活时显示材质，而小球永远不激活。
  取壁纸在小球所在屏幕位置的平均色，保留色相 / 饱和度、亮度换成 Mica 色调，再按深色 `#202020`×0.8、
  浅色 `#F3F3F3`×0.5 混合；外加 Win11 的 1px 渐变描边、柔和投影、悬停 / 按下叠色，正在听时换强调色 + 脉冲圈。
- 诊断：`%TEMP%\WinTools-voiceball-trace.on` 存在时（需重启程序），每次判定结果与原因写入
  `%TEMP%\WinTools-voiceball-trace.txt`。
- 小球状态靠点击次数自行记录，用别的方式结束语音后可能与输入法实际状态不一致；焦点离开原窗口时重置。

### 侧栏状态圆点

`UpdateNavStatusIndicators()` 里每个圆点**只反映该页的总开关**。不要再写"或者某个子功能还开着"
这类复合条件，避免页面关闭后圆点却还亮着。
实测：同一像素点从 `#4CC2FF`（开）变成 `#777A83`（关）。

### 托盘菜单

**左键单击和双击打开主窗口，右键弹这个菜单**（2026-10-02 按用户要求改回，此前是单击也弹菜单）。
单击不等双击判定（`NoLeftClickDelay = true`），双击会再触发一次「显示主窗口」，该操作是幂等的。菜单里仍保留「显示主界面」。

**打开动画方向（2026-10-02）**：H.NotifyIcon 2.2.0 的第二窗口菜单固定用 `Full` 放置，动画自上而下；任务栏在底部时应当从下往上滑出。
`FixMenuWindowSize` 里反射取库内部的 `ContextMenuFlyout`，菜单在光标上方时改成 `Top` 放置，并把宿主窗口移到菜单正下方
（弹出层位置只取决于宿主窗口上沿）。宿主窗口不能整体越出屏幕下沿，否则 WinUI 弹不出菜单——所以把它压扁到屏幕内剩余高度，
剩余不足 8px 就退回 `Full`。实测（逐帧截图）上沿从 144→60 上升、下沿不动；顺带菜单不再压住任务栏右侧的时钟。
库 2.5.0-beta 起自带同样的处理，升级后这段要重新核对（见 `ContextMenuFlyout` 属性与窗口位置是否冲突）。

菜单分三组，顺序按使用频率，退出永远在最后：

| 组 | 项 |
| --- | --- |
| 打开 | 悬浮搜索（右侧标出全局快捷键）、悬浮暂存、显示主窗口 |
| 桌面分区 | 桌面分区（`ToggleMenuFlyoutItem`，勾选 = 已开启）、同步桌面 |
| 程序 | 设置…、退出 WinTools |

菜单**只在启动时构建一次**，而配置随时会变，所以动态部分必须在每次弹出前由
`RefreshMenuState()` 重算，不能在 `BuildContextMenu` 里写死：

- 「桌面分区」的勾选状态、「同步桌面」的置灰（分区没开时同步无意义，而托盘菜单没有
  可用的 `XamlRoot`，弹不了对话框，只能置灰）；
- 「悬浮搜索」右侧的快捷键文字（`KeyboardAcceleratorTextOverride`），功能关闭时显示「已关闭」。

关掉「桌面分区」会连带把系统的「显示桌面图标」还回去（见 `DesktopCardManager.SetEnabled`），
所以这一项同时也是「我想看回桌面图标」的快捷开关。托盘改动作后要同步设置页那个
`ToggleSwitch`（`MainWindow.SetDesktopCardsEnabledFromTray`），否则用户下次打开设置页会看到相反状态。

位置与动画的坑见下面两段——那是历史遗留，别重复踩。

### 清理失效快捷方式

桌面分区页「更多」→「清理失效快捷方式」：扫出目标已不存在的桌面 `.lnk`，
**列出来（含原目标路径）让用户确认后**再一起删到回收站。

- **绝不自动删**。卸载软件留下的死快捷方式和"目标暂时读不到"从文件系统层面看是一样的。
- 误判防护（`Services/BrokenShortcutScanner`）：目标在可移动磁盘 / 网络位置 / UNC 路径 /
  未就绪的卷上，一律跳过；`TargetPath` 为空的（指向 Shell 命名空间对象，如控制面板项）也跳过。
  宁可漏报，不能误报。
- 只看 `.lnk`。`.url` 指向网址，没有「目标文件存在与否」可言。
- 解析快捷方式要 STA（`WScript.Shell` 的约束，和图标解析同源），所以扫描内部自己起一条
  STA 线程；调用方用 `Task.Run` 包一层即可，别在 UI 线程直接调。
- 删除走 `DesktopCollectService.DeleteToRecycleBin`（`FOF_ALLOWUNDO`），用户能在回收站还原。
  2026-09-12 实测：造一个指向不存在路径的 `.lnk` → 对话框正确列出 → 删除后桌面上消失、
  回收站里能找到。

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

### 内存与 CPU 基线（2026-09-24）

Debug 版，同一台机器交替各跑 2 轮，启动后第 40 秒读数（9 个分区、71 个桌面项目，卡片靠左）：

| 版本 | 私有内存 | 句柄 | WinUI 窗口 | 启动 CPU |
| --- | --- | --- | --- | --- |
| 不创建卡片（对照） | 135 MB | ~1,700 | 3 | 2.8 s |
| 当前：9 张卡片，分区库复用卡片，右键菜单共用 | 176 MB | ~2,420 | 12 | 5.0 s |
| 当前改回每个图标各带一份右键菜单 | 180 MB | ~2,400 | 12 | 6.0 s |
| 再加上另建 9 个分区库隐藏窗口（= 本次改动前） | 201 MB | ~2,880 | 21 | 7.2 s |

- **托管堆只有约 9MB**（`dotnet-dump` 的 `eeheap -gc`），其余全是 WinUI / 合成的原生内存，
  所以抠集合、缓存这类托管对象没有意义；**每多一个卡片窗口约 2.5MB、50 个句柄**，窗口数才是大头。
- 卡片空闲时不耗 CPU（没有常驻定时器）。整个进程空闲约 0.6% 单核，动鼠标时会升到 2~3%，
  来自「点击桌面」的 `WH_MOUSE_LL` 钩子和语音小球的 WinEvent 钩子，每个鼠标事件都要切到钩子线程。
- 测内存看**私有提交（Private Bytes）**，不要看工作集：整机内存紧张时系统会把工作集换出，
  任务管理器里只剩几 MB，看起来很省，其实提交量没变。
- 图标提示框（每个图标一个 `ToolTip`）约 2MB，延迟创建会导致第一次悬停不弹提示，没做。

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

### 任务栏信息（2026-09-19）

功能图标由 `FeatureIcons.cs` 统一定义，沿用 [Segoe Fluent Icons](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)，缺少该字体时回退 MDL2。侧栏、托盘菜单、搜索与暂存浮窗、语音小球和任务栏使用同一功能映射；桌面卡片用分区网格、暂存用图钉、关联用链接、语音用麦克风；音频设备选择独立为宽文字按钮，不混入功能图标。通用新增、删除、刷新等动作保留原有标准图标。原生任务栏缓存字体矢量轮廓，只在 DPI 改变时重建，并在服务释放时回收。

左侧独立「任务栏信息」功能页：默认开启 Codex / Claude 剩余额度，可整体关闭；不再提供显示内容或自定义文字选项。显示位置支持偏左、居中两个选项，默认偏左；透明线框，不提供底色或偏移设置。旧版位置字段被忽略，新位置使用 taskbarInfoAlignment 保存。主屏幕底部横向任务栏隐藏或前台全屏时，左侧信息也会隐藏。左侧仍是独立分层窗口，不注入 Explorer；信息区域响应点击，不抢前台输入焦点。
左侧窗口通过 popup owner 跟随任务栏层级；仅在显示或位置尺寸变化时定位，不再每秒重新置顶，避免点击任务栏时反复遮挡。隐藏操作也仅在可见状态改变时执行。

快捷图标默认开启：四宫格调用现有 ShowDesktopCardsFromTray 呼出图标库，不最小化应用或切回桌面；
快捷按钮排列在信息区最左侧，额度或自定义文字在右侧；鼠标悬停时仅对应按钮显示圆角底框，移出即清除，仅悬停目标改变时重绘。
四宫格按钮再次点击会收起左侧卡片；外部点击检测忽略该按钮，避免按下时收起、松开时又打开。
设备入口为耳麦图标 + 设备文字按钮，设备与 Codex / 自定义信息使用无描边柔和底框；信息框按文字测量宽度，最长 240 DIP。弹窗移除额外边线和系统描边，保留真实 Mica；任务栏分层窗口的底框是半透明绘制效果。三个功能图标在最左侧。设备选择使用 WinUI + Mica 面板，扬声器与麦克风分别下拉选择，显示当前普通默认设备；选择后同时更新普通与通话默认并重新读取验证。后台枚举，不启动常驻轮询；失去焦点或 Esc 收起，提供声音设置入口。独立指定设备的应用仍使用其应用内设置。
设备枚举采用 [Windows Core Audio](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immdeviceenumerator-getdefaultaudioendpoint)；默认设备写入使用非公开 IPolicyConfig 接口（[接口定义参考](https://github.com/frgnca/AudioDeviceCmdlets/blob/master/SOURCE/IPolicyConfig.cs)），异常会明确提示，菜单提供 Windows 声音设置入口。
只读音频集成检查：`dotnet run --project tests/AudioDevice.Tests`，验证设备标识、默认项及切换接口可用性，不实际修改设备。
收纳图标打开悬浮暂存；麦克风图标执行语音小球设置的快捷键（默认 Win+H），不抢输入焦点，关闭小球后仍可使用。图标为随 DPI 缩放的矢量线条。左键点击信息文字不弹菜单，右键打开托盘菜单，
可呼出悬浮搜索、桌面分区与刷新额度；菜单不再提供「任务栏信息」或「显示 / 恢复桌面」项。
设置页不再单列「快捷按钮」卡片，保留独立任务栏信息页中的额度与外观选项。
左侧信息区弹出的菜单向上、向右展开；从四宫格或该菜单呼出桌面图标库时，按需创建一组
独立的左侧浮层窗口，满列后向右排。常驻桌面卡片的左右位置由桌面分区页「卡片布局 → 位置」决定
（`desktopCardAlignment`，`"right"` 默认 / `"left"`，靠左时从左上角排、满列后向右排）。
**卡片靠左时不另建左侧浮层**：两组位置完全重合，四宫格 / 托盘菜单直接把常驻卡片抬到最前
（`LibraryReusesDesktopCards`），同样不播动画，再次点击或点到外面就放回桌面；切到靠左时释放
已预备的浮层窗口，切回靠右时重新预备。实测省下 9 个 WinUI 窗口、约 22MB 私有内存（见第 10 节）。
卡片关闭时从左侧呼出，仍按下文单独建浮层。
左侧浮层直接显示、直接收起，不播放过渡动画；点击浮层外部后将其隐藏，再次打开复用窗口；浮层不更改桌面图标显隐或桌面布局缓存。
日常收起使用 DWM 遮蔽，保留已准备的窗口表面；内容不变时再次打开跳过 Show 和渲染等待，整组解除遮蔽。
两组共用分区配置、排序存储和桌面监视器，同步时复用一次桌面枚举结果；排序和文件变动
通知两组同步。卡片靠右时，右侧桌面完成显示后，左侧窗口逐张提前创建并隐藏，点击时直接显示；内容变化后更新隐藏窗口，主管理器释放时一起回收。悬浮暂存保持原来的位置逻辑。
左侧打开时复用右侧现有分组快照、项目对象和已解码图标；两侧的窗口、集合和选中态独立。
内容未改变时跳过列表重建；左侧整组等待共享图标准备及统一渲染屏障后一起解除遮蔽，避免逐张出现和图标后闪；打开耗时写入 DesktopLibrary.Open 日志。
卡片内容变化采用增量插入、移动、删除，保留未变项目的界面容器；左侧复用右侧排序结果，避免重复查询 Shell 名称和文件类型。隐藏预备阶段也完成布局，布局快照不变时跳过磁盘读写。

布局回归：`dotnet run --project tests/DesktopCardLayout.Tests`，覆盖左右独立计算、列换行、
桌面原位置保留、负坐标屏幕和空列表。窗口交互仍按用户要求不做界面自动验收。
选择「仅系统托盘」会隐藏左侧窗口，但继续刷新额度；悬停 WinTools 原生托盘图标查看额度、
重置时间和更新时间，点击图标打开菜单。托盘位置由 Windows 管理，图标可能在折叠区。
这两种模式共用一个额度读取服务。左侧快捷按钮没有独立键盘焦点，可通过原生托盘菜单访问同样的动作。

2026-09-19：上述模式和快捷入口已通过 Release 编译；遵照用户要求不进行界面自动操作，
底色观感、按钮点击与自动隐藏效果仍需用户实际确认。

`TaskbarInfoService.cs` 管理显示与两分钟刷新，`MainWindow.TaskbarInfo.cs` 管理设置。

任务栏快捷入口：「任务栏信息」页的「快捷入口」可分别开关桌面库、语音小球、音频设备三个入口（配置项 `taskbarShowLibrary` / `taskbarShowStash` / `taskbarShowVoice` / `taskbarShowAudio`，默认全开）。桌面库入口还要求桌面分区已开启；语音小球的入口独立于功能本身，功能关着也能单独显示在任务栏（语音入口直接发送快捷键）。**悬浮暂存没有单独的入口开关（2026-09-30）**：「悬浮暂存」功能开关就是任务栏暂存图标的开关，开则显示图标并可拖文件到图标上方弹出的窗口，关则图标消失；`taskbarShowStash` 配置项已不再使用。入口附带状态：暂存图标右上角显示暂存文件数，语音图标在听写中显示红点，音频入口显示当前默认输出设备名。

小分辨率避让：`TaskbarInfoService.Layout.cs` 每 3 秒（任务栏尺寸变化时立即）用 UI Automation 只读扫描任务栏上的图标位置，把信息区放进图标之间的空隙，放不下时按级别收缩：完整 → 额度文字缩成百分比 → 音频只留图标 → 去掉 Claude 气泡 → 去掉全部额度 → 折叠成一个按钮（点击打开托盘快捷菜单）。空隙优先选放得下额度气泡的；扫描失败时回退到「不超过任务栏一半宽度」的旧规则。Claude 未连接时不占位。
**首次扫描完成前不显示信息条**（`_scanSettled`，2026-10-02）：此前启动 / 资源管理器刚重启时，扫描还没返回就用旧规则贴在任务栏最左边，压在小组件等系统图标上，下一次扫描才跳开。现在扫描完成才摆放并立刻重排；UIA 连续 3 次返回空（任务栏还没填充）或抛异常才视为读不到并回退旧规则。
`Services/CodexQuotaReader.cs` 启动本机 `codex.exe app-server`，完成握手后只调用
`account/rateLimits/read`，请求结束即回收进程；不会创建模型任务或消费重置额度。
优先显示 `rateLimitsByLimitId.codex`，兼容旧 `rateLimits`；按真实周期标注，缺失窗口不虚构。
接口依据：[OpenAI App Server 文档](https://developers.openai.com/codex/app-server)。
需要已安装并登录的 Codex 桌面版或 PATH 上的原生 CLI。失败显示不可用，详细原因在设置页。

验证：`dotnet run --project tests/TaskbarInfo.Tests`；加 `-- --live` 可验证本机实际额度。
覆盖通用额度桶优先级、旧格式、空字段、周窗口单独存在、百分比边界与其他额度桶隔离。

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
| `VoiceBallService` / `Services/FocusedTextProbe.cs` / `Services/MicaColorSampler.cs` | 语音小球：光标跟踪、Mica 小球绘制与快捷键模拟 |
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

设备浮层外观补充：任务栏设备与 Codex 采用透明圆角细线框，不填底色；设备弹窗恢复最初的默认 MicaBackdrop，不设置 TintOpacity、LuminosityOpacity 或着色覆盖；不要与 DesktopAcrylicBackdrop 混用。标题使用不带章节外边距的样式，避免滚动和裁切。已通过限定范围实际截图检查（artifacts/ui-check/outline-default.png），未选择音频设备。

音频入口支持再次点击收起；弹窗打开期间监听外部鼠标按下，排除入口按钮和自身下拉菜单，收起即卸载监听。线框高度 34 DIP；图标组左内距 8 DIP、按钮宽 36 DIP、到音频入口间隔 4 DIP；耳麦与音频设备文字采用固定间距。已实际验证切换收起与点击 WinTools 信息区收起，并截图检查间距。

任务栏信息页新增 Codex 验证和 Claude / Claude Code 验证。Codex 使用官方 app-server `account/login/start`，在 Chrome 打开服务返回的授权网址；保持 app-server 存活等待 `account/login/completed`，5 分钟超时，可取消；成功后返回主窗口并读取额度。需要本机已安装 Codex 与 Chrome。不会为检测启动模型会话。

Claude 使用独立 OAuth + PKCE 的兼容流程（参考 https://github.com/ipangdz/claudexbar/blob/main/docs/AUTH.md ），在 Chrome 手动授权后，将官方回调页显示的 `code#state` 粘贴回软件。软件校验 state 与 5 分钟有效期，向固定的 platform.claude.com 令牌端点换取凭证，并向 api.anthropic.com/api/oauth/usage 验证权限。验证成功后凭证保存到 Windows PasswordVault 的 WinTools.ClaudeQuota 项；后台每 5 分钟查询，并在到期时刷新独立凭证。断开只删除 WinTools 的凭据，不影响浏览器或 Claude。此兼容登录使用 Claude Code 的公共客户端及其权限范围；没有官方 WinTools 集成保证，服务端变更可能使它不可用。实际账户授权需用户手动完成。

上述操作不修改 Claude 配置，不安装状态栏脚本，不扫描浏览器 Cookie；旧的 Claude statusLine 缓存方案已移除。检测失败不会显示伪造的百分比。Claude 气泡与 Codex 分开，支持五小时和每周剩余额度，兼容传统字段与 limits 数组；不使用 context_window 或本地 token 总数估算订阅额度。授权码、令牌及完整授权网址不写入日志。
Codex 登录与额度子进程现在继承已启用的 Windows 静态 HTTPS 代理（统一代理或 https= 条目），只在没有显式代理环境变量时补充；不修改系统设置。localhost 回调加入子进程 NO_PROXY。换取凭证失败时提示检查代理并重新授权，不直接展示可能含敏感内容的服务端错误。

Codex 和 Claude 气泡在未连接、读取失败、超时或没有有效额度窗口时，统一显示「额度暂不可用」；实际剩余额度为 0 时仍显示 0%，具体失败原因保留在验证设置中。

任务栏设置将 Codex 额度与验证合并为同一卡片，只保留 Codex、Claude 两个账户区域；刷新并检测与浏览器授权放在各自卡片中。授权/检测期间，后台额度刷新不会覆盖正在进行的状态提示。

任务栏信息页沿用共享页面标题、设置卡片、账户图标和表单样式；入口精简为连接、刷新、断开。卡片优先显示额度摘要，重置时间和诊断详情放入悬停提示；Claude 授权表单只在连接时展开，使用主题卡片和强调按钮。

额度气泡悬停显示刷新（宽度按额度原文保持），单击只刷新相应账户，进行中显示刷新中并合并重复点击。托盘提示固定为 WinTools；单击/右键显示菜单，双击打开主界面，单击采用系统双击等待以避免抢先弹菜单。

悬浮暂存与任务栏快捷图标联动（2026-09-23）：任务栏「悬浮暂存」图标可见时，拖文件弹出与点击图标打开都固定在图标正上方（水平以图标为中心、越界贴边，偏左布局即左下角，居中布局随之居中），窗口变大时底边不动、向上生长；用户手动拖动窗口后不再拉回。**2026-09-30 起暂存窗口不再跟随鼠标**：无论功能开关，窗口只出现在任务栏「悬浮暂存」图标正上方；图标不可见（覆盖层隐藏、全屏、入口关闭）时固定在屏幕右上角。设置页已删除「跟随鼠标时的位置」，配置项 `stashOffsetX/Y` 与 `MoveBelowCursor` 已移除。程序选择窗口始终叠放在暂存窗口正上方。

拖拽来源判断（2026-10-03）：`FloatingStashManager.ShowForDrag` 除了要求按下点在资源管理器文件列表里，还要求**不在空白处**——在没有文件的地方按下再拖是拉框选择，不是文件拖放，不能弹暂存窗。桌面用 `LVM_HITTEST`（`IsDesktopBlankArea`）；文件夹窗口（`CabinetWClass`）的列表是 DirectUI，没有 SysListView32，改用 UI Automation 的 `ElementFromPoint`（`Services/ExplorerItemProbe.cs`）：空白处是 `List`（类名 `UIItemsView`），文件 / 文件夹是 `ListItem`（`UIItem`），文件名那截是 `Edit`（`UIProperty`）。UIA 任何失败都按「不是空白」处理，宁可多弹也不漏掉真正的文件拖拽。实测方法：`mouse_event(0x8001, …)` 绝对坐标按下 + 分步移动，枚举顶层窗口里标题为「悬浮暂存」且可见的窗口；注意 PowerShell 函数别叫 `Move`（会变成 Move-Item）。

暂存图标与托盘菜单再次点击会收起暂存窗口（保留已暂存的文件），与音频按钮一致。点击打开的暂存窗口和音频设备弹窗共用 `PopupAnimator`：250ms 减速滑入淡入、150ms 加速下沉淡出，透明度通过仅在动画期间挂上的 WS_EX_LAYERED 实现（与 Mica 不能共存）。拖拽中弹出的暂存窗口仍不播放动画，避免拖动预览闪动。两个弹窗下沿到任务栏上沿统一为 12 DIP（`WindowHelper.TaskbarPopupGapDip`）。

暂存窗口尺寸与偏移改为读写共享的 `SettingsService.Current`：此前尺寸绕过它直接写文件，设置页任何改动或退出时整份保存 Current 都会把尺寸覆盖回旧值。Codex 额度自动刷新间隔由 2 分钟改为 5 分钟（每次读取都要启动 codex app-server 子进程），与 Claude 一致。

快捷键录制（2026-09-30）：悬浮搜索、桌面分区、语音小球的快捷键输入框都是「录制」模式（`HotkeyRecorder.cs`）：聚焦后直接按组合键，Esc 取消、退格 / Delete 清除。用 `WH_KEYBOARD_LL` 钩子而不是 KeyDown，因为 Win、Alt+Space 在窗口层面拦不住；钩子只在输入框获得焦点期间存在。只有语音小球允许纯修饰键组合（如 Ctrl+Win）；其余要求至少一个修饰键（F 键等除外）。

任务栏信息条自扫描图标位置时必须排除本进程的元素（UIA 树里会含有它自己），否则会在左右两个空隙之间每 3 秒横跳一次。

卡片项目的拖动数据（2026-09-30）：`DesktopCardWindow.ItemsGrid_DragItemsStarting` 除了自定义格式 `WinTools.DesktopCardItem`，还必须带真实的 `StorageItems`（`SetDataProvider` 延迟取）并声明 `Move | Copy`。只声明 Move 且没有文件项时，暂存窗口、资源管理器等目标全部显示「禁止」光标。卡片之间换区 / 排序的目标在 DragOver 里仍返回 Move。

暂存窗口高度随文件数自适应（2026-10-02）：高度 = 固定部分 54 DIP + 文件数（最多 6 行，超过后列表内滚动）× 50 DIP；宽度仍可拖动并保存，高度不再持久化。窗口底部不再有提示文字。音频设备弹窗与暂存窗口同一版式（36 高标题栏 + 左右下等距 8 的内卡片，圆角 8）；任务栏音频入口始终是只有耳机图标的圆形按钮，不再显示设备名，也不随空间分级变化。暂存窗口内框圆角由 XAML 控制，代码里不要再 `ContentBorder.CornerRadius = ...` 覆盖。

无响应排查（2026-10-02）：Windows 的 AppHang 报告（事件日志 Application，ID 1002 / 1001，事件名 AppHangB1）只说明「界面线程卡了」，不含堆栈。`Services/UiHangWatchdog.cs` 每 2 秒给界面线程投心跳，超过 8 秒没回应就把进程完整转储到 `%LocalAppData%\WinTools\hangs\hang-*.dmp`（保留 3 份、10 分钟一份）；用 `dotnet-dump analyze` 的 `clrstack` / `threads` 看界面线程卡在哪。`HotkeyRecorder` 的低级键盘钩子在前台窗口不是本进程时必须自动卸载，否则会吞掉全系统的按键。
