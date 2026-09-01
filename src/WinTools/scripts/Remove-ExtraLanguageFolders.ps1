# 發佈後僅保留 zh-CN 語言資料夾，其餘刪除
param([string]$PublishDir)
$dir = $PublishDir.Trim().TrimEnd('\').Trim('"', "'")
if (-not $dir) { exit 0 }
if (-not [IO.Path]::IsPathRooted($dir)) { $dir = [IO.Path]::GetFullPath((Join-Path (Get-Location) $dir)) }
if (-not (Test-Path -LiteralPath $dir)) { exit 0 }

$keep = 'zh-CN'
Get-ChildItem -Path $dir -Directory | Where-Object {
  $_.Name -match '^[a-zA-Z]{2,3}(-[a-zA-Z0-9-]+)?$' -and $_.Name -ne $keep
} | Remove-Item -Recurse -Force
