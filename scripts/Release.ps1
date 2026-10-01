param(
    # 每个版本都要改版本号：默认递增修订号（1.2.0 -> 1.2.1）。功能性更新用 minor，不改版本号用 none（仅重新打包）。
    [ValidateSet('patch', 'minor', 'major', 'none')]
    [string]$Bump = 'patch',
    [string]$WindowsSdkVersion = '10.0.26100.0'
)

# 一键发布：改版本号 -> 发布 -> 生成安装包 Setup.exe 和 .sha256。
# 产物在 artifacts\dist；上传到 GitHub Release 的步骤见脚本末尾的提示。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'src\WinTools\WinTools.csproj'
$text = [IO.File]::ReadAllText($csproj)

if ($text -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') { throw "csproj 里没有 <Version>x.y.z</Version>" }
$major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
switch ($Bump) {
    'patch' { $patch++ }
    'minor' { $minor++; $patch = 0 }
    'major' { $major++; $minor = 0; $patch = 0 }
}
$version = "$major.$minor.$patch"

if ($Bump -ne 'none') {
    $text = $text -replace '<Version>[^<]+</Version>', "<Version>$version</Version>"
    $text = $text -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$version.0</AssemblyVersion>"
    $text = $text -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$version.0</FileVersion>"
    $text = $text -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$version</InformationalVersion>"
    [IO.File]::WriteAllText($csproj, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host "版本号 -> $version"
}

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw '没有找到 Inno Setup 6（ISCC.exe）。安装：winget install JRSoftware.InnoSetup' }

& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Runtime win-x64 -WindowsSdkVersion $WindowsSdkVersion
if ($LASTEXITCODE -ne 0) { throw "发布失败 (exit $LASTEXITCODE)" }

& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\WinTools.iss')
if ($LASTEXITCODE -ne 0) { throw "生成安装包失败 (exit $LASTEXITCODE)" }

$setup = Join-Path $root "artifacts\dist\WinTools-Setup-$version.exe"
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# 应用内更新会校验这个文件：第一列是 SHA-256，第二列是文件名。
[IO.File]::WriteAllText("$setup.sha256", "$hash  WinTools-Setup-$version.exe`n")

Write-Host ''
Write-Host "安装包：$setup"
Write-Host "SHA-256：$hash"
Write-Host ''
Write-Host '发布到 GitHub（让软件能在线更新）：'
Write-Host "  git add -A; git commit -m 'Release v$version'; git tag v$version; git push origin main --tags"
Write-Host "  然后在 GitHub 上用 tag v$version 创建 Release，并上传 artifacts\dist 里的 WinTools-Setup-$version.exe 和 .sha256 两个文件。"
