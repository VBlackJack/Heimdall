<#
.SYNOPSIS
    Tests the release preflight (scripts/ReleasePreflight.ps1) on recorded git and gh
    outputs, and its wiring into Build.ps1.

.DESCRIPTION
    v2026.102702 was published 28 seconds after its push, from a tree Build.ps1 had
    checked only for a pending change to the application project. Every check below is
    proven by a fixture that breaks exactly that one check while the others hold, so a
    check that stopped looking would turn its case red:

      1. Everything right                         -> Passed, HeadSha reported.
      2. One untracked file                       -> refused, the entry is named.
      3. On a feature branch                      -> refused.
      4. HEAD ahead of origin/master              -> refused.
      5. git fetch fails                          -> refused.
      6. No push run for the SHA, or only a run   -> refused, state NoRun.
         of another SHA (the pull request run)
      7. CI run in progress                       -> refused, state InProgress;
                                                     with -WaitForCi polled at
                                                     the interval, bounded.
      8. CI run completed but failed              -> refused, state Failed.
      9. gh fails, or prints something not JSON   -> refused.
     10. Two faults at once                       -> both reported.
     11. The CI query names the HEAD SHA and      -> the run checked is this commit's.
         ci.yml
     12. Get-ReleaseCreateArgs pins --target      -> the tag is the release commit;
                                                     an empty SHA throws.
     13. Build.ps1 runs the preflight before the  -> the checks are wired in, fatal
         version bump, fatal under -Publish,         only on the real publish path,
         and creates the release through            and the release is pinned.
         Get-ReleaseCreateArgs with $releaseSha
     14. Resolve-ReleaseGateOutcome              -> Pass, Warn, Abort; the
                                                     one override needs a reason,
                                                     and the notes record it.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-ReleasePreflight.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'ReleasePreflight.ps1')

$failures = 0
$sha = '0123456789abcdef0123456789abcdef01234567'
$otherSha = 'fedcba9876543210fedcba9876543210fedcba98'

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

function New-RunJson {
    param([string]$Status, [string]$Conclusion, [string]$HeadSha = $sha)
    return ('[{{"conclusion":"{0}","headSha":"{1}","status":"{2}","url":"https://github.com/VBlackJack/Heimdall/actions/runs/1"}}]' -f $Conclusion, $HeadSha, $Status)
}

# A recorded world: every reply the preflight can ask for, healthy by default.
function New-World {
    return @{
        Status    = (New-Reply -Lines @())
        Branch    = (New-Reply -Lines @('master'))
        Fetch     = (New-Reply -Lines @('From github.com:VBlackJack/Heimdall'))
        Head      = (New-Reply -Lines @($sha))
        RemoteRef = (New-Reply -Lines @($sha))
        Runs      = (New-Reply -Lines @((New-RunJson -Status 'completed' -Conclusion 'success')))
        GhCalls   = (New-Object System.Collections.Generic.List[string])
        Waits     = (New-Object System.Collections.Generic.List[int])
    }
}

function Invoke-WithWorld {
    param([hashtable]$World, [switch]$WaitForCi)
    $git = {
        param([string[]]$GitArgs)
        $joined = $GitArgs -join ' '
        switch -Regex ($joined) {
            '^status --porcelain$' { return $World.Status }
            '^rev-parse --abbrev-ref HEAD$' { return $World.Branch }
            '^fetch origin master$' { return $World.Fetch }
            '^rev-parse HEAD$' { return $World.Head }
            '^rev-parse refs/remotes/origin/master$' { return $World.RemoteRef }
            default { throw "unexpected git call: $joined" }
        }
    }.GetNewClosure()
    $gh = {
        param([string[]]$GhArgs)
        $World.GhCalls.Add(($GhArgs -join ' '))
        # A list of replies is played in order, the last one repeating: a run that moves on.
        $replies = @($World.Runs)
        $index = [Math]::Min($World.GhCalls.Count, $replies.Count) - 1
        return $replies[$index]
    }.GetNewClosure()
    $wait = {
        param([int]$Seconds)
        $World.Waits.Add($Seconds)
    }.GetNewClosure()
    return (Invoke-ReleasePreflight -Git $git -Gh $gh -Wait $wait -WaitForCi:$WaitForCi -PollIntervalSeconds 30 -MaxWaitSeconds 90)
}

