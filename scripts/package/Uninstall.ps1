param(
    [string]$InstallDir = $PSScriptRoot,
    [switch]$RemoveUserData
)

$ErrorActionPreference = 'Stop'

# 后面会整目录删除：只认真正的安装目录，避免在源码包或传错路径时删掉别的东西。
if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'WinTools.exe'))) {
    throw "$InstallDir 不是 WinTools 安装目录（找不到 WinTools.exe），已停止卸载。"
}

Get-Process -Name 'WinTools' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path -like "$InstallDir\*" } |
    ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit(5000) | Out-Null }

# 开机自启动项（由软件设置页写入）与开始菜单快捷方式
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'WinTools' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinTools.lnk') -Force -ErrorAction SilentlyContinue

if ($RemoveUserData) {
    Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'WinTools') -Recurse -Force -ErrorAction SilentlyContinue
}

# 脚本自己就在安装目录里，交给一个独立的 cmd 在退出后删除目录。
$dir = (Resolve-Path -LiteralPath $InstallDir).Path
Start-Process -WindowStyle Hidden -FilePath 'cmd.exe' -ArgumentList "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q `"$dir`""
Write-Host '已卸载 WinTools。'
