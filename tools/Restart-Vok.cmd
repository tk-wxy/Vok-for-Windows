@echo off
setlocal
title Vok Start or Restart

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "D:\dev\vok\tools\Restart-Vok.ps1"
if errorlevel 1 (
    echo.
    echo Vok failed to start. Review the error above.
    pause
)

endlocal
