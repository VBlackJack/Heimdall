<#
.SYNOPSIS
    Tests the informational-lane reporter (scripts/InformationalTestFailures.ps1).

.DESCRIPTION
    The reporter is the only thing that will ever say a test failed in a lane
    that cannot turn a run red, so a version of it that quietly reports nothing
    would restore exactly the blind spot it was written to remove. Each case
    below is chosen because a plausible implementation fails it:

      1. A failed test in a lane TRX             -> reported. The base case.
      2. A TRX whose ResultSummary carries       -> NOT reported. A text search
         outcome="Failed" while every test          for outcome="Failed", or an
         passed                                     XPath over `//*`, reports a
                                                    failure here where there is none.
      3. Failures in both lanes                  -> both reported. A reader that
                                                    stops at the first lane finds one.
      4. A TRX nested one directory deeper       -> reported. A non-recursive
                                                    enumeration misses it, and CI
                                                    has nested results before.
      5. A lane of passing tests                 -> zero failures AND a non-zero
                                                    result count. Without the
                                                    namespace the selection matches
                                                    nothing and this zero would be
                                                    indistinguishable from a clean lane.
      6. An absent lane directory                -> no throw, Present false. The
                                                    step runs with `if: always()`,
                                                    so it meets this on any job that
                                                    failed before the test steps.
      7. The failure message                     -> carried through, so the payload
                                                    that tells one cause from another
                                                    survives into the run log.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-InformationalTestFailures.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'InformationalTestFailures.ps1')

$failures = 0
$laneNames = @('CIUnstable', 'RequiresDesktop')

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-FixtureRoot {
    <#
    .SYNOPSIS
        A throwaway results directory, shaped like the one the test steps write.
    #>
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-infolanes-" + [System.Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path -Force
    return $path
}

function Write-FixtureTrx {
    <#
    .SYNOPSIS
        One TRX holding the given results, under a lane directory.
    .PARAMETER Results
        One hashtable per test: Name, Outcome, Duration, Message.
    .PARAMETER SummaryOutcome
        The outcome attribute of the ResultSummary element. It is deliberately
        settable: a real TRX of a failing run carries Failed here, and that
        attribute is the trap case 2 exists for.
    #>
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Lane,
        [Parameter(Mandatory)] [string] $FileName,
        [Parameter(Mandatory)] [hashtable[]] $Results,
        [string] $SummaryOutcome = 'Completed',
        [string] $SubDirectory
    )

    $laneRoot = Join-Path $Root $Lane
    if ($SubDirectory) { $laneRoot = Join-Path $laneRoot $SubDirectory }
    $null = New-Item -ItemType Directory -Path $laneRoot -Force

    $builder = New-Object System.Text.StringBuilder
    $null = $builder.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    $null = $builder.AppendLine('<TestRun id="00000000-0000-0000-0000-000000000001" name="fixture" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">')
    $null = $builder.AppendLine('  <Results>')
    foreach ($result in $Results) {
        $name = [System.Security.SecurityElement]::Escape([string]$result.Name)
        $outcome = [System.Security.SecurityElement]::Escape([string]$result.Outcome)
        $duration = [System.Security.SecurityElement]::Escape([string]$result.Duration)
        $null = $builder.AppendLine("    <UnitTestResult testName=`"$name`" outcome=`"$outcome`" duration=`"$duration`">")
        if ($result.ContainsKey('Message')) {
            $message = [System.Security.SecurityElement]::Escape([string]$result.Message)
            $null = $builder.AppendLine('      <Output><ErrorInfo>')
            $null = $builder.AppendLine("        <Message>$message</Message>")
            $null = $builder.AppendLine('      </ErrorInfo></Output>')
        }
        $null = $builder.AppendLine('    </UnitTestResult>')
    }
    $null = $builder.AppendLine('  </Results>')
    $null = $builder.AppendLine("  <ResultSummary outcome=`"$SummaryOutcome`" />")
    $null = $builder.AppendLine('</TestRun>')

    $path = Join-Path $laneRoot $FileName
    [System.IO.File]::WriteAllText($path, $builder.ToString(), [System.Text.UTF8Encoding]::new($false))
    return $path
}

function Remove-FixtureRoot {
    param([string] $Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 1 and 7. A failed test is reported, with its message.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'CIUnstable' -FileName 'app.trx' -SummaryOutcome 'Failed' -Results @(
        @{ Name = 'Suite.PassingTest'; Outcome = 'Passed'; Duration = '00:00:00.0100000' },
        @{ Name = 'Suite.RelauncherTest'; Outcome = 'Failed'; Duration = '00:00:26.9082654'
           Message = "System.TimeoutException : 'relaunch-target' never recorded within 20 s.`nhost exit code: 1" }
    )

    $found = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames)
    Assert-True ($found.Count -eq 1) "a failed test in an informational lane is reported ($($found.Count) found)"
    if ($found.Count -eq 1) {
        Assert-True ($found[0].TestName -eq 'Suite.RelauncherTest') 'the reported failure names the failing test, not its passing neighbour'
        Assert-True ($found[0].Lane -eq 'CIUnstable') 'the reported failure names its lane'
        Assert-True ($found[0].Duration -eq '00:00:26.9082654') 'the duration is carried through unrounded'
        Assert-True ($found[0].Message -match 'never recorded within 20 s') 'the failure message is carried through, payload included'
    }
}
finally { Remove-FixtureRoot -Path $root }

