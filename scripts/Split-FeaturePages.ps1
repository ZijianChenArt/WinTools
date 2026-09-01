param(
    [string]$ProjectDir = (Join-Path $PSScriptRoot '..\src\WinTools')
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
$xamlPath = Join-Path $ProjectDir 'MainWindow.xaml'
$codePath = Join-Path $ProjectDir 'MainWindow.xaml.cs'
$xaml = [IO.File]::ReadAllText($xamlPath)
$code = [IO.File]::ReadAllText($codePath)
$hostXamlPath = Join-Path $ProjectDir 'FeaturePagesHost.xaml'

$contentMarker = '                            <!-- 快捷设置 内容 -->'
$contentStart = $xaml.IndexOf($contentMarker, [StringComparison]::Ordinal)
if ($contentStart -ge 0) {
    $borderClose = $xaml.IndexOf("            </Border>`r`n        </NavigationView>", $contentStart, [StringComparison]::Ordinal)
    if ($borderClose -lt 0) { throw '找不到内容框结束标记。' }
    $viewportClose = $xaml.LastIndexOf("                        </Grid>`r`n", $borderClose, [StringComparison]::Ordinal)
    if ($viewportClose -lt $contentStart) { throw '找不到页面视口结束标记。' }

    $featureContent = $xaml.Substring($contentStart, $viewportClose - $contentStart).Trim()
    $styleStart = $xaml.IndexOf('            <!-- Codex / ChatGPT 风格的克制排版', [StringComparison]::Ordinal)
    $styleEnd = $xaml.IndexOf('        </Grid.Resources>', $styleStart, [StringComparison]::Ordinal)
    if ($styleStart -lt 0 -or $styleEnd -lt 0) { throw '找不到页面样式。' }
    $pageStyles = $xaml.Substring($styleStart, $styleEnd - $styleStart).Trim()

    $hostXaml = @"
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="WinTools.FeaturePagesHost"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:WinTools"
    HorizontalContentAlignment="Stretch"
    VerticalContentAlignment="Stretch">
    <UserControl.Resources>
$pageStyles
    </UserControl.Resources>
    <Grid HorizontalAlignment="Stretch" VerticalAlignment="Stretch">
$featureContent
    </Grid>
</UserControl>
"@
    [IO.File]::WriteAllText($hostXamlPath, $hostXaml, $utf8)
    $replacement = '                            <local:FeaturePagesHost x:Name="FeaturePages"/>' + "`r`n"
    $mainXaml = $xaml.Substring(0, $contentStart) + $replacement + $xaml.Substring($viewportClose)
    [IO.File]::WriteAllText($xamlPath, $mainXaml, $utf8)
}
elseif (Test-Path -LiteralPath $hostXamlPath) {
    $featureContent = [IO.File]::ReadAllText($hostXamlPath)
}
else {
    throw '找不到功能页内容。'
}

$eventPattern = '\s(?:Click|Toggled|SelectionChanged|ValueChanged|TextChanged|DragOver|Drop|DragItemsCompleted|DoubleTapped)="([A-Za-z0-9_]+)"'
$eventNames = [regex]::Matches($featureContent, $eventPattern) |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$relayMethods = [Collections.Generic.List[string]]::new()
foreach ($name in $eventNames) {
    $methodPattern = "(?m)^    private (?:(?:async) )?void $([regex]::Escape($name))\(([^\r\n]+)\)"
    $match = [regex]::Match($code, $methodPattern)
    if (-not $match.Success) { throw "找不到事件处理方法：$name" }
    $parameters = $match.Groups[1].Value
    $arguments = ($parameters -split ',') | ForEach-Object { ($_ -split '\s+')[-1] }
    $relayMethods.Add("    private void $name($parameters) => Owner?.$name($($arguments -join ', '));")
    $code = [regex]::Replace($code, "(?m)^    private (?=((?:async )?void $([regex]::Escape($name))\())", '    internal ', 1)
}

$controlMatches = [regex]::Matches($featureContent, '<(?<type>[A-Za-z][A-Za-z0-9]*)\b[^>]*?\bx:Name="(?<name>[A-Za-z][A-Za-z0-9_]*)"', 'Singleline')
$aliases = [Collections.Generic.List[string]]::new()
foreach ($match in $controlMatches) {
    $type = $match.Groups['type'].Value
    $name = $match.Groups['name'].Value
    $aliases.Add("    private $type $name => FeaturePages.GetControl<$type>(nameof($name));")
}
$aliases = $aliases | Sort-Object -Unique

$hostCode = @"
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace WinTools;

/// <summary>承载全部功能页的独立视图；主窗口只负责应用壳层与导航。</summary>
public sealed partial class FeaturePagesHost : UserControl
{
    internal MainWindow? Owner { get; set; }

    public FeaturePagesHost() => InitializeComponent();

    internal T GetControl<T>(string name) where T : DependencyObject =>
        FindName(name) as T ?? throw new InvalidOperationException($"找不到页面控件：{name}");

$($relayMethods -join "`r`n")
}
"@
[IO.File]::WriteAllText((Join-Path $ProjectDir 'FeaturePagesHost.xaml.cs'), $hostCode, $utf8)

$aliasCode = @"
using Microsoft.UI.Xaml.Controls;

namespace WinTools;

public sealed partial class MainWindow
{
$($aliases -join "`r`n")
}
"@
[IO.File]::WriteAllText((Join-Path $ProjectDir 'MainWindow.PageControls.cs'), $aliasCode, $utf8)

$code = $code.Replace('        InitializeComponent();', "        InitializeComponent();`r`n        FeaturePages.Owner = this;")
[IO.File]::WriteAllText($codePath, $code, $utf8)

Write-Output "已拆出 $($eventNames.Count) 个页面事件和 $($aliases.Count) 个控件引用。"
