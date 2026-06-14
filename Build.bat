@echo off
:: Heimdall — Debug build (no installer, no release)

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File Build.ps1
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Build FAILED.
)
pause
