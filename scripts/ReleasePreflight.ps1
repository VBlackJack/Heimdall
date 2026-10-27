<#
.SYNOPSIS
    Checks, before a Release run bumps the version, that the tree it is about to ship is
    the one CI verified; decides what the release gates' findings do to the run; builds
    the `gh release create` arguments, pinned to the release commit.

.DESCRIPTION
    Dot-source this file to get Invoke-ReleasePreflight, Resolve-ReleaseGateOutcome,
    Get-ReleaseGateOverrideNotes, Get-ReleaseCreateArgs and Invoke-NativeCapture.

    Build.ps1 used to check only that the application project had no pending change. A
    release could therefore be built from a feature branch, from a master behind or ahead
    of origin, from uncommitted edits anywhere else in the tree, or from a commit CI had
    not finished with: v2026.102702 was published 28 seconds after its push. And
    `gh release create` took no --target, so the tag pointed at whatever the default
    branch was when GitHub processed the call.

    The preflight runs four checks and reports every failure, not only the first:
      1. the whole working tree is clean (`git status --porcelain` is empty);
      2. the current branch is master;
      3. after `git fetch origin master`, HEAD is origin/master;
      4. the latest ci.yml run of the push event whose headSha is HEAD completed with
         conclusion success. The pull request run of the same change carries another
         SHA (GitHub's merge commit), so only the push run of master vouches for HEAD.
         "No run", "in progress" and "failed" are reported separately; with -WaitForCi
         a run in progress is polled every CiPollIntervalSeconds for at most
         CiMaxWaitSeconds.

    Build.ps1 makes a failure fatal under -Publish and prints it as a warning under
    -DryRun or a plain Release build. The one override of the whole release gate set,
    this preflight and the WebView2 runtime age alike, is -AcceptCiFailure "<reason>":
    the publish continues and the reason, with the findings it overrode, goes into the
    generated release notes. There is no other override.

    Git, gh and the wait are reached through injectable script blocks so the suite can
    feed them recorded outputs. Git and gh receive an argument array and return an
    object with ExitCode and Lines; the wait receives a number of seconds.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

$script:ReleaseBranch = 'master'
$script:ReleaseRemote = 'origin'
$script:ReleaseWorkflow = 'ci.yml'
$script:ReleaseCiEvent = 'push'
# How many dirty entries a failure message lists before it summarises the rest.
$script:ReleasePreflightListedEntries = 10
# A CI run of this repository takes about 25 minutes; poll every 30 s for at most 40 min.
$script:CiPollIntervalSeconds = 30
$script:CiMaxWaitSeconds = 2400

function Invoke-NativeCapture {
    <#
    .SYNOPSIS
        Runs a native command with stderr folded in, never throwing on stderr output.
    #>
    param(
        [Parameter(Mandatory = $true)] [string]$FilePath,
        [string[]]$ArgumentList = @()
    )

    $prevEAP = $ErrorActionPreference
    $prevPrompt = $env:GIT_TERMINAL_PROMPT
    $ErrorActionPreference = 'Continue'
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        $lines = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    } catch {
        $lines = @("$_")
        $exitCode = -1
    } finally {
        $ErrorActionPreference = $prevEAP
        if ($null -eq $prevPrompt) {
            Remove-Item Env:\GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
        } else {
            $env:GIT_TERMINAL_PROMPT = $prevPrompt
        }
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Lines = $lines }
}

function Get-CiRunState {
    <#
    .SYNOPSIS
        Queries the latest push run of the workflow for one SHA and names its state:
        Success, InProgress, NoRun, Failed or QueryFailed, with a message.
    #>
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Gh,
        [Parameter(Mandatory = $true)] [string]$Sha,
        [Parameter(Mandatory = $true)] [string]$Workflow,
        [Parameter(Mandatory = $true)] [string]$EventName,
        [Parameter(Mandatory = $true)] [scriptblock]$Describe
    )

    $state = {
        param($Name, $Message)
        return [pscustomobject]@{ State = $Name; Message = $Message }
    }

    $runs = & $Gh @('run', 'list', '--workflow', $Workflow, '--event', $EventName, '--commit', $Sha,
        '--limit', '1', '--json', 'status,conclusion,headSha,url')
    if ($runs.ExitCode -ne 0) {
        return (& $state 'QueryFailed' "gh run list failed ($(& $Describe $runs)).")
    }

    $text = ((@($runs.Lines)) -join "`n").Trim()
    $parsed = $null
    $isJson = -not [string]::IsNullOrWhiteSpace($text)
    if ($isJson) {
        try { $parsed = ConvertFrom-Json -InputObject $text } catch { $isJson = $false }
    }
    if (-not $isJson) {
        return (& $state 'QueryFailed' "gh run list returned output that is not JSON ($(& $Describe $runs)).")
    }

    # PowerShell 7 enumerates a JSON array, 5.1 returns it whole; piping flattens both,
    # and an empty array becomes an empty list either way. A run of another commit is
    # not this commit's run, whatever the filter was supposed to return.
    $matching = @($parsed | Where-Object { $null -ne $_ -and [string]$_.headSha -eq $Sha })
    if ($matching.Count -eq 0) {
        return (& $state 'NoRun' "No $Workflow $EventName run found for $Sha. Push master and wait for CI to start, then retry.")
    }

    $latest = $matching[0]
    if ([string]$latest.status -ne 'completed') {
        return (& $state 'InProgress' "The $Workflow $EventName run for $Sha is still '$($latest.status)': $($latest.url)")
    }
    if ([string]$latest.conclusion -ne 'success') {
        return (& $state 'Failed' "The $Workflow $EventName run for $Sha concluded '$($latest.conclusion)', not success: $($latest.url)")
    }
    return (& $state 'Success' "CI green for $Sha")
}

