<#
.SYNOPSIS
    The CI vulnerability scan: runs `dotnet list package --vulnerable`, then fails when
    the scan itself failed and warns when it found something.

.DESCRIPTION
    A vulnerable package stays a workflow warning, as before: advisory databases carry
    false positives and advisories with no upgrade path, and a maintainer decides. What
    changed is the other branch. A scan that did not happen (non-zero exit code twice in a row, output
    that is not the report, a project of the solution missing from it) used to print
    "No vulnerable dependencies detected."; it now fails the step. The decision is
    Get-VulnerableDependencyVerdict in scripts/VulnerableDependencyReport.ps1.

.PARAMETER Solution
    The solution to scan. Defaults to Heimdall.slnx at the repository root.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run with:  pwsh -NoProfile -File scripts\Invoke-VulnerableDependencyCheck.ps1
#>
[CmdletBinding()]
param(
    [string]$Solution
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'VulnerableDependencyReport.ps1')

if ([string]::IsNullOrWhiteSpace($Solution)) {
    $Solution = Join-Path (Split-Path -Parent $PSScriptRoot) 'Heimdall.slnx'
}

$expected = @()
if (Test-Path -LiteralPath $Solution -PathType Leaf) {
    $expected = @(Get-SolutionProjectNames -SolutionPath $Solution)
}

# stdout carries the JSON report; stderr is kept apart so a warning line cannot
# corrupt it, and shown alongside.
$runScan = {
    $stderrFile = [System.IO.Path]::GetTempFileName()
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $stdout = @(& dotnet list $Solution package --vulnerable --include-transitive --format json 2> $stderrFile)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEAP
    }
    $stderr = [System.IO.File]::ReadAllText($stderrFile)
    Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue

    $output = ($stdout | ForEach-Object { "$_" }) -join "`n"
    Write-Host $output
    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

$result = Invoke-VulnerableDependencyScan -Runner $runScan -ExpectedProjects $expected
switch ($result.Verdict) {
    'Clean' {
        Write-Host "No vulnerable dependencies detected across $($expected.Count) projects."
        exit 0
    }
    'Vulnerable' {
        foreach ($finding in $result.Findings) { Write-Host "  $finding" }
        Write-Host "::warning::Vulnerable dependencies detected ($($result.Findings.Count)) - see output above"
        exit 0
    }
    default {
        foreach ($reason in $result.Reasons) { Write-Host "  $reason" }
        Write-Host "::error::The vulnerable-dependency scan did not run to completion, so its silence proves nothing - see output above"
        exit 1
    }
}
