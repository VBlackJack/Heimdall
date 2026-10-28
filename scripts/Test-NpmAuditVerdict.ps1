<#
.SYNOPSIS
    Tests the npm audit verdict (scripts/NpmAuditVerdict.ps1) on recorded `npm audit --json`
    shapes, the baseline mechanism against the recorded real report, and the CI wiring.

.DESCRIPTION
    The audit covers the npm dependencies baked into the committed milkdown bundle, which
    NuGet's scan cannot see. Below high it informs; at high or critical it fails. Each case
    is chosen because a plausible implementation fails it:

      1. No vulnerability                       -> Clean.
      2. Only low and moderate advisories       -> Warn: printed, never failing.
      3. A high advisory outside the baseline   -> Fail, naming package and GHSA.
      4. A critical one                         -> Fail.
      5. The same high advisory in the baseline -> Warn, marked accepted; accepted for one
                                                   package only, not for another carrying
                                                   the same advisory.
      6. A package that is high only through    -> not a finding of its own; the advisory
         a dependency (via is a string)            behind it is counted once.
      7. One GHSA reported on two ranges        -> one finding.
      8. A baseline entry without a reason      -> refused when read.
      9. A baseline entry the audit no longer   -> reported as stale, verdict unchanged.
         reports
     10. Not JSON, an npm error, a report       -> ToolFailure, never Clean: a silent audit
         version we do not read, a high count     proves nothing.
         with no advisory behind it
     11. The recorded real report of 2026-10-28 -> with an empty baseline Fail with all ten
                                                   high advisories (the parser sees the real
                                                   shape); with a baseline built from it Warn.
     12. A first attempt that cannot be         -> retried once, then trusted.
         trusted
     13. ci.yml runs the check and this suite.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-NpmAuditVerdict.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NpmAuditVerdict.ps1')

$failures = 0
$repoRoot = Split-Path -Parent $PSScriptRoot
$baselineFile = Join-Path $repoRoot 'src/Heimdall.App/Assets/milkdown-editor/npm-audit-baseline.json'
$recordedReport = Join-Path $PSScriptRoot 'fixtures/npm-audit-milkdown.json'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-Advisory {
    param([int]$Source, [string]$Name, [string]$Severity, [string]$Ghsa)
    return [ordered]@{
        source = $Source; name = $Name; dependency = $Name; title = "$Name advisory $Ghsa"
        url = "https://github.com/advisories/$Ghsa"; severity = $Severity; range = '<9.9.9'
    }
}

# A report in the layout npm 11 prints: one entry per package, `via` holding advisory
# objects or the names of the packages it depends on, and npm's own per-severity totals.
function New-AuditJson {
    param([System.Collections.IDictionary]$Packages, [System.Collections.IDictionary]$Totals = $null, [int]$Version = 2)
    $vulnerabilities = [ordered]@{}
    $counts = [ordered]@{ info = 0; low = 0; moderate = 0; high = 0; critical = 0 }
    foreach ($name in $Packages.Keys) {
        $via = @($Packages[$name])
        $worst = 'low'
        foreach ($entry in $via) {
            $severity = if ($entry -is [string]) { 'high' } else { $entry.severity }
            if (@('low', 'moderate', 'high', 'critical').IndexOf($severity) -gt @('low', 'moderate', 'high', 'critical').IndexOf($worst)) { $worst = $severity }
        }
        $vulnerabilities[$name] = [ordered]@{ name = $name; severity = $worst; isDirect = $false; via = $via; effects = @(); range = '*'; nodes = @("node_modules/$name"); fixAvailable = $true }
        $counts[$worst]++
    }
    if ($null -ne $Totals) { foreach ($key in $Totals.Keys) { $counts[$key] = $Totals[$key] } }
    $counts['total'] = $Packages.Count
    return (ConvertTo-Json -Depth 8 -InputObject ([ordered]@{
        auditReportVersion = $Version
        vulnerabilities    = $vulnerabilities
        metadata           = [ordered]@{ vulnerabilities = $counts; dependencies = [ordered]@{ prod = 1; total = 1 } }
    }))
}

function New-Baseline {
    param([string[]]$Advisories, [string]$Package = 'dompurify')
    return @($Advisories | ForEach-Object { [pscustomobject]@{ Advisory = $_; Package = $Package; Reason = 'recorded for the test' } })
}

# 1.
$v = Get-NpmAuditVerdict -Output (New-AuditJson -Packages @{})
Assert-True ($v.Verdict -eq 'Clean' -and @($v.Findings).Count -eq 0) "no vulnerability is Clean ($($v.Verdict))"

# 2.
$json = New-AuditJson -Packages @{
    katex    = @((New-Advisory -Source 1 -Name 'katex' -Severity 'low' -Ghsa 'GHSA-aaaa-bbbb-cccc'))
    dompurify = @((New-Advisory -Source 2 -Name 'dompurify' -Severity 'moderate' -Ghsa 'GHSA-dddd-eeee-ffff'))
}
$v = Get-NpmAuditVerdict -Output $json
Assert-True ($v.Verdict -eq 'Warn' -and @($v.Findings).Count -eq 2) "low and moderate advisories only warn ($($v.Verdict))"

