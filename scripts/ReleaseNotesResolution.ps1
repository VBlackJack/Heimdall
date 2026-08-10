<#
.SYNOPSIS
    Resolves the hand-written release-notes path and returns the publish decision.

.DESCRIPTION
    Dot-source this file to get Resolve-ReleaseNotes. The function preserves the
    release-notes precedence used by Build.ps1: an explicit -ReleaseNotesFile wins,
    otherwise the tracked docs/release-notes/v<version>.md convention is used.

    The function is side-effect free except for testing path existence. It does not
    write, log, exit, or call external tools. Build.ps1 owns those effects.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>

#Requires -Version 5.1

[Diagnostics.CodeAnalysis.SuppressMessageAttribute(
    'PSUseSingularNouns',
    '',
    Justification = 'Release notes is the established domain term used throughout Build.ps1.'
)]
param()

function Resolve-ReleaseNotes {
    <#
    .SYNOPSIS
        Returns the release-notes path decision for one build number.
    .OUTPUTS
        A record with Decision (UseNotes, Abort, or AutoOnly), Path (the usable
        notes path or null), CandidatePath, and Source (Explicit or Convention).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$ProjectRoot,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$BuildNumber,

        [Parameter()]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ReleaseNotesFile,

        [Parameter()]
        [switch]$AllowAutoNotesOnly
    )

    # Function-scoped strict mode protects the decision without leaking into the
    # dot-sourcing caller (Build.ps1 relies on its own non-strict runtime).
    Set-StrictMode -Version Latest

    if (-not [string]::IsNullOrWhiteSpace($ReleaseNotesFile)) {
        if (Test-Path -LiteralPath $ReleaseNotesFile) {
            return [PSCustomObject]@{
                Decision      = 'UseNotes'
                Path          = $ReleaseNotesFile
                CandidatePath = $ReleaseNotesFile
                Source        = 'Explicit'
            }
        }

        return [PSCustomObject]@{
            Decision      = 'Abort'
            Path          = $null
            CandidatePath = $ReleaseNotesFile
            Source        = 'Explicit'
        }
    }

    $conventionPath = Join-Path $ProjectRoot "docs\release-notes\v${BuildNumber}.md"
    if (Test-Path -LiteralPath $conventionPath) {
        return [PSCustomObject]@{
            Decision      = 'UseNotes'
            Path          = $conventionPath
            CandidatePath = $conventionPath
            Source        = 'Convention'
        }
    }

    if ($AllowAutoNotesOnly) {
        return [PSCustomObject]@{
            Decision      = 'AutoOnly'
            Path          = $null
            CandidatePath = $conventionPath
            Source        = 'Convention'
        }
    }

    return [PSCustomObject]@{
        Decision      = 'Abort'
        Path          = $null
        CandidatePath = $conventionPath
        Source        = 'Convention'
    }
}
