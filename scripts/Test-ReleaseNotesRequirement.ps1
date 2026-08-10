<#
.SYNOPSIS
    Tests the fail-closed release-notes requirement.

.DESCRIPTION
    This suite proves both directions of scripts/ReleaseNotesResolution.ps1:

      1. Convention notes present, no switch       -> use the notes.
      2. Convention notes absent, no switch        -> abort.
      3. Convention notes absent, switch passed    -> proceed with auto-only notes.
      4. Explicit notes present                    -> override the convention path.
      5. Explicit notes absent, switch or not      -> abort.
      6. Build.ps1 delegates convention resolution -> an inline warning-only branch
                                                      cannot return unnoticed.

    Every behavioural case runs in a temporary sandbox. The suite writes no repository
    file and exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  powershell -NoProfile -File scripts\Test-ReleaseNotesRequirement.ps1
#>

#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleaseNotesResolution.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $repoRoot 'Build.ps1'
$failures = 0

function Assert-True {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Message
    )

    if ($Condition) {
        Write-Output "PASS: $Message"
    } else {
        Write-Output "FAIL: $Message"
        $script:failures++
    }
}

function New-NotesSandbox {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param()

    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-releasenotesguard-" + [System.Guid]::NewGuid().ToString('N'))
    $notesDirectory = Join-Path (Join-Path $sandbox 'docs') 'release-notes'
    if ($PSCmdlet.ShouldProcess($notesDirectory, 'Create temporary release-notes sandbox')) {
        $null = New-Item -ItemType Directory -Path $notesDirectory -Force
    }
    return $sandbox
}

function Remove-NotesSandbox {
    [CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    if ($PSCmdlet.ShouldProcess($Path, 'Remove temporary release-notes sandbox')) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$buildNumber = '9999.999901'
$sandbox = New-NotesSandbox
try {
    $conventionPath = Join-Path $sandbox "docs\release-notes\v${buildNumber}.md"
    [System.IO.File]::WriteAllText($conventionPath, 'Convention notes.', [System.Text.UTF8Encoding]::new($false))

    $result = Resolve-ReleaseNotes -ProjectRoot $sandbox -BuildNumber $buildNumber
    Assert-True ($result.Decision -eq 'UseNotes') 'notes present without the switch use the notes'
    Assert-True ($result.Path -eq $conventionPath) 'the convention path is returned when it exists'

    $explicitPath = Join-Path $sandbox 'explicit-notes.md'
    [System.IO.File]::WriteAllText($explicitPath, 'Explicit notes.', [System.Text.UTF8Encoding]::new($false))
    $result = Resolve-ReleaseNotes -ProjectRoot $sandbox -BuildNumber $buildNumber -ReleaseNotesFile $explicitPath
    Assert-True ($result.Decision -eq 'UseNotes') 'explicit notes that exist are accepted'
    Assert-True ($result.Path -eq $explicitPath) 'explicit notes win over the convention path'
} finally {
    Remove-NotesSandbox -Path $sandbox
}

$sandbox = New-NotesSandbox
try {
    $result = Resolve-ReleaseNotes -ProjectRoot $sandbox -BuildNumber $buildNumber
    Assert-True ($result.Decision -eq 'Abort') 'notes absent without the switch abort the publish path'
    Assert-True ($null -eq $result.Path) 'an abort exposes no usable notes path'

    $result = Resolve-ReleaseNotes -ProjectRoot $sandbox -BuildNumber $buildNumber -AllowAutoNotesOnly
    Assert-True ($result.Decision -eq 'AutoOnly') 'notes absent with the switch proceed with auto-only notes'
    Assert-True ($null -eq $result.Path) 'auto-only mode exposes no hand-written notes path'

    $missingExplicitPath = Join-Path $sandbox 'missing-explicit-notes.md'
    foreach ($allowAutoOnly in @($false, $true)) {
        $parameters = @{
            ProjectRoot        = $sandbox
            BuildNumber        = $buildNumber
            ReleaseNotesFile   = $missingExplicitPath
            AllowAutoNotesOnly = $allowAutoOnly
        }
        $result = Resolve-ReleaseNotes @parameters
        Assert-True ($result.Decision -eq 'Abort') "missing explicit notes abort when AllowAutoNotesOnly is $allowAutoOnly"
        Assert-True ($result.CandidatePath -eq $missingExplicitPath) "missing explicit notes report the exact path when AllowAutoNotesOnly is $allowAutoOnly"
    }
} finally {
    Remove-NotesSandbox -Path $sandbox
}

# Match line by line rather than with an anchored multiline regex over raw CRLF
# text. See Test-DryRunProjectFileGuard.ps1 case 9 for the regression this avoids.
if (Test-Path -LiteralPath $buildScript) {
    $buildLines = @(Get-Content -LiteralPath $buildScript)
    $helpEnd = [Array]::IndexOf($buildLines, '#>')
    $buildCodeLines = if ($helpEnd -ge 0) {
        @($buildLines | Select-Object -Skip ($helpEnd + 1))
    } else {
        $buildLines
    }
    $resolverCalls = @($buildCodeLines | Where-Object {
        $_ -match '^\s*\$releaseNotesResolution\s*=\s*Resolve-ReleaseNotes\b'
    })
    $executableConventionPaths = @($buildCodeLines | Where-Object {
        $trimmed = $_.TrimStart()
        -not $trimmed.StartsWith('#') -and
            $trimmed -match 'docs[\\/]+release-notes[\\/]'
    })

    foreach ($line in $executableConventionPaths) {
        Write-Output ("      Build.ps1 resolves the convention path inline: {0}" -f $line.Trim())
    }
    Assert-True ($resolverCalls.Count -eq 1) "Build.ps1 calls Resolve-ReleaseNotes exactly once ($($resolverCalls.Count) call(s))"
    Assert-True ($executableConventionPaths.Count -eq 0) "Build.ps1 contains no executable convention-path resolution ($($executableConventionPaths.Count) line(s))"
} else {
    Write-Output "FAIL: Build.ps1 not found at $buildScript"
    $failures++
}

Write-Output ''
if ($failures -gt 0) {
    Write-Output "$failures release-notes requirement test(s) FAILED."
    exit 1
}

Write-Output 'All release-notes requirement tests passed.'
exit 0
