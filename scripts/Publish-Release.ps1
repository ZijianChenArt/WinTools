param(
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\WinTools\WinTools.csproj'
$explorerProject = Join-Path $projectRoot 'src\WinToolsExplorerCommand\WinToolsExplorerCommand.vcxproj'
$publishRoot = Join-Path $projectRoot 'artifacts\release'
$publishDir = Join-Path $publishRoot $Runtime
$stagingDir = Join-Path $publishRoot ".$Runtime-staging"

# Staging can always be rebuilt. User configuration lives in
# %LocalAppData%\WinTools and is intentionally never copied into release output.
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

$platform = switch ($Runtime) {
    'win-x64' { 'x64' }
    'win-x86' { 'x86' }
    'win-arm64' { 'ARM64' }
}

# Build the native Explorer command explicitly. A clean workspace has no cached
# DLL for the managed project to copy into its publish directory.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer (vswhere.exe) was not found.'
}
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild was not found.'
}

& $msbuild $explorerProject /nologo /verbosity:minimal /t:Build /p:Configuration=Release /p:Platform=$platform
if ($LASTEXITCODE -ne 0) {
    throw "Explorer command build failed with exit code $LASTEXITCODE"
}

dotnet publish $project `
    -c Release `
    -r $Runtime `
    -p:SelfContained=false `
    -p:WindowsAppSDKSelfContained=false `
    -p:Platform=$platform `
    -p:WindowsPackageType=None `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $stagingDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# Release output does not include debug symbols.
$pdb = Join-Path $stagingDir 'WinTools.pdb'
if (Test-Path -LiteralPath $pdb) {
    Remove-Item -LiteralPath $pdb -Force
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
Move-Item -LiteralPath $stagingDir -Destination $publishDir

$files = Get-ChildItem -LiteralPath $publishDir -Recurse -File
$size = ($files | Measure-Object Length -Sum).Sum
Write-Host "Published $($files.Count) files ($([math]::Round($size / 1MB, 2)) MB)"
Write-Host "Portable directory: $publishDir"
