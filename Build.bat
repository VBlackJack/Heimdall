@echo off
:: Heimdall - Debug build (no installer, no release)

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File Build.ps1
set RC=%ERRORLEVEL%
if %RC% NEQ 0 (
    echo.
    echo Build FAILED.
)
pause
exit /b %RC%
