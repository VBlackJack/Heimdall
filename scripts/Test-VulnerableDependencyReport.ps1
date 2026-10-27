<#
.SYNOPSIS
    Tests the vulnerable-dependency verdict (scripts/VulnerableDependencyReport.ps1) and
    the CI step script around it (scripts/Invoke-VulnerableDependencyCheck.ps1).

.DESCRIPTION
    The CI step printed "No vulnerable dependencies detected." whenever the scan's text
    lacked the phrase of a finding, which is also what a failed scan prints. The fixtures
    below are the JSON shapes `dotnet list package --vulnerable --format json` writes,
    recorded on 2026-10-27 (the vulnerable one from a project referencing
    Newtonsoft.Json 12.0.1, the failed one from a project referencing a package that
    does not exist).

      1. Every project reported, nothing vulnerable          -> Clean.
      2. A real advisory on a top-level package              -> Vulnerable, finding named.
      3. A transitive advisory                               -> Vulnerable too.
      4. Exit code 1 with the failed-restore report          -> ToolFailure.
      5. Exit code 0 but a problem in the report             -> ToolFailure.
      6. A project of the solution missing from the report   -> ToolFailure, named.
      7. Output that is not the report (localized text,      -> ToolFailure.
         empty output)
      8. A solution with no project                          -> ToolFailure.
      9. Get-SolutionProjectNames reads Heimdall.slnx        -> the expected list is the
                                                                real one.
     10. The step script on a solution path that does not    -> exits non-zero. This is
         exist (when dotnet is on PATH)                         the case the old step
                                                                reported as clean.
     11. ci.yml runs the step script and no longer greps     -> the wiring.
         the console text
     12. A non-zero exit is retried once, then fails         -> one retry, never two.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-VulnerableDependencyReport.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'VulnerableDependencyReport.ps1')

$failures = 0
$repoRoot = Split-Path -Parent $PSScriptRoot

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

$expected = @('Alpha', 'Beta')

$clean = @'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [
    { "path": "C:/repo/src/Alpha/Alpha.csproj" },
    { "path": "C:/repo/src/Beta/Beta.csproj" }
  ]
}
'@

$vulnerable = @'
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [ "https://api.nuget.org/v3/index.json" ],
  "projects": [
    { "path": "C:/repo/src/Alpha/Alpha.csproj" },
    {
      "path": "C:/repo/src/Beta/Beta.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "topLevelPackages": [
            {
              "id": "Newtonsoft.Json",
              "requestedVersion": "12.0.1",
              "resolvedVersion": "12.0.1",
              "vulnerabilities": [
                { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-5crp-9r3c-p9vr" }
              ]
            }
          ]
        }
      ]
    }
  ]
}
'@

