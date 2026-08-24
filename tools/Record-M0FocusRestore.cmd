@echo off
setlocal
title Vok M0 Focus Restore Recorder
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "D:\dev\vok\tools\Record-M0FocusRestore.ps1"
if errorlevel 1 (
  echo.
  echo Recorder failed. Review the error above.
)
echo.
pause