function Get-FailureText {
    param($Result)
    return (@($Result.Failures) -join ' | ')
}

# 1.
$world = New-World
$r = Invoke-WithWorld -World $world
Assert-True ($r.Passed -and @($r.Failures).Count -eq 0) "a clean master in step with origin and green CI passes ($(Get-FailureText $r))"
Assert-True ($r.HeadSha -eq $sha) 'the preflight reports the HEAD SHA it verified'

# 11.
Assert-True ($world.GhCalls.Count -eq 1) "CI is asked once ($($world.GhCalls.Count))"
$ghCall = if ($world.GhCalls.Count -gt 0) { $world.GhCalls[0] } else { '' }
Assert-True ($ghCall -match "--commit $sha" -and $ghCall -match '--workflow ci\.yml' -and $ghCall -match '--event push' -and $ghCall -match '--json status,conclusion,headSha') "the CI query targets this commit's ci.yml push run ($ghCall)"
Assert-True ($world.Waits.Count -eq 0) 'a finished run is not waited for'
# 2.
$world = New-World; $world.Status = New-Reply -Lines @('?? stray.txt')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match 'not clean.*stray\.txt') "an untracked file refuses the release ($(Get-FailureText $r))"
$world = New-World; $world.Status = New-Reply -ExitCode 128 -Lines @('fatal: not a git repository')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match 'git status failed') 'a failing git status refuses the release'

# 3.
$world = New-World; $world.Branch = New-Reply -Lines @('fix/installer-release-gates')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match "'fix/installer-release-gates', not 'master'") "a feature branch refuses the release ($(Get-FailureText $r))"

# 4.
$world = New-World; $world.RemoteRef = New-Reply -Lines @($otherSha)
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match "is not origin/master $otherSha") "HEAD apart from origin/master refuses the release ($(Get-FailureText $r))"

# 5.
$world = New-World; $world.Fetch = New-Reply -ExitCode 128 -Lines @('fatal: unable to access')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match 'git fetch origin master failed') "a failed fetch refuses the release ($(Get-FailureText $r))"

# 6. No run, reported as such.
$world = New-World; $world.Runs = New-Reply -Lines @('[]')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and $r.CiState -eq 'NoRun' -and (Get-FailureText $r) -match 'No ci\.yml push run found') "no CI run refuses the release ($($r.CiState): $(Get-FailureText $r))"
# The pull request run of the same change carries another SHA: it does not count.
$world = New-World; $world.Runs = New-Reply -Lines @((New-RunJson -Status 'completed' -Conclusion 'success' -HeadSha $otherSha))
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and $r.CiState -eq 'NoRun') "a green run of another SHA does not vouch for HEAD ($($r.CiState))"

# 7. In progress, reported as such; waited for only when asked, and only so long.
$world = New-World; $world.Runs = New-Reply -Lines @((New-RunJson -Status 'in_progress' -Conclusion ''))
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and $r.CiState -eq 'InProgress' -and (Get-FailureText $r) -match "still 'in_progress'") "a CI run in progress refuses the release ($($r.CiState): $(Get-FailureText $r))"
Assert-True ($world.Waits.Count -eq 0) 'without -WaitForCi a run in progress is not waited for'

$world = New-World
$world.Runs = @(
    (New-Reply -Lines @((New-RunJson -Status 'queued' -Conclusion ''))),
    (New-Reply -Lines @((New-RunJson -Status 'in_progress' -Conclusion ''))),
    (New-Reply -Lines @((New-RunJson -Status 'completed' -Conclusion 'success')))
)
$r = Invoke-WithWorld -World $world -WaitForCi
Assert-True ($r.Passed -and $r.CiState -eq 'Success') "a run that finishes green while waited for passes ($($r.CiState): $(Get-FailureText $r))"
Assert-True ($world.Waits.Count -eq 2 -and @($world.Waits | Where-Object { $_ -ne 30 }).Count -eq 0) "it waited twice, one poll interval each ($(@($world.Waits) -join ','))"