$transitive = @'
{
  "version": 1,
  "projects": [
    { "path": "C:/repo/src/Alpha/Alpha.csproj" },
    {
      "path": "C:/repo/src/Beta/Beta.csproj",
      "frameworks": [
        {
          "framework": "net10.0",
          "transitivePackages": [
            {
              "id": "System.Text.RegularExpressions",
              "resolvedVersion": "4.3.0",
              "vulnerabilities": [
                { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-cmhx-cq75-c4mj" }
              ]
            }
          ]
        }
      ]
    }
  ]
}
'@

$failedRestore = @'
{
   "version": 1,
   "problems": [
      { "text": "Restore failed. Run dotnet restore for details.", "level": "error" }
   ]
}
'@

$problemWithProjects = @'
{
  "version": 1,
  "problems": [ { "text": "Unable to load the service index for source.", "level": "error" } ],
  "projects": [
    { "path": "C:/repo/src/Alpha/Alpha.csproj" },
    { "path": "C:/repo/src/Beta/Beta.csproj" }
  ]
}
'@

$missingBeta = @'
{
  "version": 1,
  "projects": [ { "path": "C:/repo/src/Alpha/Alpha.csproj" } ]
}
'@

# 1.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $clean -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'Clean') "every project reported and nothing found is Clean ($($r.Verdict): $($r.Reasons -join '; '))"

# 2.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $vulnerable -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'Vulnerable') "a top-level advisory is Vulnerable ($($r.Verdict))"
Assert-True ((@($r.Findings) -join ' ') -match 'Beta.*Newtonsoft\.Json 12\.0\.1 - High') "the finding names project, package, version and severity ($(@($r.Findings) -join '; '))"

# 3.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $transitive -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'Vulnerable' -and @($r.Findings).Count -eq 1) "a transitive advisory is Vulnerable ($($r.Verdict))"

# 4.
$r = Get-VulnerableDependencyVerdict -ExitCode 1 -Output $failedRestore -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure' -and (@($r.Reasons) -join ' ') -match 'exited with code 1') "a non-zero exit code is ToolFailure ($($r.Verdict))"
$r = Get-VulnerableDependencyVerdict -ExitCode 1 -Output $clean -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure') 'a non-zero exit code wins over a report that looks clean'

# 5.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $problemWithProjects -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure' -and (@($r.Reasons) -join ' ') -match 'service index') "a reported problem is ToolFailure even with exit code 0 ($($r.Verdict))"
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $failedRestore -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure') 'a report with problems and no project list is ToolFailure'

# 6.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $missingBeta -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure' -and (@($r.Reasons) -join ' ') -match 'Beta is in the solution but absent') "a missing project is ToolFailure and named ($($r.Reasons -join '; '))"

# 7.
$localized = "Le projet specifie 'Alpha' n'a aucun package vulnerable compte tenu des sources actuelles."
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $localized -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure') "console text instead of the report is ToolFailure ($($r.Verdict))"
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output '' -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure') "empty output is ToolFailure ($($r.Verdict))"

# 8.
$r = Get-VulnerableDependencyVerdict -ExitCode 0 -Output $clean -ExpectedProjects @()
Assert-True ($r.Verdict -eq 'ToolFailure') "an empty project list is ToolFailure ($($r.Verdict))"

# 9.
$names = @(Get-SolutionProjectNames -SolutionPath (Join-Path $repoRoot 'Heimdall.slnx'))
Assert-True ($names.Count -ge 10 -and $names -contains 'Heimdall.App' -and $names -contains 'Heimdall.Core.Tests') "Heimdall.slnx yields its projects ($($names.Count): $($names -join ', '))"

# 12. Retry once on a non-zero exit, then fail.
function New-ScriptedRunner {
    param([object[]]$Replies)
    $state = @{ Calls = 0; Replies = $Replies }
    $runner = {
        $state.Calls++
        return $state.Replies[[Math]::Min($state.Calls, $state.Replies.Count) - 1]
    }.GetNewClosure()
    return @{ State = $state; Runner = $runner }
}
$fail = [pscustomobject]@{ ExitCode = 1; Output = $failedRestore }
$ok = [pscustomobject]@{ ExitCode = 0; Output = $clean }

$scripted = New-ScriptedRunner -Replies @($ok)
$r = Invoke-VulnerableDependencyScan -Runner $scripted.Runner -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'Clean' -and $scripted.State.Calls -eq 1) "a scan that succeeds is run once ($($scripted.State.Calls) call(s), $($r.Verdict))"

$scripted = New-ScriptedRunner -Replies @($fail, $ok)
$r = Invoke-VulnerableDependencyScan -Runner $scripted.Runner -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'Clean' -and $scripted.State.Calls -eq 2 -and $r.Attempts -eq 2) "a scan that fails once is retried and its second result counts ($($scripted.State.Calls) call(s), $($r.Verdict))"

$scripted = New-ScriptedRunner -Replies @($fail, $fail, $ok)
$r = Invoke-VulnerableDependencyScan -Runner $scripted.Runner -ExpectedProjects $expected
Assert-True ($r.Verdict -eq 'ToolFailure' -and $scripted.State.Calls -eq 2) "a scan that fails twice is not tried a third time and fails ($($scripted.State.Calls) call(s), $($r.Verdict))"

# 10. The positive control: the case the old step called clean.
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $bogus = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-no-such-solution-" + [Guid]::NewGuid().ToString('N') + '.slnx')
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-VulnerableDependencyCheck.ps1') -Solution $bogus 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    $text = @($out) -join "`n"
    Assert-True ($code -ne 0) "the step fails on a solution that does not exist (exit $code)"
    Assert-True (-not ($text -match 'No vulnerable dependencies detected')) 'and does not claim a clean scan'
    # The step's own verdict, not a crash: a script that failed to parse exits 1 too.
    Assert-True ($text -match 'did not run to completion') 'the failure is the verdict of the step, not a crash of the script'
    Assert-True ($text -match 'attempt 1 of 2') 'the failed scan was retried before failing'
    # And the real solution goes through the same script to a verdict.
    $out = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-VulnerableDependencyCheck.ps1') 2>&1
    $code = $LASTEXITCODE
    $text = @($out) -join "`n"
    Assert-True ($code -eq 0 -and ($text -match 'No vulnerable dependencies detected across \d+ projects' -or $text -match '::warning::Vulnerable')) "the real solution reaches a verdict (exit $code)"
} else {
    Write-Host 'SKIP: dotnet not on PATH; the end-to-end case needs it' -ForegroundColor Yellow
}

# 11. Wiring.
$ci = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\ci.yml') -Raw
Assert-True ($ci -match 'scripts/Invoke-VulnerableDependencyCheck\.ps1') 'ci.yml runs the step script'
Assert-True (-not ($ci -match 'has the following vulnerable packages')) 'ci.yml no longer greps the console text'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: a scan that did not happen can no longer report itself clean.' -ForegroundColor Green
exit 0
