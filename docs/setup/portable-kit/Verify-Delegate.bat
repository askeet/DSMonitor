
@echo off
setlocal

set "SCRIPT=%~dp0Verify-Delegate.ps1"
if not exist "%SCRIPT%" (
    >&2 echo ERROR: Verify-Delegate.ps1 not found next to this BAT file.
    set "EXIT_CODE=1"
    goto :finish
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "EXIT_CODE=%ERRORLEVEL%"

:finish
echo.
pause
exit /b %EXIT_CODE%
