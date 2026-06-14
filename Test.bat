@echo off
:: Heimdall — Run all tests

cd /d "%~dp0"
dotnet test Heimdall.slnx --verbosity normal
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Tests FAILED.
) else (
    echo.
    echo All tests passed.
)
pause
