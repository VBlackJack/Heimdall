<#
.SYNOPSIS
    Tests the release-time reader of the informational CI lanes
    (scripts/InformationalLaneReport.ps1) on recorded gh replies and TRX fixtures, and its
    wiring into Build.ps1 and ci.yml.

.DESCRIPTION
    The CIUnstable and RequiresDesktop lanes cannot turn a CI run red, so a release
    preflight that reads "CI green" says nothing about them. The reader turns the lanes'
    result for the release commit into console warnings and a release-notes section, and
    never into a refusal. Each case is chosen because a plausible implementation fails it:

      1. No failure in either lane          -> State Read, no message, no notes. A
                                               ResultSummary outcome="Failed" over passing
                                               tests (the TRX trap) is not a failure.
      2. Two failures, one per lane         -> both named with their lane in the messages
                                               and in the notes; the notes pass the
                                               release-notes typography guard even though
                                               a test name carries a non-ASCII character.
      3. gh run list fails / not JSON /     -> NotRead, the warning "informational lanes
         no run for the SHA / the artifact     not read", a notes section saying so, and
         cannot be downloaded                  no throw: an unreadable lane never blocks.
      4. The artifact holds no TRX          -> NotRead, never "clean": zero over zero
                                               results is unknown.
      5. One lane without results           -> Read, the lane is listed as unread, the
                                               failures of the other are still reported.
      6. TRX nested under TestResults/      -> read: the upload root may differ.
      7. More failures than the notes list  -> the rest is summarised, not dropped.
      8. The downloaded files               -> removed afterwards.
      9. The query names HEAD's ci.yml      -> the run read is this commit's.
         push run and the test-results
         artifact
     10. Build.ps1 and ci.yml wiring        -> the report is read after the gate decision,
                                               never added to the gate failures, its notes
                                               reach the generated release notes, and this
                                               suite runs in CI.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-InformationalLaneReport.ps1
    Also run under Windows PowerShell 5.1, which is what Build.ps1 is invoked through.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NotesTypographyGuard.ps1')
. (Join-Path $PSScriptRoot 'InformationalLaneReport.ps1')

$failures = 0
$sha = '0123456789abcdef0123456789abcdef01234567'
$otherSha = 'fedcba9876543210fedcba9876543210fedcba98'
$runId = '4242424242'
$runUrl = 'https://github.com/VBlackJack/Heimdall/actions/runs/4242424242'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-Reply {
    param([int]$ExitCode = 0, [string[]]$Lines = @())
    return [pscustomobject]@{ ExitCode = $ExitCode; Lines = $Lines }
}