$world = New-World; $world.Runs = New-Reply -Lines @((New-RunJson -Status 'in_progress' -Conclusion ''))
$r = Invoke-WithWorld -World $world -WaitForCi
Assert-True (-not $r.Passed -and $r.CiState -eq 'InProgress' -and (Get-FailureText $r) -match 'Still running after 90 s') "a run that never ends is given up on ($(Get-FailureText $r))"
Assert-True ($world.Waits.Count -eq 3) "the wait is bounded: 90 s at 30 s is three polls ($($world.Waits.Count))"
Assert-True ($script:CiPollIntervalSeconds -gt 0 -and $script:CiMaxWaitSeconds -ge $script:CiPollIntervalSeconds) "the default poll is bounded ($script:CiPollIntervalSeconds s, at most $script:CiMaxWaitSeconds s)"

# 8. Failed, reported as such.
$world = New-World; $world.Runs = New-Reply -Lines @((New-RunJson -Status 'completed' -Conclusion 'failure'))
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and $r.CiState -eq 'Failed' -and (Get-FailureText $r) -match "concluded 'failure'") "a red CI run refuses the release ($($r.CiState): $(Get-FailureText $r))"
$world = New-World; $world.Runs = New-Reply -Lines @('[{"conclusion":"cancelled","headSha":"' + $sha + '","status":"completed","url":"u"},{"conclusion":"success","headSha":"' + $sha + '","status":"completed","url":"u"}]')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match "concluded 'cancelled'") 'the latest run decides, not an older green one'

# 9.
$world = New-World; $world.Runs = New-Reply -ExitCode 1 -Lines @('To get started with GitHub CLI, please run:  gh auth login')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and $r.CiState -eq 'QueryFailed' -and (Get-FailureText $r) -match 'gh run list failed') "a failing gh refuses the release ($(Get-FailureText $r))"
$world = New-World; $world.Runs = New-Reply -Lines @('no runs, have a nice day')
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed -and (Get-FailureText $r) -match 'not JSON') "output that is not JSON refuses the release ($(Get-FailureText $r))"
$world = New-World; $world.Runs = New-Reply -Lines @()
$r = Invoke-WithWorld -World $world
Assert-True (-not $r.Passed) 'an empty gh output refuses the release'
# 10.
$world = New-World; $world.Status = New-Reply -Lines @(' M Build.ps1'); $world.Branch = New-Reply -Lines @('develop')
$r = Invoke-WithWorld -World $world
Assert-True (@($r.Failures).Count -eq 2) "two faults are both reported ($(@($r.Failures).Count): $(Get-FailureText $r))"

# 12.
$createArgs = @(Get-ReleaseCreateArgs -BuildNumber '2026.102801' -Artifacts @('a.exe', 'b.zip') -NotesFile 'notes.md' -TargetSha $sha)
$targetIndex = [Array]::IndexOf($createArgs, '--target')
Assert-True ($targetIndex -ge 0 -and $createArgs[$targetIndex + 1] -eq $sha) "gh release create is pinned to the release commit ($($createArgs -join ' '))"
Assert-True (($createArgs[0..2] -join ' ') -eq 'release create v2026.102801' -and $createArgs -contains 'a.exe' -and $createArgs -contains 'b.zip') 'the tag and the artifacts are still passed'
$threw = $false
try { $null = Get-ReleaseCreateArgs -BuildNumber '2026.102801' -Artifacts @() -NotesFile 'n.md' -TargetSha ' ' } catch { $threw = $true }
Assert-True $threw 'an empty target SHA throws instead of creating an unpinned release'

