<#
.SYNOPSIS
    Reads the result of `npm audit --json` for the vendored milkdown editor and decides
    whether the audit actually happened, and what it found.

.DESCRIPTION
    Dot-source this file to get Get-NpmAuditVerdict, Read-NpmAuditBaseline and
    Invoke-NpmAuditScan; run scripts/Invoke-NpmAuditCheck.ps1 for the CI step itself.

    src/Heimdall.App/Assets/milkdown-editor is built into a single HTML bundle
    (dist/index.html) that is committed, so NuGet's vulnerability scan never sees the
    npm dependencies inside it. `npm audit --omit=dev --package-lock-only` reads the
    lock alone: no install, no node_modules, only the registry's advisory endpoint.

    The rule, as decided: below high the audit is informational (printed and raised as
    workflow warnings, the step stays green); at high or critical the step fails. Two
    refinements keep that rule honest rather than noisy:

      * Advisories are counted, not packages. npm reports a package as high because a
        dependency of it is (a package whose `via` is the name of another package). Only
        the advisory objects are findings, deduplicated by GHSA identifier, so one
        advisory reported on two version ranges is one finding.
      * A baseline lists the high advisories that were already in the lock when the gate
        was introduced, each with a reason. Without it the gate would have turned master
        red, and so blocked every release (the preflight needs a green CI run), over
        findings that only a rebuild of the committed bundle can clear. An advisory in
        the baseline is printed as a warning; a high or critical advisory NOT in it fails.
        An entry the audit no longer reports is flagged as stale so the file shrinks.
        An empty baseline restores the plain rule.

    Fail-closed on the tool side, as in scripts/VulnerableDependencyReport.ps1: output that
    is not an audit report, a report that carries an `error`, a report version this script
    was not written for, or a high count with no advisory behind it is ToolFailure, never
    Clean. A silent audit proves nothing.

    Verdicts:
      Clean        the audit ran, no advisory at all
      Warn         the audit ran; advisories below high, or high ones all in the baseline
      Fail         at least one high or critical advisory outside the baseline
      ToolFailure  the audit cannot be trusted; Reasons says why

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>

# The only `npm audit --json` report layout this script was written against.
$script:NpmAuditReportVersion = 2
# Severities that fail the step, as npm names them.
$script:NpmAuditFailingSeverities = @('high', 'critical')
# A failed audit (a registry hiccup on the runner) is retried once, then fails the step.
$script:NpmAuditAttempts = 2

function Get-NpmAdvisoryId {
    <#
    .SYNOPSIS
        The GHSA identifier at the end of an advisory URL; the npm source number when the
        URL carries none.
    #>
    param($Advisory)

    $url = [string]$Advisory.url
    if ($url -match '(GHSA-[0-9a-z]{4}-[0-9a-z]{4}-[0-9a-z]{4})') { return $Matches[1] }
    return "npm-$([string]$Advisory.source)"
}

function Read-NpmAuditBaseline {
    <#
    .SYNOPSIS
        The accepted advisories of a baseline file: an object per entry with Advisory,
        Package and Reason. A missing file is an empty baseline; an entry without a reason
        throws, because an unexplained acceptance is the silent kind.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    $document = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($Path))
    $entries = @()
    if ($null -ne $document.PSObject.Properties['accepted']) { $entries = @($document.accepted) }
    $baseline = @()
    foreach ($entry in $entries) {
        $advisory = [string]$entry.advisory
        $reason = [string]$entry.reason
        if ([string]::IsNullOrWhiteSpace($advisory)) { throw "The npm audit baseline $Path has an entry without an advisory identifier." }
        if ([string]::IsNullOrWhiteSpace($reason)) { throw "The npm audit baseline entry $advisory has no reason: an acceptance must say why." }
        $baseline += [pscustomobject]@{ Advisory = $advisory; Package = [string]$entry.package; Reason = $reason }
    }
    return $baseline
}

