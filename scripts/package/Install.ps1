param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\WinTools'),
    [switch]$NoLaunch,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$exeName = 'WinTools.exe'
$exe = Join-Path $InstallDir $exeName

if (-not (Test-Path -LiteralPath (Join-Path $source $exeName))) {
    throw "找不到 $source\$exeName，请先把 zip 完整解压后再运行安装。"
}

# 覆盖安装前先关掉安装目录里正在运行的旧版本，否则文件被占用。
Get-Process -Name 'WinTools' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "$InstallDir\*" } |
    ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit(5000) | Out-Null }

# robocopy /MIR 会删掉目标里多出来的文件：只允许装进空目录或已有的 WinTools 目录。
if ((Test-Path -LiteralPath $InstallDir) -and
    (Get-ChildItem -LiteralPath $InstallDir -Force | Select-Object -First 1) -and
    -not (Test-Path -LiteralPath $exe)) {
    throw "$InstallDir 已有其他文件且不是 WinTools 安装目录，请换一个空目录。"
}

Write-Host "安装到 $InstallDir ..."
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
robocopy $source $InstallDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制文件失败 (robocopy exit $LASTEXITCODE)" }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $InstallDir -Force

if (-not $NoShortcut) {
    $lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinTools.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Save()
    Write-Host '已创建开始菜单快捷方式：WinTools'
}

Write-Host '安装完成。用户配置保存在 %LocalAppData%\WinTools，首次运行为全新默认配置。'
if (-not $NoLaunch) { Start-Process -FilePath $exe -WorkingDirectory $InstallDir }

exit 0