# 3. and 4.
$json = New-AuditJson -Packages @{ dompurify = @((New-Advisory -Source 3 -Name 'dompurify' -Severity 'high' -Ghsa 'GHSA-1111-2222-3333')) }
$v = Get-NpmAuditVerdict -Output $json
Assert-True ($v.Verdict -eq 'Fail' -and $v.Findings[0].Package -eq 'dompurify' -and $v.Findings[0].Id -eq 'GHSA-1111-2222-3333' -and -not $v.Findings[0].Accepted) "a high advisory outside the baseline fails, naming package and GHSA ($($v.Verdict))"
$json = New-AuditJson -Packages @{ lodash = @((New-Advisory -Source 4 -Name 'lodash' -Severity 'critical' -Ghsa 'GHSA-4444-5555-6666')) }
$v = Get-NpmAuditVerdict -Output $json
Assert-True ($v.Verdict -eq 'Fail') "a critical advisory fails ($($v.Verdict))"

# 5.
$json = New-AuditJson -Packages @{ dompurify = @((New-Advisory -Source 3 -Name 'dompurify' -Severity 'high' -Ghsa 'GHSA-1111-2222-3333')) }
$v = Get-NpmAuditVerdict -Output $json -Baseline (New-Baseline -Advisories @('GHSA-1111-2222-3333'))
Assert-True ($v.Verdict -eq 'Warn' -and $v.Findings[0].Accepted) "a baselined high advisory warns and is marked accepted ($($v.Verdict))"
$v = Get-NpmAuditVerdict -Output $json -Baseline (New-Baseline -Advisories @('GHSA-1111-2222-3333') -Package 'some-other-package')
Assert-True ($v.Verdict -eq 'Fail') "an advisory accepted for another package does not cover this one ($($v.Verdict))"

# 6.
$json = New-AuditJson -Packages ([ordered]@{
    'server-renderer' = @((New-Advisory -Source 5 -Name 'server-renderer' -Severity 'high' -Ghsa 'GHSA-7777-8888-9999'))
    vue               = @('server-renderer')
})
$v = Get-NpmAuditVerdict -Output $json
Assert-True (@($v.Findings).Count -eq 1 -and $v.Findings[0].Package -eq 'server-renderer') "a package high only through a dependency is not a finding of its own ($(@($v.Findings).Count) finding(s))"

# 7.
$json = New-AuditJson -Packages @{ nanoid = @(
    (New-Advisory -Source 6 -Name 'nanoid' -Severity 'high' -Ghsa 'GHSA-aaaa-1111-bbbb'),
    (New-Advisory -Source 7 -Name 'nanoid' -Severity 'high' -Ghsa 'GHSA-aaaa-1111-bbbb')
) }
$v = Get-NpmAuditVerdict -Output $json
Assert-True (@($v.Findings).Count -eq 1) "one GHSA on two version ranges is one finding ($(@($v.Findings).Count))"

