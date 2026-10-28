<#
.SYNOPSIS
    Reads the informational-lane result of the CI run that vouches for the release commit,
    and turns it into console warnings and a release-notes section.

.DESCRIPTION
    Dot-source this file to get Get-InformationalLaneReport, Get-InformationalLaneMessages
    and Get-InformationalLaneNotes.

    The CIUnstable and RequiresDesktop lanes of ci.yml run with `continue-on-error: true`,
    so a failed test there leaves the run, the job and the commit check green. The release
    preflight therefore reads "CI green for HEAD" without ever having been told that a test
    failed in those lanes (docs/CI_FLAKY_TESTS.md, "Reading an informational lane's real
    result"). They stay non-gating on purpose: a release is not refused for them. This
    module makes them visible instead, in the console and in the generated release notes.

    Source of the result: the `test-results` artifact of the push run of ci.yml for HEAD,
    which holds the TRX file of every lane, read with the same functions the CI step
    "Report informational lane failures" uses (scripts/InformationalTestFailures.ps1). The
    alternatives were rejected for what they cost in reliability:

      * the step's log output: it has to be fetched with `gh run view --log`, whose step
        column is sometimes rendered as UNKNOWN STEP for every line of a run, so a lane
        cannot be bounded by step name, and a log of ten thousand lines is parsed with a
        regular expression for a marker that the step itself derived from the TRX;
      * the step's conclusion or the run's conclusion: `continue-on-error` makes both
        report success whatever the lane did.

    The artifact is the structured data the log marker was printed from, it is read by a
    tested parser that selects UnitTestResult elements through the TRX namespace, and it
    lets an unread lane be told from a clean one: a lane with zero results is reported as
    not read, never as clean. Artifacts are kept 30 days (ci.yml), far longer than the
    minutes between the CI run of a commit and its release.

    Whatever goes wrong (no run, no artifact, an expired artifact, a lane directory without
    results, a gh failure) becomes the state "informational lanes not read", reported as a
    warning. It never throws and never refuses a release.

    gh is reached through an injectable script block that receives an argument array and
    returns an object with ExitCode and Lines, as in scripts/ReleasePreflight.ps1.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

$script:InformationalLaneWorkflow = 'ci.yml'
$script:InformationalLaneEvent = 'push'
$script:InformationalLaneArtifactName = 'test-results'
# The artifact keeps the TestResults/<lane>/ layout of ci.yml; the root is looked for
# beside the download directory and one level down, so a different upload root still reads.
$script:InformationalLaneResultsSubdirectory = 'TestResults'
# How many failed tests the release notes name before they summarise the rest.
$script:InformationalLaneListedFailures = 50
# Heading shared by the notes section in every state.
$script:InformationalLaneNotesHeading = '### Informational test lanes'

function Get-InformationalLaneReport {
    <#
    .SYNOPSIS
        The informational-lane result of the CI push run of one commit.
    .OUTPUTS
        An object with State (Read or NotRead), Reason (why it was not read, or $null),
        RunUrl, Failures (Lane, TestName, Duration), ResultCount, LaneNames and
        UnreadLanes (lanes that held no result, so are unknown rather than clean).
    #>
    [CmdletBinding()]
    param(
        [scriptblock]$Gh = { param([string[]]$GhArgs) Invoke-NativeCapture -FilePath 'gh' -ArgumentList $GhArgs },
        [Parameter(Mandatory = $true)] [string]$Sha,
        [string]$Workflow = $script:InformationalLaneWorkflow,
        [string]$EventName = $script:InformationalLaneEvent,
        [string]$ArtifactName = $script:InformationalLaneArtifactName,
        # Where the artifact is downloaded; a unique temporary directory when omitted.
        [string]$DownloadRoot
    )

    # Not named $laneNames or $resultsRoot: PowerShell variables are case-insensitive, and
    # dot-sourcing InformationalTestFailures.ps1 below creates its two parameters, $LaneNames
    # and $ResultsRoot, as null variables in this scope.
    $informationalLanes = @('CIUnstable', 'RequiresDesktop')
    $notRead = {
        param($Reason, $RunUrl)
        return [pscustomobject]@{
            State       = 'NotRead'
            Reason      = $Reason
            RunUrl      = $RunUrl
            Failures    = @()
            ResultCount = 0
            LaneNames   = $informationalLanes
            UnreadLanes = $informationalLanes
        }
    }
    $describe = {
        param($Result)
        $text = (@($Result.Lines) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' / '
        if ([string]::IsNullOrWhiteSpace($text)) { "exit code $($Result.ExitCode)" } else { "exit code $($Result.ExitCode): $text" }
    }

    $runs = & $Gh @('run', 'list', '--workflow', $Workflow, '--event', $EventName, '--commit', $Sha,
        '--limit', '1', '--json', 'databaseId,headSha,url')
    if ($runs.ExitCode -ne 0) {
        return (& $notRead "gh run list failed ($(& $describe $runs))" $null)
    }
    $text = ((@($runs.Lines)) -join "`n").Trim()
    $parsed = $null
    $isJson = -not [string]::IsNullOrWhiteSpace($text)
    if ($isJson) {
        try { $parsed = ConvertFrom-Json -InputObject $text } catch { $isJson = $false }
    }
    if (-not $isJson) {
        return (& $notRead "gh run list returned output that is not JSON ($(& $describe $runs))" $null)
    }
    $matching = @($parsed | Where-Object { $null -ne $_ -and [string]$_.headSha -eq $Sha })
    if ($matching.Count -eq 0) {
        return (& $notRead "no $Workflow $EventName run found for $Sha" $null)
    }
    $runId = [string]$matching[0].databaseId
    $runUrl = [string]$matching[0].url

    $ownsDownload = [string]::IsNullOrWhiteSpace($DownloadRoot)
    if ($ownsDownload) {
        $DownloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('heimdall-informational-lanes-' + [Guid]::NewGuid().ToString('N'))
    }
    try {
        $null = New-Item -ItemType Directory -Path $DownloadRoot -Force
        $download = & $Gh @('run', 'download', $runId, '--name', $ArtifactName, '--dir', $DownloadRoot)
        if ($download.ExitCode -ne 0) {
            return (& $notRead "the $ArtifactName artifact of run $runId could not be downloaded ($(& $describe $download))" $runUrl)
        }

        # Dot-sourced here, not at the top of this file: the reader sets strict mode, and
        # a script-level dot-source would switch it on for the whole of Build.ps1.
        . (Join-Path $PSScriptRoot 'InformationalTestFailures.ps1')

        $laneResultsRoot = $DownloadRoot
        $nested = Join-Path $DownloadRoot $script:InformationalLaneResultsSubdirectory
        $hasLaneAtRoot = @($informationalLanes | Where-Object { Test-Path -LiteralPath (Join-Path $DownloadRoot $_) }).Count -gt 0
        if (-not $hasLaneAtRoot -and (Test-Path -LiteralPath $nested)) { $laneResultsRoot = $nested }

        $coverage = @(Get-InformationalLaneCoverage -ResultsRoot $laneResultsRoot -LaneNames $informationalLanes)
        $unread = @($coverage | Where-Object { $_.ResultCount -eq 0 } | ForEach-Object { $_.Lane })
        $resultCount = 0
        foreach ($lane in $coverage) { $resultCount += $lane.ResultCount }
        if ($resultCount -eq 0) {
            return (& $notRead "run $runId holds no test result for the informational lanes" $runUrl)
        }

        $failed = @(Get-InformationalTestFailures -ResultsRoot $laneResultsRoot -LaneNames $informationalLanes)
        $failures = @($failed | ForEach-Object {
            [pscustomobject]@{ Lane = [string]$_.Lane; TestName = [string]$_.TestName; Duration = [string]$_.Duration }
        })
        return [pscustomobject]@{
            State       = 'Read'
            Reason      = $null
            RunUrl      = $runUrl
            Failures    = $failures
            ResultCount = $resultCount
            LaneNames   = $informationalLanes
            UnreadLanes = $unread
        }
    } catch {
        return (& $notRead "the informational lane results could not be read ($($_.Exception.Message))" $runUrl)
    } finally {
        if ($ownsDownload) {
            Remove-Item -LiteralPath $DownloadRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function ConvertTo-InformationalLaneText {
    <#
    .SYNOPSIS
        A test name or reason made safe for the release notes: printable ASCII, one line.
    .DESCRIPTION
        The notes are published under the typography guard of scripts/NotesTypographyGuard.ps1,
        and a test name is data from the test run, so anything outside printable ASCII
        becomes a question mark rather than a refusal at publish time.
    #>
    param([AllowEmptyString()] [string]$Text)

    $flat = ($Text -replace '\s+', ' ').Trim()
    return ($flat -replace '[^\x20-\x7E]', '?')
}

function Get-InformationalLaneMessages {
    <#
    .SYNOPSIS
        The warnings to print for a report. Empty when the lanes were read and are clean.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Report
    )

    $messages = New-Object System.Collections.Generic.List[string]
    if ($Report.State -ne 'Read') {
        $messages.Add("informational lanes not read: $(ConvertTo-InformationalLaneText $Report.Reason). Their result for this commit is unknown.")
        return $messages.ToArray()
    }
    if (@($Report.UnreadLanes).Count -gt 0) {
        $messages.Add("informational lanes not read: no result for $(@($Report.UnreadLanes) -join ', '). Their result for this commit is unknown.")
    }
    foreach ($failure in @($Report.Failures)) {
        $messages.Add("informational lane $($failure.Lane) failed: $(ConvertTo-InformationalLaneText $failure.TestName) ($($failure.Duration))")
    }
    return $messages.ToArray()
}

function Get-InformationalLaneNotes {
    <#
    .SYNOPSIS
        The release-notes section for a report; an empty string when the lanes were read
        and nothing failed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Report
    )

    $failures = @($Report.Failures)
    $unread = @($Report.UnreadLanes)
    if ($Report.State -eq 'Read' -and $failures.Count -eq 0 -and $unread.Count -eq 0) {
        return ''
    }

    $section = "$script:InformationalLaneNotesHeading`n`n"
    if ($Report.State -ne 'Read') {
        $section += "The informational test lanes (CIUnstable, RequiresDesktop) were not read for this release: $(ConvertTo-InformationalLaneText $Report.Reason). Their result for this commit is unknown. These lanes never gate a release.`n`n"
        return $section
    }

    if ($unread.Count -gt 0) {
        $section += "No result could be read for the lane(s) $($unread -join ', '), so their state for this commit is unknown.`n`n"
    }
    if ($failures.Count -gt 0) {
        $where = if ([string]::IsNullOrWhiteSpace($Report.RunUrl)) { 'the CI run of this commit' } else { "the CI run of this commit ($($Report.RunUrl))" }
        $section += "$($failures.Count) test(s) failed in the informational lanes of $where. These lanes never gate a release, so this release was published with these failures:`n`n"
        foreach ($failure in @($failures | Select-Object -First $script:InformationalLaneListedFailures)) {
            $section += "- $($failure.Lane): $(ConvertTo-InformationalLaneText $failure.TestName)`n"
        }
        if ($failures.Count -gt $script:InformationalLaneListedFailures) {
            $section += "- and $($failures.Count - $script:InformationalLaneListedFailures) more`n"
        }
        $section += "`n"
    }
    return $section
}