function Get-NpmAuditVerdict {
    <#
    .SYNOPSIS
        Classifies the output of `npm audit --json`.
    .PARAMETER Output
        The standard output of the audit, JSON text.
    .PARAMETER Baseline
        The accepted high advisories, from Read-NpmAuditBaseline.
    .OUTPUTS
        An object with Verdict, Reasons (why a ToolFailure), Findings (one per advisory:
        Package, Severity, Id, Title, Url, Accepted), StaleBaseline (baseline ids the audit
        no longer reports) and Counts (npm's own per-severity totals).
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()] [string]$Output = '',
        [AllowEmptyCollection()] [object[]]$Baseline = @()
    )

    $result = {
        param($Verdict, $Reasons, $Findings, $Stale, $Counts)
        return [pscustomobject]@{
            Verdict       = $Verdict
            Reasons       = @($Reasons)
            Findings      = @($Findings)
            StaleBaseline = @($Stale)
            Counts        = $Counts
        }
    }

    $text = $Output.Trim()
    $report = $null
    if (-not [string]::IsNullOrWhiteSpace($text)) {
        try { $report = ConvertFrom-Json -InputObject $text } catch { $report = $null }
    }
    if ($null -eq $report) {
        return (& $result 'ToolFailure' @('npm audit printed output that is not a JSON report.') @() @() $null)
    }
    if ($null -ne $report.PSObject.Properties['error']) {
        $code = [string]$report.error.code
        $summary = [string]$report.error.summary
        return (& $result 'ToolFailure' @("npm audit reported an error ($code): $summary") @() @() $null)
    }
    $version = $null
    if ($null -ne $report.PSObject.Properties['auditReportVersion']) { $version = $report.auditReportVersion }
    if ($version -ne $script:NpmAuditReportVersion) {
        return (& $result 'ToolFailure' @("npm audit report version '$version' is not the $script:NpmAuditReportVersion this script reads.") @() @() $null)
    }
    if ($null -eq $report.PSObject.Properties['vulnerabilities'] -or $null -eq $report.PSObject.Properties['metadata']) {
        return (& $result 'ToolFailure' @('npm audit report has no vulnerabilities or metadata section.') @() @() $null)
    }

    $counts = $report.metadata.vulnerabilities
    $acceptedIds = @($Baseline | ForEach-Object { [string]$_.Advisory })
    # An entry names the package it was accepted on; the same advisory showing up on another
    # package is a new finding.
    $isAccepted = {
        param($Id, $PackageName)
        foreach ($entry in $Baseline) {
            if ([string]$entry.Advisory -ne $Id) { continue }
            if ([string]::IsNullOrWhiteSpace([string]$entry.Package) -or [string]$entry.Package -eq $PackageName) { return $true }
        }
        return $false
    }

    # Only advisory objects are findings. A string in `via` names another package of the
    # chain, whose own advisories are counted on its own entry.
    $findings = @{}
    foreach ($package in @($report.vulnerabilities.PSObject.Properties)) {
        foreach ($via in @($package.Value.via)) {
            if ($via -is [string]) { continue }
            $id = Get-NpmAdvisoryId -Advisory $via
            $severity = ([string]$via.severity).ToLowerInvariant()
            $key = "$($package.Name)|$id"
            if (-not $findings.ContainsKey($key)) {
                $failing = $script:NpmAuditFailingSeverities -contains $severity
                $findings[$key] = [pscustomobject]@{
                    Package  = [string]$package.Name
                    Severity = $severity
                    Id       = $id
                    Title    = [string]$via.title
                    Url      = [string]$via.url
                    Accepted = ($failing -and [bool](& $isAccepted $id ([string]$package.Name)))
                }
            }
        }
    }
    $ordered = @($findings.Values | Sort-Object -Property @{ Expression = { $script:NpmAuditFailingSeverities -contains $_.Severity }; Descending = $true }, Package, Id)

    # npm says high packages exist but gives no advisory behind them: the layout moved,
    # and counting zero findings would read as clean.
    $reportedFailing = 0
    foreach ($severity in $script:NpmAuditFailingSeverities) {
        if ($null -ne $counts.PSObject.Properties[$severity]) { $reportedFailing += [int]$counts.$severity }
    }
    $failingFindings = @($ordered | Where-Object { $script:NpmAuditFailingSeverities -contains $_.Severity })
    if ($reportedFailing -gt 0 -and $failingFindings.Count -eq 0) {
        return (& $result 'ToolFailure' @("npm audit counts $reportedFailing high or critical package(s) but lists no advisory behind them; the report layout changed.") $ordered @() $counts)
    }

    $reportedIds = @($ordered | ForEach-Object { $_.Id })
    $stale = @($acceptedIds | Where-Object { $reportedIds -notcontains $_ })

    $unaccepted = @($failingFindings | Where-Object { -not $_.Accepted })
    if ($unaccepted.Count -gt 0) {
        return (& $result 'Fail' @() $ordered $stale $counts)
    }
    if ($ordered.Count -gt 0) {
        return (& $result 'Warn' @() $ordered $stale $counts)
    }
    return (& $result 'Clean' @() @() $stale $counts)
}

function Invoke-NpmAuditScan {
    <#
    .SYNOPSIS
        Runs the audit, retries it once when it could not be trusted, and classifies it.
    .PARAMETER Runner
        Runs the audit once and returns an object with ExitCode and Output (the JSON text).
    .OUTPUTS
        The Get-NpmAuditVerdict object, plus Attempts and Output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Runner,
        [AllowEmptyCollection()] [object[]]$Baseline = @()
    )

    $attempt = 0
    do {
        $attempt++
        $run = & $Runner
        $verdict = Get-NpmAuditVerdict -Output ([string]$run.Output) -Baseline $Baseline
        if ($verdict.Verdict -ne 'ToolFailure') { break }
        Write-Host "npm audit attempt $attempt of $script:NpmAuditAttempts could not be trusted: $(@($verdict.Reasons) -join ' ')"
    } while ($attempt -lt $script:NpmAuditAttempts)

    $verdict | Add-Member -NotePropertyName Attempts -NotePropertyValue $attempt
    $verdict | Add-Member -NotePropertyName Output -NotePropertyValue ([string]$run.Output)
    return $verdict
}
