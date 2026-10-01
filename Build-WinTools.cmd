@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在关闭运行中的 WinTools...
taskkill /im WinTools.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
echo 正在编译并发布，大约需要一到几分钟...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Transcript -Path artifacts/build-log.txt -Force | Out-Null; $code = 0; try { & ./scripts/Publish-Release.ps1 -Runtime win-x64 -WindowsSdkVersion 10.0.26100.0 | Out-Host } catch { Write-Host $_ -ForegroundColor Red; $code = 1 }; Stop-Transcript | Out-Null; exit $code"
if errorlevel 1 (
  echo.
  echo 编译失败，日志在 artifacts\build-log.txt
  pause
  exit /b 1
)
echo 编译成功，正在启动新版 WinTools...
start "" "artifacts\release\win-x64\WinTools.exe"
timeout /t 5