# 8. Baseline file rules.
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('heimdall-npm-audit-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work -Force
try {
    $noReason = Join-Path $work 'no-reason.json'
    [System.IO.File]::WriteAllText($noReason, '{"accepted":[{"advisory":"GHSA-1111-2222-3333","package":"dompurify","reason":"  "}]}')
    $threw = $false
    try { $null = Read-NpmAuditBaseline -Path $noReason } catch { $threw = $true }
    Assert-True $threw 'a baseline entry without a reason is refused'
    $good = Join-Path $work 'good.json'
    [System.IO.File]::WriteAllText($good, '{"accepted":[{"advisory":"GHSA-1111-2222-3333","package":"dompurify","reason":"why"}]}')
    $entries = @(Read-NpmAuditBaseline -Path $good)
    Assert-True ($entries.Count -eq 1 -and $entries[0].Advisory -eq 'GHSA-1111-2222-3333' -and $entries[0].Reason -eq 'why') 'a well-formed baseline is read'
    Assert-True (@(Read-NpmAuditBaseline -Path (Join-Path $work 'absent.json')).Count -eq 0) 'a missing baseline file is an empty baseline'
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# 9.
$v = Get-NpmAuditVerdict -Output (New-AuditJson -Packages @{}) -Baseline (New-Baseline -Advisories @('GHSA-0000-0000-0000'))
Assert-True ($v.Verdict -eq 'Clean' -and @($v.StaleBaseline) -contains 'GHSA-0000-0000-0000') 'a baseline entry the audit no longer reports is flagged as stale'

# 10. A silent audit proves nothing.
$v = Get-NpmAuditVerdict -Output 'npm ERR! network request failed'
Assert-True ($v.Verdict -eq 'ToolFailure') "output that is not JSON is ToolFailure ($($v.Verdict))"
$v = Get-NpmAuditVerdict -Output ''
Assert-True ($v.Verdict -eq 'ToolFailure') 'empty output is ToolFailure'
$v = Get-NpmAuditVerdict -Output '{"error":{"code":"ENOTFOUND","summary":"request to https://registry.npmjs.org failed"}}'
Assert-True ($v.Verdict -eq 'ToolFailure' -and (@($v.Reasons) -join ' ') -match 'ENOTFOUND') "an npm error is ToolFailure ($(@($v.Reasons) -join ' '))"
$v = Get-NpmAuditVerdict -Output (New-AuditJson -Packages @{} -Version 3)
Assert-True ($v.Verdict -eq 'ToolFailure' -and (@($v.Reasons) -join ' ') -match "version '3'") 'a report version this script does not read is ToolFailure'
$v = Get-NpmAuditVerdict -Output (New-AuditJson -Packages @{} -Totals @{ high = 2 })
Assert-True ($v.Verdict -eq 'ToolFailure' -and (@($v.Reasons) -join ' ') -match 'lists no advisory') 'a high count with no advisory behind it is ToolFailure, not Clean'
$v = Get-NpmAuditVerdict -Output '{"auditReportVersion":2}'
Assert-True ($v.Verdict -eq 'ToolFailure') 'a report without its sections is ToolFailure'

# 11. The recorded real report.
Assert-True (Test-Path -LiteralPath $recordedReport) 'the recorded real npm audit report is present'
$recorded = [System.IO.File]::ReadAllText($recordedReport)
$v = Get-NpmAuditVerdict -Output $recorded
$highs = @($v.Findings | Where-Object { $_.Severity -eq 'high' })
Assert-True ($v.Verdict -eq 'Fail' -and $highs.Count -eq 10) "the recorded report, with no baseline, fails on its ten high advisories ($($v.Verdict), $($highs.Count))"
Assert-True (@($v.Findings).Count -eq 30) "and counts thirty distinct advisories ($(@($v.Findings).Count))"
# The baseline is built from the recorded report itself (its ten highs), so the case does not
# depend on what the committed baseline holds: it is empty once the bundle's dependencies are
# patched, and a regression to a non-empty file must not turn this suite red.
$baseline = @($highs | ForEach-Object { [pscustomobject]@{ Advisory = $_.Id; Package = $_.Package; Reason = 'recorded for the test' } })
Assert-True ($baseline.Count -eq 10) "the recorded highs make a ten-entry baseline ($($baseline.Count))"
$v = Get-NpmAuditVerdict -Output $recorded -Baseline $baseline
Assert-True ($v.Verdict -eq 'Warn' -and @($v.StaleBaseline).Count -eq 0) "with that baseline the recorded report warns and no entry is stale ($($v.Verdict))"
$trimmed = @($baseline | Select-Object -Skip 1)
$v = Get-NpmAuditVerdict -Output $recorded -Baseline $trimmed
Assert-True ($v.Verdict -eq 'Fail') 'removing one baseline entry fails the gate again'

# The committed baseline parses (every entry carries a reason), and no entry of it is a
# placeholder: an acceptance must say why this advisory has no patched version in range.
$committed = @(Read-NpmAuditBaseline -Path $baselineFile)
$templated = @($committed | Where-Object { $_.Reason -like '*Already in package-lock.json when the audit gate was introduced*' })
Assert-True ($templated.Count -eq 0) "no committed baseline entry carries the copied template reason ($($templated.Count) of $($committed.Count))"

# 12. One retry.
$script:attempts = 0
$runner = {
    $script:attempts++
    if ($script:attempts -eq 1) { return [pscustomobject]@{ ExitCode = 1; Output = 'ECONNRESET' } }
    return [pscustomobject]@{ ExitCode = 0; Output = (New-AuditJson -Packages @{}) }
}
$scan = Invoke-NpmAuditScan -Runner $runner
Assert-True ($scan.Verdict -eq 'Clean' -and $scan.Attempts -eq 2) "a first attempt that cannot be trusted is retried once ($($scan.Verdict) after $($scan.Attempts))"
$script:attempts = 0
$always = { $script:attempts++; return [pscustomobject]@{ ExitCode = 1; Output = 'ECONNRESET' } }
$scan = Invoke-NpmAuditScan -Runner $always
Assert-True ($scan.Verdict -eq 'ToolFailure' -and $scan.Attempts -eq $script:NpmAuditAttempts) "and given up on after $($script:NpmAuditAttempts) attempts ($($scan.Attempts))"

# 13. Wiring.
$ci = Get-Content -LiteralPath (Join-Path $repoRoot '.github/workflows/ci.yml') -Raw
Assert-True ($ci -match 'Invoke-NpmAuditCheck\.ps1') 'ci.yml runs the npm audit check'
Assert-True ($ci -match 'Test-NpmAuditVerdict\.ps1') 'ci.yml runs this suite'
$runner = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Invoke-NpmAuditCheck.ps1') -Raw
Assert-True ($runner -match '--omit=dev' -and $runner -match '--package-lock-only') 'the check audits production dependencies from the lock alone, with no install'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the npm audit informs below high, fails at high or critical, and never reads silence as clean.' -ForegroundColor Green
exit 0