# 14. What the findings do to the run, and the one override.
$found = @('The current branch is ''develop'', not ''master''.')
$o = Resolve-ReleaseGateOutcome -Failures @() -Fatal $true
Assert-True ($o.Decision -eq 'Pass') "no finding passes ($($o.Decision))"
$o = Resolve-ReleaseGateOutcome -Failures $found -Fatal $true
Assert-True ($o.Decision -eq 'Abort' -and (@($o.Messages) -join ' ') -match 'AcceptCiFailure') "a finding aborts a publish and names the override ($($o.Decision))"
$o = Resolve-ReleaseGateOutcome -Failures $found -Fatal $false
Assert-True ($o.Decision -eq 'Warn') "a finding only warns a dry run ($($o.Decision))"
$o = Resolve-ReleaseGateOutcome -Failures $found -Fatal $true -AcceptCiFailure 'hotfix for a crash on start, CI runner outage'
Assert-True ($o.Decision -eq 'Overridden') "a reason lets the publish continue ($($o.Decision))"
foreach ($blank in @('', '   ')) {
    $o = Resolve-ReleaseGateOutcome -Failures $found -Fatal $true -AcceptCiFailure $blank
    Assert-True ($o.Decision -eq 'Abort') "an override without a reason ('$blank') is refused ($($o.Decision))"
}
$section = Get-ReleaseGateOverrideNotes -Reason 'CI runner outage' -Failures $found
Assert-True ($section -match '### Release gate override' -and $section -match 'Reason given: CI runner outage' -and $section -match "- The current branch is 'develop'") 'the override and what it overrode go into the release notes'

# 13. Wiring into Build.ps1.
$buildScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1'
$buildText = Get-Content -LiteralPath $buildScript -Raw
$buildLines = @(Get-Content -LiteralPath $buildScript)
Assert-True ($buildText -match 'ReleasePreflight\.ps1') 'Build.ps1 dot-sources the preflight'
$preflightIndex = $buildText.IndexOf('= Invoke-ReleasePreflight')
$bumpIndex = $buildText.IndexOf('Update-AppProjectVersion -Path')
Assert-True ($preflightIndex -ge 0 -and $bumpIndex -gt $preflightIndex) 'the preflight runs before the version bump'
Assert-True ($buildText -match '\$releasePreflightFatal\s*=\s*\$Publish\.IsPresent\s+-and\s+-not\s+\$DryRun\.IsPresent') 'the preflight is fatal under -Publish and only there'
Assert-True ($buildText -match 'Invoke-ReleasePreflight -WaitForCi:\$releasePreflightFatal') 'only a real publish waits for CI'
Assert-True ($buildText -match 'Resolve-ReleaseGateOutcome @gateArgs') 'Build.ps1 decides the findings through Resolve-ReleaseGateOutcome'
Assert-True ($buildText -match '\$notes \+= Get-ReleaseGateOverrideNotes -Reason \$releaseGateOverride\.Reason') 'an override is appended to the release notes'
$overrideIndex = $buildText.IndexOf('$notes += Get-ReleaseGateOverrideNotes')
$notesFileIndex = $buildText.IndexOf('WriteAllText($notesFile')
Assert-True ($overrideIndex -ge 0 -and $notesFileIndex -gt $overrideIndex) 'before the notes file is written'
$switches = @([regex]::Matches($buildText, '\[switch\]\$(\w+)') | ForEach-Object { $_.Groups[1].Value })
# The switches Build.ps1 had before the release gates, none of which overrides one. A new
# switch fails here until it is added on purpose: a second gate override is the regression.
$knownSwitches = @('SkipTests', 'Publish', 'DryRun', 'AllowNonAsciiNotes', 'AllowAutoNotesOnly')
$newSwitches = @($switches | Where-Object { $knownSwitches -notcontains $_ })
Assert-True ($newSwitches.Count -eq 0) "no release-gate override switch besides -AcceptCiFailure (new: $($newSwitches -join ', '))"
Assert-True ($buildText -match '\[string\]\$AcceptCiFailure') 'the override is -AcceptCiFailure, a reason'
Assert-True (-not ($buildText -match "'release',\s*'create'")) 'Build.ps1 no longer assembles gh release create arguments by hand'
$pinnedCreate = @($buildLines | Where-Object { $_ -match '-TargetSha \$releaseSha' })
Assert-True ($pinnedCreate.Count -eq 1) "the real release is created pinned to `$releaseSha ($($pinnedCreate.Count) site(s))"
Assert-True ($buildText -match '\$releaseShaOut\s*=\s*&\s*git rev-parse HEAD') 'the release SHA is read from HEAD after the commit'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: a release ships only a clean master that origin and CI agree on, and its tag is pinned.' -ForegroundColor Green
exit 0