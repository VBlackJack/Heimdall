<#
.SYNOPSIS
    Runs the Inno Setup compiler on the installer script and keeps what it printed.

.DESCRIPTION
    Dot-source this file to get Invoke-InnoSetupCompile.

    Build.ps1 used to run ISCC as `& $iscc ... 2>&1 | Out-Null`. A compile that failed
    printed its reason, the line of the script and the message, into the void; the build
    said only "Inno Setup failed" and the next move was to run ISCC by hand. The output
    is now captured and returned with the exit code, so the caller prints it and decides.

    The capture runs with $ErrorActionPreference = 'Continue' for the same reason as
    Invoke-NativeLogged in Build.ps1: under 'Stop', Windows PowerShell turns the first
    redirected stderr line of a native command into a terminating error, which would end
    the build before the compiler's message was read.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

#Requires -Version 5.1

param()

function Invoke-InnoSetupCompile {
    <#
    .SYNOPSIS
        Compiles installer\innosetup.iss for one edition.
    .OUTPUTS
        An object with ExitCode and Lines (every line ISCC wrote, stdout and stderr).
    #>
    param(
        [Parameter(Mandatory)] [string] $IsccPath,
        [Parameter(Mandatory)] [string] $ScriptPath,
        [Parameter(Mandatory)] [string] $AppVersion,
        [Parameter(Mandatory)] [string] $Variant,
        [Parameter(Mandatory)] [string] $SourceDir
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $IsccPath "/DAppVersion=$AppVersion" "/DVariant=$Variant" "/DSourceDir=$SourceDir" /Q $ScriptPath 2>&1 |
            ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Lines = $lines }
}
