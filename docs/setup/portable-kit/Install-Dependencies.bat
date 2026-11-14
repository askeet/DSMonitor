@echo off
setlocal

set "OPENCODE_VERSION=2.2.6"
set "FORGE_VERSION=1.2.4"

set "SCRIPT=%~dp0Install-Dependencies.ps1"

if not exist "%SCRIPT%" (
    echo ERROR: Install-Dependencies.ps1 not found.
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass ^
    -File "%SCRIPT%" ^
    -OpenCodeVersion "%OPENCODE_VERSION%" ^
    -ForgeVersion "%FORGE_VERSION%"

set "EXIT_CODE=%ERRORLEVEL%"
echo.
pause
exit /b %EXIT_CODE%

