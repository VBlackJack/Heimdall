<#
.SYNOPSIS
    The CI audit of the vendored milkdown editor's npm dependencies: runs
    `npm audit --omit=dev --package-lock-only`, prints what it found, and fails at high
    or critical.

.DESCRIPTION
    The editor is committed as a built bundle, so NuGet's scan cannot see its npm
    dependencies. The audit reads package-lock.json alone: no `npm install`. Below high it
    is informational (warnings, exit 0); at high or critical it exits 1, except for the
    advisories listed in npm-audit-baseline.json next to the lock, each with a reason.
    The decision is Get-NpmAuditVerdict in scripts/NpmAuditVerdict.ps1.

.PARAMETER ProjectDirectory
    The directory holding package-lock.json. Defaults to the milkdown editor.

.PARAMETER BaselinePath
    The accepted-advisories file. Defaults to npm-audit-baseline.json in ProjectDirectory.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run with:  pwsh -NoProfile -File scripts\Invoke-NpmAuditCheck.ps1
#>
[CmdletBinding()]
param(
    [string]$ProjectDirectory,
    [string]$BaselinePath
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NpmAuditVerdict.ps1')

if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) {
    $ProjectDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Heimdall.App/Assets/milkdown-editor'
}
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $ProjectDirectory 'npm-audit-baseline.json'
}

$baseline = @(Read-NpmAuditBaseline -Path $BaselinePath)

# stdout carries the JSON report; stderr is kept apart so a notice line cannot corrupt it.
# npm exits 1 whenever it found anything, so the exit code is not the verdict.
$runAudit = {
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location $ProjectDirectory
    try {
        $stdout = @(& npm audit --omit=dev --package-lock-only --json 2> $stderrFile)
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
        $ErrorActionPreference = $prevEAP
    }
    $stderr = [System.IO.File]::ReadAllText($stderrFile)
    Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($stderr)) { Write-Host $stderr }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = (($stdout | ForEach-Object { "$_" }) -join "`n") }
}

$verdict = Invoke-NpmAuditScan -Runner $runAudit -Baseline $baseline

if ($verdict.Verdict -eq 'ToolFailure') {
    foreach ($reason in $verdict.Reasons) { Write-Host "  $reason" }
    Write-Host $verdict.Output
    Write-Host '::error::The npm audit did not run to completion, so its silence proves nothing - see output above'
    exit 1
}

Write-Host ("npm audit of {0}: {1}" -f $ProjectDirectory, ((@('critical', 'high', 'moderate', 'low', 'info') | ForEach-Object { "$_=$($verdict.Counts.$_)" }) -join ' '))
foreach ($finding in $verdict.Findings) {
    $mark = if ($finding.Accepted) { ' (accepted by the baseline)' } else { '' }
    Write-Host ("  [{0}] {1} {2}{3}: {4}" -f $finding.Severity, $finding.Package, $finding.Id, $mark, $finding.Title)
}
# One annotation for the run summary: a warning per advisory would bury the page.
$informational = @($verdict.Findings | Where-Object { ($script:NpmAuditFailingSeverities -notcontains $_.Severity) -or $_.Accepted })
if ($informational.Count -gt 0) {
    $acceptedCount = @($informational | Where-Object { $_.Accepted }).Count
    Write-Host "::warning::npm audit of the milkdown editor: $($informational.Count) advisory(ies) reported, $acceptedCount of them high and accepted by the baseline - see the step log"
}
foreach ($stale in $verdict.StaleBaseline) {
    Write-Host "::warning::npm audit baseline entry $stale is no longer reported; remove it from npm-audit-baseline.json"
}

if ($verdict.Verdict -eq 'Fail') {
    $blocking = @($verdict.Findings | Where-Object { ($script:NpmAuditFailingSeverities -contains $_.Severity) -and -not $_.Accepted })
    foreach ($finding in $blocking) {
        Write-Host ("::error::npm {0} {1} {2}: {3} ({4})" -f $finding.Severity, $finding.Package, $finding.Id, $finding.Title, $finding.Url)
    }
    Write-Host "$($blocking.Count) high or critical advisory(ies) outside the baseline. Update package-lock.json and rebuild dist/index.html, or accept the advisory in npm-audit-baseline.json with a reason."
    exit 1
}

if ($verdict.Verdict -eq 'Clean') {
    Write-Host 'No advisory reported for the milkdown editor dependencies.'
} else {
    Write-Host 'No high or critical advisory outside the baseline.'
}
exit 0
