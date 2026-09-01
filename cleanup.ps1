#requires -Version 5.1
<#
WinTools workspace cleanup.
Dry-run by default. Use -Execute to remove regenerable build and diagnostic output.
The canonical release under artifacts\release is preserved.
#>
[CmdletBinding()]
param([switch]$Execute)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$relativeTargets = @(
    'scripts\logs',
    'src\WinTools\bin',
    'src\WinTools\obj',
    'src\WinTools\AppPackages',
    'src\WinTools\artifacts',
    'src\WinToolsExplorerCommand\bin',
    'src\WinToolsExplorerCommand\obj'
)

$targets = foreach ($relativePath in $relativeTargets) {
    $fullPath = [IO.Path]::GetFullPath((Join-Path $workspaceRoot $relativePath))
    if (-not $fullPath.StartsWith($workspaceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe cleanup target: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        [pscustomobject]@{ Relative = $relativePath; Full = $fullPath }
    }
}

if (-not $targets) {
    Write-Host 'Workspace is already clean.' -ForegroundColor Green
    return
}

$totalBytes = 0L
foreach ($target in $targets) {
    $totalBytes += [long]((Get-ChildItem -LiteralPath $target.Full -Recurse -Force -File -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum)
}

$modeLabel = if ($Execute) { 'Cleanup plan (execute):' } else { 'Cleanup plan (dry run):' }
Write-Host $modeLabel -ForegroundColor Cyan
foreach ($target in $targets) { Write-Host "  $($target.Relative)" }
Write-Host ("Total: {0:N2} MB" -f ($totalBytes / 1MB)) -ForegroundColor Yellow

if (-not $Execute) {
    Write-Host 'Run .\cleanup.ps1 -Execute to remove these generated files.' -ForegroundColor Cyan
    return
}

foreach ($target in $targets) {
    Remove-Item -LiteralPath $target.Full -Recurse -Force
}
Write-Host 'Cleanup completed.' -ForegroundColor Green
