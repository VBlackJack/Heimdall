@echo off
REM Heimdall - One-click release
REM Builds, tests, packages, commits version bump, pushes, and publishes to GitHub.
REM Usage: double-click or run from terminal.

cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File Build.ps1 -Mode Release -Publish
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Release FAILED.
    pause
    exit /b 1
)
echo.
echo Release complete.
pause
