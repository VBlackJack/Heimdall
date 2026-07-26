<#
.SYNOPSIS
    Version stamping for Build.ps1: applies the computed build number to the
    application project, and decides whether that tracked file may be written.

.DESCRIPTION
    Dot-source this file to get Get-StampedAppProjectText, Update-AppProjectVersion
    and Get-VersionOverrideArgs.

    Build.ps1 stamps two values into src/Heimdall.App/Heimdall.App.csproj:
    <Version>, the Win32-compatible 1.0.MMDD.xx assembly version, and
    <InformationalVersion>, the YYYY.MMDDxx build number the UI displays.

    On the real publish path that write is the point: it is what the release
    commit carries. On the dry-run path it was a side effect, and a compounding
    one. A mode documented as making no git changes left a tracked file modified,
    and the auto-increment in Build.ps1 reads <InformationalVersion> back out of
    that same file, so each dry run incremented the number the next one started
    from: two consecutive dry runs yielded two different build numbers.

    A dry run therefore skips the write and receives the same two values as
    MSBuild global properties instead (-p:Version, -p:InformationalVersion).
    Global properties outrank the project file, so the versions still reach the
    compiled assembly attributes and the dry run validates what the real run
    would ship, with no window during which the working tree is dirty.

    Measured 2026-07-26, with the project declaring 1.0.0725.1 / 2026.072501:
    building with -p:Version=1.0.9999.42 -p:InformationalVersion=9999.999942
    produced AssemblyFileVersion 1.0.9999.42 and AssemblyInformationalVersion
    9999.999942, neither of them the project file's value. That measurement is
    what chose this approach over writing the file and restoring it afterwards.

    Known and accepted consequence, also measured: an MSBuild global property
    flows into ProjectReference builds, so a dry run stamps the satellite
    assemblies (Heimdall.Core, TwinShell.Core, ...) with the app version, where
    a real run leaves them at their own default. Nothing reads those values -
    the installers take the version from Build.ps1 variables and the app reads
    its own AssemblyInformationalVersion - so the overrides are deliberately
    passed on the dry-run path only, leaving the real publish output unchanged.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

function Get-StampedAppProjectText {
    <#
    .SYNOPSIS
        Returns the project file text with <Version> and <InformationalVersion>
        replaced. Pure transform: it reads nothing and writes nothing.
    .DESCRIPTION
        Kept separate from the write so the substitution can be tested without
        touching a file, and so there is exactly one place where the shape of
        the two elements is encoded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [string]$AssemblyVersion,

        [Parameter(Mandatory = $true)]
        [string]$InformationalVersion
    )

    $stamped = $Text -replace '<Version>[^<]+</Version>', "<Version>${AssemblyVersion}</Version>"
    if ($stamped -match '<InformationalVersion>') {
        $stamped = $stamped -replace '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>${InformationalVersion}</InformationalVersion>"
    } else {
        $stamped = $stamped -replace '</Version>', "</Version>`n    <InformationalVersion>${InformationalVersion}</InformationalVersion>"
    }
    return $stamped
}

function Update-AppProjectVersion {
    <#
    .SYNOPSIS
        Stamps the version into the application project file, unless -DryRun.
    .OUTPUTS
        $true when the file was written, $false when the write was skipped
        because -DryRun was passed. The caller uses that to phrase its progress
        line, and the test suite uses it to prove the skip is real.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$AssemblyVersion,

        [Parameter(Mandatory = $true)]
        [string]$InformationalVersion,

        [switch]$DryRun
    )

    # The single guard this whole module exists to enforce. A dry run leaves the
    # tracked project file exactly as it found it; Get-VersionOverrideArgs below
    # supplies the same versions to MSBuild instead.
    if ($DryRun) {
        return $false
    }

    $text = Get-Content -LiteralPath $Path -Raw
    $stamped = Get-StampedAppProjectText -Text $text `
        -AssemblyVersion $AssemblyVersion `
        -InformationalVersion $InformationalVersion
    [System.IO.File]::WriteAllText($Path, $stamped, [System.Text.UTF8Encoding]::new($false))
    return $true
}

function Get-VersionOverrideArgs {
    <#
    .SYNOPSIS
        Returns the MSBuild arguments that carry the version when the project
        file was not written, and an empty array when it was.
    .DESCRIPTION
        The pairing with Update-AppProjectVersion is the invariant: exactly one
        of the two mechanisms supplies the version on any given run. Splat the
        result into dotnet build and dotnet publish.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AssemblyVersion,

        [Parameter(Mandatory = $true)]
        [string]$InformationalVersion,

        [switch]$DryRun
    )

    if (-not $DryRun) {
        return @()
    }
    return @("-p:Version=$AssemblyVersion", "-p:InformationalVersion=$InformationalVersion")
}
