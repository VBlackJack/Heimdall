@echo off
:: Heimdall - Quick launch (Debug)
::
:: Just run this. It fetches, auto-pulls when safe, builds and launches.
:: You don't have to remember flags - the defaults handle the common case.
::
:: Auto-pull triggers only when:
::   - You're on master or main
::   - Local is behind origin
::   - No uncommitted changes
::   - No local commits ahead of origin
::
:: Advanced flags (rarely needed):
::   Run.bat no-fetch     skip the network fetch (offline / fast iteration)
::   Run.bat no-pull      fetch but never auto-pull
::   Run.bat pull         force pull (works on any branch)
::   Run.bat clean        wipe bin/ and obj/ before build
::   Run.bat -- ...       forward args after -- to the .NET app

setlocal enabledelayedexpansion
cd /d "%~dp0"
title Heimdall - Debug

:: Detect double-click launch (Explorer wraps the call in cmd.exe /c "...").
:: When set, all exit points hit a final pause so the user can read output.
set DOUBLE_CLICK=
echo %CMDCMDLINE% | find /i "/c" >nul && set DOUBLE_CLICK=1

:: ----- argument parsing ----------------------------------------------------
set CLEAN=
set FORCE_PULL=
set NO_PULL=
set NO_FETCH=
set PASSTHROUGH=
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="clean"    (set CLEAN=1      & shift & goto :parse_args)
if /i "%~1"=="pull"     (set FORCE_PULL=1 & shift & goto :parse_args)
if /i "%~1"=="no-pull"  (set NO_PULL=1    & shift & goto :parse_args)
if /i "%~1"=="no-fetch" (set NO_FETCH=1   & shift & goto :parse_args)
if "%~1"=="--" (shift & goto :passthrough_loop)
echo Unknown flag: %~1
echo Usage: Run.bat [pull^|no-pull] [no-fetch] [clean] [-- args-for-app]
set EXIT=2
goto :end
:passthrough_loop
if "%~1"=="" goto :args_done
set PASSTHROUGH=!PASSTHROUGH! "%~1"
shift
goto :passthrough_loop
:args_done

:: ----- read git state (cheap, all-local) ----------------------------------
for /f "delims=" %%a in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set BRANCH=%%a
if "!BRANCH!"=="" (
    echo Not inside a git repository - launching as-is.
    goto :run
)

:: ----- quiet fetch on tracked branch so behind/ahead is accurate ----------
:: Failure (offline, no remote) is silent - we still launch with local state.
if not defined NO_FETCH (
    git fetch --quiet --no-tags origin !BRANCH! 2>nul
)

for /f "delims=" %%a in ('git rev-parse --short HEAD 2^>nul') do set SHA=%%a
for /f "delims=" %%a in ('git log -1 --format^=%%s 2^>nul')   do set SUBJECT=%%a

set BEHIND=0
set AHEAD=0
set DIRTY=0
for /f %%a in ('git rev-list --count HEAD..origin/!BRANCH! 2^>nul') do set BEHIND=%%a
for /f %%a in ('git rev-list --count origin/!BRANCH!..HEAD 2^>nul') do set AHEAD=%%a
for /f %%a in ('git status --porcelain 2^>nul ^| find /c /v ""')    do set DIRTY=%%a

:: ----- decide whether to auto-pull ----------------------------------------
:: Explicit "pull" flag always wins. Auto-pull only on master/main when the
:: tree is clean and we have no local commits ahead - anything else needs
:: human attention.
set DO_PULL=
if defined FORCE_PULL set DO_PULL=1
if not defined NO_PULL if not defined FORCE_PULL (
    if "!BEHIND!" NEQ "0" if "!AHEAD!"=="0" if "!DIRTY!"=="0" (
        if /i "!BRANCH!"=="master" set DO_PULL=1
        if /i "!BRANCH!"=="main"   set DO_PULL=1
    )
)

if defined DO_PULL (
    echo Auto-pulling !BEHIND! commit^(s^) from origin/!BRANCH!...
    git pull --ff-only --quiet
    echo.
    REM Refresh banner state after the pull
    for /f "delims=" %%a in ('git rev-parse --short HEAD 2^>nul') do set SHA=%%a
    for /f "delims=" %%a in ('git log -1 --format^=%%s 2^>nul')   do set SUBJECT=%%a
    set BEHIND=0
)

:: ----- banner --------------------------------------------------------------
echo ----------------------------------------------------------------
echo  Heimdall - Quick launch (Debug)
echo  Branch: !BRANCH! @ !SHA!
if defined SUBJECT echo  Last:   !SUBJECT!
if not "!BEHIND!"=="0" (
    echo  WARN:   !BEHIND! commit^(s^) BEHIND origin/!BRANCH! - run 'Run.bat pull' to update
)
if not "!AHEAD!"=="0" (
    echo  Info:   !AHEAD! commit^(s^) ahead of origin/!BRANCH!
)
if not "!DIRTY!"=="0" (
    echo  Dirty:  !DIRTY! file^(s^) with uncommitted changes
)
echo ----------------------------------------------------------------
echo.

:: ----- clean ---------------------------------------------------------------
if defined CLEAN (
    echo Cleaning bin\ and obj\ trees...
    for /d /r %%d in (bin obj) do if exist "%%d" rmdir /s /q "%%d"
    echo Done.
    echo.
)

:: ----- run -----------------------------------------------------------------
:run
dotnet run --nologo --project src\Heimdall.App\Heimdall.App.csproj !PASSTHROUGH!
set EXIT=%ERRORLEVEL%
if %EXIT% NEQ 0 (
    echo.
    echo Run failed with exit code %EXIT%.
)

:end
:: From a terminal, exit silently on success and only pause on failure
:: (preserves scriptable use). From a double-click, always pause so the
:: banner, dotnet output and any error are readable before the window
:: disappears.
if defined DOUBLE_CLICK (
    echo.
    pause
) else if not "%EXIT%"=="0" (
    pause
)
exit /b %EXIT%