# 2. The ResultSummary trap: a text search for outcome="Failed" reports a failure here.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'CIUnstable' -FileName 'green.trx' -SummaryOutcome 'Failed' -Results @(
        @{ Name = 'Suite.One'; Outcome = 'Passed'; Duration = '00:00:00.0010000' },
        @{ Name = 'Suite.Two'; Outcome = 'Passed'; Duration = '00:00:00.0020000' }
    )

    $found = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames)
    Assert-True ($found.Count -eq 0) 'a ResultSummary carrying outcome="Failed" is not counted as a failed test'

    $coverage = @(Get-InformationalLaneCoverage -ResultsRoot $root -LaneNames $laneNames)
    $unstable = $coverage | Where-Object { $_.Lane -eq 'CIUnstable' }
    Assert-True ($unstable.ResultCount -eq 2) "the file was really read, so that zero is a measurement ($($unstable.ResultCount) results)"
}
finally { Remove-FixtureRoot -Path $root }

# 3. Both lanes are read, not just the first.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'CIUnstable' -FileName 'a.trx' -Results @(
        @{ Name = 'Suite.UnstableFailure'; Outcome = 'Failed'; Duration = '00:00:01.0000000'; Message = 'first lane' }
    )
    $null = Write-FixtureTrx -Root $root -Lane 'RequiresDesktop' -FileName 'b.trx' -Results @(
        @{ Name = 'Suite.DesktopFailure'; Outcome = 'Failed'; Duration = '00:00:02.0000000'; Message = 'second lane' }
    )

    $found = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames)
    Assert-True ($found.Count -eq 2) "a failure in each lane gives two reports ($($found.Count) found)"
    Assert-True (@($found | Where-Object { $_.Lane -eq 'RequiresDesktop' }).Count -eq 1) 'the second lane is read, not only the first'
}
finally { Remove-FixtureRoot -Path $root }

# 4. A TRX nested one directory deeper is still found.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'CIUnstable' -FileName 'nested.trx' -SubDirectory 'runner_2026-09-07' -Results @(
        @{ Name = 'Suite.NestedFailure'; Outcome = 'Failed'; Duration = '00:00:03.0000000'; Message = 'nested' }
    )

    $found = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames)
    Assert-True ($found.Count -eq 1) 'a TRX in a subdirectory of the lane is read, so the sweep is recursive'
}
finally { Remove-FixtureRoot -Path $root }

# 5. A clean lane: zero failures, and a result count proving the zero was measured.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'RequiresDesktop' -FileName 'clean.trx' -Results @(
        @{ Name = 'Suite.One'; Outcome = 'Passed'; Duration = '00:00:00.0010000' },
        @{ Name = 'Suite.Two'; Outcome = 'Passed'; Duration = '00:00:00.0020000' },
        @{ Name = 'Suite.Three'; Outcome = 'Passed'; Duration = '00:00:00.0030000' }
    )

    $found = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames)
    Assert-True ($found.Count -eq 0) 'a lane of passing tests reports no failure'

    $coverage = @(Get-InformationalLaneCoverage -ResultsRoot $root -LaneNames $laneNames)
    $desktop = $coverage | Where-Object { $_.Lane -eq 'RequiresDesktop' }
    Assert-True ($desktop.TrxFiles -eq 1 -and $desktop.ResultCount -eq 3) "coverage separates a clean lane from an unread one ($($desktop.TrxFiles) file(s), $($desktop.ResultCount) result(s))"
}
finally { Remove-FixtureRoot -Path $root }

# 6. An absent lane directory is a state, not an error.
$root = New-FixtureRoot
try {
    $null = Write-FixtureTrx -Root $root -Lane 'CIUnstable' -FileName 'only.trx' -Results @(
        @{ Name = 'Suite.One'; Outcome = 'Passed'; Duration = '00:00:00.0010000' }
    )

    $coverage = @(Get-InformationalLaneCoverage -ResultsRoot $root -LaneNames $laneNames)
    $desktop = $coverage | Where-Object { $_.Lane -eq 'RequiresDesktop' }
    Assert-True ($null -ne $desktop -and -not $desktop.Present) 'an absent lane directory is reported as absent'
    Assert-True (@(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $laneNames).Count -eq 0) 'an absent lane directory does not throw'
}
finally { Remove-FixtureRoot -Path $root }

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the informational-lane reporter reads the TRX, both lanes, and only real test results.' -ForegroundColor Green
exit 0
