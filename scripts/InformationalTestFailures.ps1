<#
.SYNOPSIS
    Prints the tests that failed in the informational CI lanes, which a green run
    hides completely.

.DESCRIPTION
    Two test steps in .github/workflows/ci.yml carry `continue-on-error: true`:
    the CIUnstable lane and the RequiresDesktop lane. A test that fails there
    leaves the workflow at `conclusion: success`, no step is marked red, and
    nothing in the run says a test failed. The only surviving trace is the TRX
    inside the `test-results` artifact, which somebody has to download and parse
    on purpose.

    Measured on 2026-09-07 over the 99 green runs since 2026-08-26: 1 179 440
    test results, and seven failed tests inside runs GitHub called successful.
    One of them was the occurrence BL-0067 had been waiting a fortnight for, on
    master, in the release run for v2026.090505 - captured, never replayed, and
    never seen. An item whose reopening trigger is "the lane turns red" cannot
    fire when its tests no longer sit in a lane that can turn red.

    So this step does not change what blocks. It makes the informational lanes
    audible: every failure is printed under a greppable marker and raised as a
    workflow warning, so the signal reaches the run summary without gating a PR.

    Two measurement traps this script exists to avoid, both met while measuring
    the runs above:

      1. Do not grep for `outcome="Failed"`. The `ResultSummary` element of a TRX
         carries the same attribute, so a text search reports a failure in a file
         whose tests all passed. Only `UnitTestResult` elements are counted here,
         selected through the TRX namespace.
      2. A zero is not evidence on its own. A wrong path, a missing namespace or
         an empty lane all produce the same silent zero as a clean run, so the
         summary always states how many TRX files were read and how many results
         they held. A zero over zero results is reported as such, never as "no
         failures".

    Dot-source this file to get Get-InformationalTestFailures and
    Get-InformationalLaneCoverage; run it to report on a results directory.
    It exits 0 whatever it finds: this is an observation, not a gate.

.PARAMETER ResultsRoot
    The directory the test steps write into, holding one subdirectory per
    informational lane. Defaults to TestResults beside the repository root.

.PARAMETER LaneNames
    The lane subdirectories to read. Defaults to the two lanes ci.yml runs with
    `continue-on-error: true`; pass a subset to look at one of them.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\InformationalTestFailures.ps1
#>
param(
    [string] $ResultsRoot,
    [string[]] $LaneNames
)

Set-StrictMode -Version Latest

# The lanes ci.yml runs with continue-on-error, and the directories they write
# into. One list, so adding a lane to the workflow means adding it here and
# nowhere else.
$script:DefaultInformationalLaneNames = @('CIUnstable', 'RequiresDesktop')

# The TRX schema every vstest logger emits. Selecting without it silently matches
# nothing, which is the zero this script refuses to report as a clean result.
$script:TrxNamespaceUri = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'

# How much of a failure message to print. Long enough to carry the payload that
# tells one cause from another (a host exit code, a process state), short enough
# that a run log stays readable.
$script:MessageExcerptLength = 600

function Get-TrxNamespaceManager {
    <#
    .SYNOPSIS
        A namespace manager binding the prefix `t` to the TRX schema.
    #>
    param([Parameter(Mandatory)] [System.Xml.XmlDocument] $Document)

    $manager = New-Object System.Xml.XmlNamespaceManager($Document.NameTable)
    $manager.AddNamespace('t', $script:TrxNamespaceUri)
    # The comma is load-bearing: XmlNamespaceManager enumerates its prefixes, so
    # returning it bare hands the caller four strings instead of one manager, and
    # SelectNodes then refuses the argument.
    return , $manager
}

function Get-InformationalLaneCoverage {
    <#
    .SYNOPSIS
        What was actually read, so a zero can be told from a miss.
    .OUTPUTS
        One object per lane: Lane, Present (the directory exists), TrxFiles and
        ResultCount (UnitTestResult elements, whatever their outcome).
    #>
    param(
        [Parameter(Mandatory)] [string] $ResultsRoot,
        [Parameter(Mandatory)] [string[]] $LaneNames
    )

    $coverage = @()
    foreach ($lane in $LaneNames) {
        $laneRoot = Join-Path $ResultsRoot $lane
        $present = Test-Path -LiteralPath $laneRoot
        $files = @()
        $results = 0

        if ($present) {
            # Recursive: a logger writes one file per assembly and CI has already
            # nested them one directory deeper than expected once.
            $files = @(Get-ChildItem -LiteralPath $laneRoot -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue)
            foreach ($file in $files) {
                $document = New-Object System.Xml.XmlDocument
                $document.Load($file.FullName)
                $manager = Get-TrxNamespaceManager -Document $document
                $results += @($document.SelectNodes('//t:UnitTestResult', $manager)).Count
            }
        }

        $coverage += [PSCustomObject]@{
            Lane        = $lane
            Present     = $present
            TrxFiles    = $files.Count
            ResultCount = $results
        }
    }

    return $coverage
}