function Invoke-ReleasePreflight {
    <#
    .SYNOPSIS
        Runs the release preflight.
    .OUTPUTS
        An object with Passed (bool), HeadSha (or $null), CiState and Failures (messages).
    #>
    [CmdletBinding()]
    param(
        [scriptblock]$Git = { param([string[]]$GitArgs) Invoke-NativeCapture -FilePath 'git' -ArgumentList $GitArgs },
        [scriptblock]$Gh = { param([string[]]$GhArgs) Invoke-NativeCapture -FilePath 'gh' -ArgumentList $GhArgs },
        [scriptblock]$Wait = { param([int]$Seconds) Start-Sleep -Seconds $Seconds },
        [switch]$WaitForCi,
        [int]$PollIntervalSeconds = $script:CiPollIntervalSeconds,
        [int]$MaxWaitSeconds = $script:CiMaxWaitSeconds,
        [string]$Branch = $script:ReleaseBranch,
        [string]$Remote = $script:ReleaseRemote,
        [string]$Workflow = $script:ReleaseWorkflow,
        [string]$EventName = $script:ReleaseCiEvent
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $describe = {
        param($Result)
        $text = (@($Result.Lines) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' / '
        if ([string]::IsNullOrWhiteSpace($text)) { "exit code $($Result.ExitCode)" } else { "exit code $($Result.ExitCode): $text" }
    }

    # 1. Whole tree clean.
    $status = & $Git @('status', '--porcelain')
    if ($status.ExitCode -ne 0) {
        $failures.Add("git status failed ($(& $describe $status)).")
    } else {
        $dirty = @(@($status.Lines) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($dirty.Count -gt 0) {
            $listed = @($dirty | Select-Object -First $script:ReleasePreflightListedEntries) -join '; '
            $more = if ($dirty.Count -gt $script:ReleasePreflightListedEntries) { " and $($dirty.Count - $script:ReleasePreflightListedEntries) more" } else { '' }
            $failures.Add("The working tree is not clean ($($dirty.Count) entries: $listed$more). A release ships what was committed and verified, nothing else.")
        }
    }

    # 2. On the release branch.
    $branchResult = & $Git @('rev-parse', '--abbrev-ref', 'HEAD')
    $currentBranch = (@($branchResult.Lines) | Select-Object -First 1)
    if ($branchResult.ExitCode -ne 0) {
        $failures.Add("Cannot read the current branch ($(& $describe $branchResult)).")
    } elseif ([string]$currentBranch -ne $Branch) {
        $failures.Add("The current branch is '$currentBranch', not '$Branch'.")
    }

    # 3. In step with the remote, fetched first so the comparison is with origin as it is now.
    $headSha = $null
    $fetch = & $Git @('fetch', $Remote, $Branch)
    if ($fetch.ExitCode -ne 0) {
        $failures.Add("git fetch $Remote $Branch failed ($(& $describe $fetch)).")
    }
    $head = & $Git @('rev-parse', 'HEAD')
    if ($head.ExitCode -ne 0) {
        $failures.Add("Cannot read HEAD ($(& $describe $head)).")
    } else {
        $headSha = ([string](@($head.Lines) | Select-Object -First 1)).Trim()
    }
    if ($fetch.ExitCode -eq 0) {
        $remoteRef = "refs/remotes/$Remote/$Branch"
        $remoteHead = & $Git @('rev-parse', $remoteRef)
        if ($remoteHead.ExitCode -ne 0) {
            $failures.Add("Cannot read $remoteRef ($(& $describe $remoteHead)).")
        } elseif ($null -ne $headSha) {
            $remoteSha = ([string](@($remoteHead.Lines) | Select-Object -First 1)).Trim()
            if ($remoteSha -ne $headSha) {
                $failures.Add("HEAD $headSha is not $Remote/$Branch $remoteSha. Push or pull until they match.")
            }
        }
    }

    # 4. CI green on that exact commit, waiting a bounded time for a run in progress.
    $ciState = 'NotChecked'
    if ($null -ne $headSha) {
        $ci = Get-CiRunState -Gh $Gh -Sha $headSha -Workflow $Workflow -EventName $EventName -Describe $describe
        $waited = 0
        while ($WaitForCi -and $ci.State -eq 'InProgress' -and $waited -lt $MaxWaitSeconds) {
            Write-Host "[preflight] $($ci.Message); waiting ($waited/$MaxWaitSeconds s)" -ForegroundColor DarkGray
            & $Wait $PollIntervalSeconds
            $waited += $PollIntervalSeconds
            $ci = Get-CiRunState -Gh $Gh -Sha $headSha -Workflow $Workflow -EventName $EventName -Describe $describe
        }
        $ciState = $ci.State
        if ($ci.State -eq 'InProgress' -and $WaitForCi) {
            $failures.Add("$($ci.Message) Still running after $MaxWaitSeconds s.")
        } elseif ($ci.State -ne 'Success') {
            $failures.Add($ci.Message)
        }
    }

    return [pscustomobject]@{
        Passed   = ($failures.Count -eq 0)
        HeadSha  = $headSha
        CiState  = $ciState
        Failures = $failures.ToArray()
    }
}

function Resolve-ReleaseGateOutcome {
    <#
    .SYNOPSIS
        Decides what the release gates' findings do to the run.
    .OUTPUTS
        An object with Decision (Pass, Warn, Abort or Overridden) and Messages.
        Pass: no finding. Warn: findings on a run that does not publish. Abort: findings
        on a publish, or an override given without a reason. Overridden: findings on any
        run, accepted by -AcceptCiFailure with a reason that goes into the release notes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Failures,
        [Parameter(Mandatory = $true)] [bool]$Fatal,
        # $null when the override was not passed at all.
        [AllowNull()] [AllowEmptyString()] [string]$AcceptCiFailure
    )

    $outcome = {
        param($Decision, [string[]]$Messages)
        return [pscustomobject]@{ Decision = $Decision; Messages = @($Messages) }
    }

    $overrideGiven = $PSBoundParameters.ContainsKey('AcceptCiFailure') -and $null -ne $AcceptCiFailure
    if ($overrideGiven -and [string]::IsNullOrWhiteSpace($AcceptCiFailure)) {
        return (& $outcome 'Abort' @('-AcceptCiFailure needs the reason, in words: it is written into the release notes.'))
    }
    if ($Failures.Count -eq 0) {
        return (& $outcome 'Pass' @())
    }
    if ($overrideGiven) {
        return (& $outcome 'Overridden' (@($Failures) + @("Overridden by -AcceptCiFailure: $AcceptCiFailure")))
    }
    if ($Fatal) {
        return (& $outcome 'Abort' (@($Failures) + @('Refusing to publish. Fix the findings, or pass -AcceptCiFailure "<reason>" to publish anyway with the reason in the release notes.')))
    }
    return (& $outcome 'Warn' (@($Failures) + @('A -Publish run would stop here.')))
}

function Get-ReleaseGateOverrideNotes {
    <#
    .SYNOPSIS
        The release-notes section that records an -AcceptCiFailure override.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$Reason,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Failures
    )

    $section = "### Release gate override`n`n"
    $section += "This release was published although its release gates did not pass. Reason given: $($Reason.Trim())`n`n"
    foreach ($failure in $Failures) {
        $section += "- $failure`n"
    }
    return $section + "`n"
}

function Get-ReleaseCreateArgs {
    <#
    .SYNOPSIS
        The `gh release create` arguments, with the tag pinned to the release commit.
    .DESCRIPTION
        Without --target, GitHub creates a missing tag on the default branch as it stands
        when the call arrives, which is not necessarily the commit this run built.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$BuildNumber,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Artifacts,
        [Parameter(Mandatory = $true)] [string]$NotesFile,
        [Parameter(Mandatory = $true)] [string]$TargetSha
    )

    if ([string]::IsNullOrWhiteSpace($TargetSha)) {
        throw 'A release needs the SHA of the commit it tags.'
    }

    return @('release', 'create', "v$BuildNumber") + $Artifacts + @('--title', "v$BuildNumber", '--notes-file', $NotesFile, '--target', $TargetSha)
}
