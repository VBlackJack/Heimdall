@echo off
:: Heimdall - Run all tests

cd /d "%~dp0"
dotnet test Heimdall.slnx --verbosity normal
set RC=%ERRORLEVEL%
if %RC% NEQ 0 (
    echo.
    echo Tests FAILED.
) else (
    echo.
    echo All tests passed.
)
pause
exit /b %RC%