function Write-FixtureTrx {
    <#
    .SYNOPSIS
        One TRX holding the given results, under <Root>/<Lane>.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Lane,
        [Parameter(Mandatory)] [hashtable[]] $Results,
        [string] $SummaryOutcome = 'Completed',
        [string] $FileName = 'lane.trx'
    )

    $laneRoot = Join-Path $Root $Lane
    $null = New-Item -ItemType Directory -Path $laneRoot -Force

    $builder = New-Object System.Text.StringBuilder
    $null = $builder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    $null = $builder.AppendLine('<TestRun id="00000000-0000-0000-0000-000000000001" name="fixture" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">')
    $null = $builder.AppendLine('  <Results>')
    foreach ($result in $Results) {
        $name = [System.Security.SecurityElement]::Escape([string]$result.Name)
        $outcome = [System.Security.SecurityElement]::Escape([string]$result.Outcome)
        $null = $builder.AppendLine("    <UnitTestResult testName=`"$name`" outcome=`"$outcome`" duration=`"00:00:01.5000000`" />")
    }
    $null = $builder.AppendLine('  </Results>')
    $null = $builder.AppendLine("  <ResultSummary outcome=`"$SummaryOutcome`" />")
    $null = $builder.AppendLine('</TestRun>')
    [System.IO.File]::WriteAllText((Join-Path $laneRoot $FileName), $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
}

function New-RunListJson {
    param([string]$HeadSha = $sha)
    return ('[{{"databaseId":{0},"headSha":"{1}","url":"{2}"}}]' -f $runId, $HeadSha, $runUrl)
}

# A recorded gh: the list reply, and a download that lays the given fixture down in --dir.
function Invoke-WithWorld {
    param(
        [hashtable]$World
    )
    $gh = {
        param([string[]]$GhArgs)
        $World.Calls.Add(($GhArgs -join ' '))
        if ($GhArgs[0] -eq 'run' -and $GhArgs[1] -eq 'list') { return $World.List }
        if ($GhArgs[0] -eq 'run' -and $GhArgs[1] -eq 'download') {
            $dir = $GhArgs[[Array]::IndexOf($GhArgs, '--dir') + 1]
            $World.DownloadDir = $dir
            if ($World.Download.ExitCode -eq 0) { & $World.Lay $dir }
            return $World.Download
        }
        throw "unexpected gh call: $($GhArgs -join ' ')"
    }.GetNewClosure()
    return (Get-InformationalLaneReport -Gh $gh -Sha $sha)
}

function New-World {
    param([scriptblock]$Lay = { param($Dir) })
    return @{
        List        = (New-Reply -Lines @((New-RunListJson)))
        Download    = (New-Reply)
        Lay         = $Lay
        Calls       = (New-Object System.Collections.Generic.List[string])
        DownloadDir = $null
    }
}

function Get-TypographyViolationCount {
    param([string]$Text)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ('heimdall-infolane-notes-' + [Guid]::NewGuid().ToString('N') + '.md')
    try {
        [System.IO.File]::WriteAllText($path, $Text, [System.Text.UTF8Encoding]::new($false))
        return @(Get-NotesTypographyViolations -Path $path).Count
    } finally {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

$passing = @(
    @{ Name = 'Suite.PassingOne'; Outcome = 'Passed' },
    @{ Name = 'Suite.PassingTwo'; Outcome = 'Passed' }
)

# 1. No failure, with the ResultSummary trap set on the CIUnstable lane.
$world = New-World -Lay {
    param($Dir)
    Write-FixtureTrx -Root $Dir -Lane 'CIUnstable' -Results $passing -SummaryOutcome 'Failed'
    Write-FixtureTrx -Root $Dir -Lane 'RequiresDesktop' -Results $passing
}
$report = Invoke-WithWorld -World $world
Assert-True ($report.State -eq 'Read' -and @($report.Failures).Count -eq 0) "two clean lanes read as Read with no failure ($($report.State), $(@($report.Failures).Count) failure(s))"
Assert-True ($report.ResultCount -eq 4 -and @($report.UnreadLanes).Count -eq 0) "the result count proves the lanes were read ($($report.ResultCount) result(s))"
Assert-True (@(Get-InformationalLaneMessages -Report $report).Count -eq 0) 'a clean report prints no warning'
Assert-True ((Get-InformationalLaneNotes -Report $report) -eq '') 'a clean report adds nothing to the release notes'

# 9. The query.
$list = if ($world.Calls.Count -gt 0) { $world.Calls[0] } else { '' }
$download = if ($world.Calls.Count -gt 1) { $world.Calls[1] } else { '' }
Assert-True ($list -match "--commit $sha" -and $list -match '--workflow ci\.yml' -and $list -match '--event push') "the run read is the ci.yml push run of this commit ($list)"
Assert-True ($download -match "^run download $runId --name test-results --dir ") "the test-results artifact of that run is downloaded ($download)"

# 8. The download is removed afterwards.
Assert-True ($null -ne $world.DownloadDir -and -not (Test-Path -LiteralPath $world.DownloadDir)) 'the downloaded artifact is removed afterwards'

# 2. Two failures, one per lane, one name with a non-ASCII character.
$accent = [string][char]0x00E9
$world = New-World -Lay {
    param($Dir)
    Write-FixtureTrx -Root $Dir -Lane 'CIUnstable' -Results ($passing + @(@{ Name = 'Suite.Updater_Caf' + $accent + '_RelaunchesTheApp'; Outcome = 'Failed' }))
    Write-FixtureTrx -Root $Dir -Lane 'RequiresDesktop' -Results ($passing + @(@{ Name = 'Suite.SessionTree_MultiSelection'; Outcome = 'Failed' }))
}
$report = Invoke-WithWorld -World $world
$messages = @(Get-InformationalLaneMessages -Report $report)
$notes = Get-InformationalLaneNotes -Report $report
Assert-True ($report.State -eq 'Read' -and @($report.Failures).Count -eq 2) "two failures are read ($(@($report.Failures).Count))"
Assert-True ($messages.Count -eq 2 -and ($messages -join ' ') -match 'CIUnstable failed: Suite\.Updater_Caf\?_RelaunchesTheApp' -and ($messages -join ' ') -match 'RequiresDesktop failed: Suite\.SessionTree_MultiSelection') "each failure is printed with its lane ($($messages -join ' | '))"
Assert-True ($notes -match '### Informational test lanes' -and $notes -match '2 test\(s\) failed' -and $notes -match '- CIUnstable: Suite\.Updater_Caf\?_RelaunchesTheApp' -and $notes -match '- RequiresDesktop: Suite\.SessionTree_MultiSelection') 'the release notes name both failures under their lane'
Assert-True ($notes -match [regex]::Escape($runUrl) -and $notes -match 'never gate a release') 'the notes link the CI run and say the lanes do not gate'
Assert-True ((Get-TypographyViolationCount -Text $notes) -eq 0) 'the notes pass the release-notes typography guard, despite a non-ASCII test name'
Assert-True ($notes -notmatch 'not read') 'a read report does not claim the lanes were not read'

# 3. Not readable: every way, one warning, one notes section, no throw.
$cases = @(
    @{ Label = 'gh run list fails'; Mutate = { param($w) $w.List = New-Reply -ExitCode 1 -Lines @('gh: not logged in') }; Reason = 'gh run list failed' },
    @{ Label = 'gh run list is not JSON'; Mutate = { param($w) $w.List = New-Reply -Lines @('no runs today') }; Reason = 'not JSON' },
    @{ Label = 'no run for the SHA'; Mutate = { param($w) $w.List = New-Reply -Lines @('[]') }; Reason = 'no ci\.yml push run found' },
    @{ Label = 'only a run of another SHA'; Mutate = { param($w) $w.List = New-Reply -Lines @((New-RunListJson -HeadSha $otherSha)) }; Reason = 'no ci\.yml push run found' },
    @{ Label = 'the artifact has expired'; Mutate = { param($w) $w.Download = New-Reply -ExitCode 1 -Lines @('no valid artifacts found to download') }; Reason = 'could not be downloaded' }
)
foreach ($case in $cases) {
    $world = New-World -Lay { param($Dir) Write-FixtureTrx -Root $Dir -Lane 'CIUnstable' -Results $passing }
    & $case.Mutate $world
    $threw = $false
    $report = $null
    try { $report = Invoke-WithWorld -World $world } catch { $threw = $true }
    Assert-True (-not $threw) "$($case.Label): the reader does not throw"
    if ($null -ne $report) {
        $messages = @(Get-InformationalLaneMessages -Report $report)
        $notes = Get-InformationalLaneNotes -Report $report
        Assert-True ($report.State -eq 'NotRead' -and @($report.Failures).Count -eq 0) "$($case.Label): State NotRead"
        Assert-True ($messages.Count -eq 1 -and $messages[0] -match '^informational lanes not read: ' -and $messages[0] -match $case.Reason) "$($case.Label): the warning says the lanes were not read, and why ($($messages -join ' | '))"
        Assert-True ($notes -match '### Informational test lanes' -and $notes -match 'were not read for this release' -and $notes -match 'result for this commit is unknown') "$($case.Label): the notes say the result is unknown"
        Assert-True ((Get-TypographyViolationCount -Text $notes) -eq 0) "$($case.Label): the notes pass the typography guard"
    }
}

# 4. An artifact with no TRX is unknown, never clean.
$world = New-World -Lay { param($Dir) $null = New-Item -ItemType Directory -Path (Join-Path $Dir 'CIUnstable') -Force }
$report = Invoke-WithWorld -World $world
Assert-True ($report.State -eq 'NotRead' -and (@(Get-InformationalLaneMessages -Report $report) -join ' ') -match 'holds no test result') "zero results over empty lanes is NotRead, not clean ($($report.State))"

# 5. One lane without results: the other lane's failure is still reported.
$world = New-World -Lay {
    param($Dir)
    Write-FixtureTrx -Root $Dir -Lane 'CIUnstable' -Results ($passing + @(@{ Name = 'Suite.OnlyLaneFailure'; Outcome = 'Failed' }))
}
$report = Invoke-WithWorld -World $world
$messages = @(Get-InformationalLaneMessages -Report $report)
Assert-True ($report.State -eq 'Read' -and @($report.UnreadLanes).Count -eq 1 -and $report.UnreadLanes[0] -eq 'RequiresDesktop') "a lane without results is listed as unread ($(@($report.UnreadLanes) -join ','))"
Assert-True ($messages.Count -eq 2 -and ($messages -join ' ') -match 'not read: no result for RequiresDesktop' -and ($messages -join ' ') -match 'CIUnstable failed: Suite\.OnlyLaneFailure') 'both the unread lane and the failure of the other lane are printed'
Assert-True ((Get-InformationalLaneNotes -Report $report) -match 'No result could be read for the lane\(s\) RequiresDesktop') 'the notes name the unread lane'

# 6. Nested under TestResults/.
$world = New-World -Lay {
    param($Dir)
    $nested = Join-Path $Dir 'TestResults'
    Write-FixtureTrx -Root $nested -Lane 'CIUnstable' -Results ($passing + @(@{ Name = 'Suite.NestedFailure'; Outcome = 'Failed' }))
    Write-FixtureTrx -Root $nested -Lane 'RequiresDesktop' -Results $passing
}
$report = Invoke-WithWorld -World $world
Assert-True ($report.State -eq 'Read' -and @($report.Failures).Count -eq 1 -and $report.Failures[0].TestName -eq 'Suite.NestedFailure') 'lane directories under TestResults/ are read as well'

# 7. More failures than the notes list.
$many = @(1..60 | ForEach-Object { @{ Name = "Suite.Failing$_"; Outcome = 'Failed' } })
$world = New-World -Lay {
    param($Dir)
    Write-FixtureTrx -Root $Dir -Lane 'CIUnstable' -Results $many
    Write-FixtureTrx -Root $Dir -Lane 'RequiresDesktop' -Results $passing
}
$report = Invoke-WithWorld -World $world
$notes = Get-InformationalLaneNotes -Report $report
Assert-True (@($report.Failures).Count -eq 60 -and $notes -match '60 test\(s\) failed') 'all sixty failures are counted'
Assert-True ($notes -match '- and 10 more' -and ([regex]::Matches($notes, '- CIUnstable: ')).Count -eq $script:InformationalLaneListedFailures) "the notes list $($script:InformationalLaneListedFailures) names and summarise the rest"

# 10. Wiring.
$buildScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1'
$buildText = Get-Content -LiteralPath $buildScript -Raw
Assert-True ($buildText -match 'InformationalLaneReport\.ps1') 'Build.ps1 dot-sources the informational-lane reader'
Assert-True ($buildText -match 'Get-InformationalLaneReport[^\r\n]*-Sha \$preflight\.HeadSha') 'the lanes are read for the commit the preflight verified'
$decisionIndex = $buildText.IndexOf('Resolve-ReleaseGateOutcome @gateArgs')
$readIndex = $buildText.IndexOf('= Get-InformationalLaneReport')
Assert-True ($decisionIndex -ge 0 -and $readIndex -gt $decisionIndex) 'the lanes are read after the gate decision, so a refused release pays no download'
Assert-True ($buildText -notmatch 'releaseGateFailures\s*\+=[^\r\n]*[Ii]nformational') 'the informational lanes are never added to the gate failures'
Assert-True ($buildText -match '\$notes \+= \$informationalLaneNotes') 'the section reaches the generated release notes'
$notesIndex = $buildText.IndexOf('$notes += $informationalLaneNotes')
$downloadsIndex = $buildText.IndexOf('$notes += "### Downloads')
Assert-True ($notesIndex -ge 0 -and $downloadsIndex -gt $notesIndex) 'before the Downloads table'
$ciText = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '.github/workflows/ci.yml') -Raw
Assert-True ($ciText -match 'Test-InformationalLaneReport\.ps1') 'ci.yml runs this suite'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the informational lanes are read for the release commit, reported, and never block.' -ForegroundColor Green
exit 0