function Get-InformationalTestFailures {
    <#
    .SYNOPSIS
        Every failed test recorded in the informational lanes of a results directory.
    .OUTPUTS
        One object per failed test: Lane, TrxFile, TestName, Duration and Message.
    #>
    param(
        [Parameter(Mandatory)] [string] $ResultsRoot,
        [Parameter(Mandatory)] [string[]] $LaneNames
    )

    $failures = @()
    foreach ($lane in $LaneNames) {
        $laneRoot = Join-Path $ResultsRoot $lane
        if (-not (Test-Path -LiteralPath $laneRoot)) { continue }

        foreach ($file in @(Get-ChildItem -LiteralPath $laneRoot -Filter '*.trx' -Recurse -File -ErrorAction SilentlyContinue)) {
            $document = New-Object System.Xml.XmlDocument
            $document.Load($file.FullName)
            $manager = Get-TrxNamespaceManager -Document $document

            # UnitTestResult only. ResultSummary carries an outcome attribute too,
            # and counting it would report a failure in a file where every test passed.
            foreach ($node in @($document.SelectNodes("//t:UnitTestResult[@outcome='Failed']", $manager))) {
                $messageNode = $node.SelectSingleNode('.//t:Message', $manager)
                $message = if ($null -ne $messageNode) { [string]$messageNode.InnerText } else { '' }

                $failures += [PSCustomObject]@{
                    Lane     = $lane
                    TrxFile  = $file.Name
                    TestName = [string]$node.GetAttribute('testName')
                    Duration = [string]$node.GetAttribute('duration')
                    Message  = $message
                }
            }
        }
    }

    return $failures
}

function Get-MessageExcerpt {
    <#
    .SYNOPSIS
        The head of a failure message, on one line, for a run log.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Message)

    $flat = ($Message -replace '\s+', ' ').Trim()
    if ($flat.Length -le $script:MessageExcerptLength) { return $flat }
    return $flat.Substring(0, $script:MessageExcerptLength) + '...'
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'

    $root = if ($PSBoundParameters.ContainsKey('ResultsRoot') -and $ResultsRoot) {
        $ResultsRoot
    } else {
        Join-Path (Split-Path -Parent $PSScriptRoot) 'TestResults'
    }
    $lanes = if ($PSBoundParameters.ContainsKey('LaneNames') -and $LaneNames) {
        $LaneNames
    } else {
        $script:DefaultInformationalLaneNames
    }

    Write-Host ("Informational lanes under '{0}': {1}" -f $root, ($lanes -join ', '))

    if (-not (Test-Path -LiteralPath $root)) {
        # Not an error: the job may have failed before any test step ran, and this
        # step is deliberately wired with `if: always()`.
        Write-Host "No results directory. Nothing was measured, so nothing is claimed."
        exit 0
    }

    $coverage = @(Get-InformationalLaneCoverage -ResultsRoot $root -LaneNames $lanes)
    foreach ($lane in $coverage) {
        Write-Host ("  {0}: {1} TRX file(s), {2} test result(s){3}" -f `
            $lane.Lane, $lane.TrxFiles, $lane.ResultCount, $(if ($lane.Present) { '' } else { ' (lane directory absent)' }))
    }

    $failures = @(Get-InformationalTestFailures -ResultsRoot $root -LaneNames $lanes)
    $totalResults = ($coverage | Measure-Object -Property ResultCount -Sum).Sum

    if ($failures.Count -eq 0) {
        if ($totalResults -eq 0) {
            # The distinction this script exists to keep: nothing read is not nothing wrong.
            Write-Host 'No test results were read from the informational lanes, so their state is UNKNOWN, not clean.'
        } else {
            Write-Host ("No failure in the informational lanes ({0} test result(s) read)." -f $totalResults) -ForegroundColor Green
        }
        exit 0
    }

    Write-Host ''
    Write-Host ("{0} test(s) failed in the informational lanes. The run stays green: these lanes do not block." -f $failures.Count) -ForegroundColor Yellow
    foreach ($failure in $failures) {
        Write-Host ("[INFO-FAIL] {0} | {1} | {2}" -f $failure.Lane, $failure.Duration, $failure.TestName) -ForegroundColor Yellow
        Write-Host ("            {0}" -f (Get-MessageExcerpt -Message $failure.Message))
        Write-Host ("            {0}" -f $failure.TrxFile)
        Write-Host ("::warning::{0} failed in the informational {1} lane after {2}" -f $failure.TestName, $failure.Lane, $failure.Duration)
    }
    Write-Host ''
    Write-Host 'These are observations, not gates. Read the full TRX in the test-results artifact'
    Write-Host 'before drawing a conclusion: the console cannot be trusted to attach a message to'
    Write-Host 'the right test when assemblies run in parallel.'

    exit 0
}
