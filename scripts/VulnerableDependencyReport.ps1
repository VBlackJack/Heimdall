<#
.SYNOPSIS
    Reads the result of `dotnet list package --vulnerable --format json` and decides
    whether the scan actually happened, and what it found.

.DESCRIPTION
    Dot-source this file to get Get-VulnerableDependencyVerdict and
    Get-SolutionProjectNames; run scripts/Invoke-VulnerableDependencyCheck.ps1 for the
    CI step itself.

    The CI step used to pipe the command into Out-String, never read its exit code, and
    print "No vulnerable dependencies detected." whenever the text lacked the phrase of
    a finding. A failed restore, a missing solution or an unreachable feed therefore read
    as a clean scan. The verdict here is fail-closed: a scan is Clean only when the
    command exited 0, its output parses, it reports no problem, and every project of the
    solution appears in it.

    JSON rather than the console text: the text is localized (a French SDK prints "n'a
    aucun package vulnerable"), so a phrase match would read every project of a French
    developer box as unscanned, and the same match would silently stop matching the day
    the English wording changes.

    Verdicts:
      Clean        every project scanned, no vulnerable package
      Vulnerable   every project scanned, at least one vulnerable package
      ToolFailure  the scan cannot be trusted; Reasons says why

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>

# A non-zero exit is retried once (a feed hiccup on the runner), then fails the step.
$script:VulnerableScanAttempts = 2

function Invoke-VulnerableDependencyScan {
    <#
    .SYNOPSIS
        Runs the scan, retries it once when it exits non-zero, and classifies the result.
    .PARAMETER Runner
        Runs the scan once and returns an object with ExitCode and Output (the JSON text).
    .OUTPUTS
        The Get-VulnerableDependencyVerdict object, plus Attempts and Output.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [scriptblock]$Runner,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$ExpectedProjects
    )

    $attempt = 0
    do {
        $attempt++
        $run = & $Runner
        if ($run.ExitCode -eq 0) { break }
        Write-Host "Vulnerability scan attempt $attempt of $script:VulnerableScanAttempts exited with code $($run.ExitCode)."
    } while ($attempt -lt $script:VulnerableScanAttempts)

    $verdict = Get-VulnerableDependencyVerdict -ExitCode $run.ExitCode -Output ([string]$run.Output) -ExpectedProjects $ExpectedProjects
    $verdict | Add-Member -NotePropertyName Attempts -NotePropertyValue $attempt
    $verdict | Add-Member -NotePropertyName Output -NotePropertyValue ([string]$run.Output)
    return $verdict
}

function Get-SolutionProjectNames {
    <#
    .SYNOPSIS
        The project names a .slnx declares (file names without extension).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SolutionPath
    )

    [xml]$solution = [System.IO.File]::ReadAllText($SolutionPath)
    $names = @($solution.SelectNodes('//Project') | ForEach-Object {
        [System.IO.Path]::GetFileNameWithoutExtension((($_.GetAttribute('Path')) -replace '\\', '/'))
    })
    return $names
}

function Get-VulnerableDependencyVerdict {
    <#
    .SYNOPSIS
        Classifies one run of the vulnerability scan.
    .OUTPUTS
        An object with Verdict, Reasons (why it is not Clean) and Findings (one line per
        vulnerable package, for the warning).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExpectedProjects
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    $findings = New-Object System.Collections.Generic.List[string]
    $verdict = {
        param($Name)
        return [pscustomobject]@{ Verdict = $Name; Reasons = $reasons.ToArray(); Findings = $findings.ToArray() }
    }

    if ($ExitCode -ne 0) {
        $reasons.Add("dotnet list package exited with code $ExitCode.")
        return (& $verdict 'ToolFailure')
    }
    if ($ExpectedProjects.Count -eq 0) {
        $reasons.Add('The solution declares no project, so nothing was scanned.')
        return (& $verdict 'ToolFailure')
    }

    $report = $null
    try {
        $report = ConvertFrom-Json -InputObject $Output
    } catch {
        $report = $null
    }
    if ($null -eq $report -or -not ($report.PSObject.Properties.Name -contains 'projects')) {
        $reasons.Add('The scan output is not the JSON report dotnet list package --format json writes.')
        return (& $verdict 'ToolFailure')
    }

    if ($report.PSObject.Properties.Name -contains 'problems') {
        foreach ($problem in @($report.problems)) {
            $reasons.Add("Reported problem: $($problem.text)")
        }
    }

    $scanned = @{}
    foreach ($project in @($report.projects)) {
        $projectPath = ([string]$project.path) -replace '\\', '/'
        $name = [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        $scanned[$name] = $true

        if ($project.PSObject.Properties.Name -contains 'problems') {
            foreach ($problem in @($project.problems)) {
                $reasons.Add("$name reported a problem: $($problem.text)")
            }
        }
        if (-not ($project.PSObject.Properties.Name -contains 'frameworks')) { continue }
        foreach ($framework in @($project.frameworks)) {
            foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
                if (-not ($framework.PSObject.Properties.Name -contains $kind)) { continue }
                foreach ($package in @($framework.$kind)) {
                    if (-not ($package.PSObject.Properties.Name -contains 'vulnerabilities')) { continue }
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        $findings.Add(("{0} ({1}): {2} {3} - {4} {5}" -f $name, $framework.framework, $package.id, $package.resolvedVersion, $vulnerability.severity, $vulnerability.advisoryurl))
                    }
                }
            }
        }
    }

    foreach ($expected in $ExpectedProjects) {
        if (-not $scanned.ContainsKey($expected)) {
            $reasons.Add("$expected is in the solution but absent from the scan report.")
        }
    }

    if ($reasons.Count -gt 0) {
        return (& $verdict 'ToolFailure')
    }
    if ($findings.Count -gt 0) {
        return (& $verdict 'Vulnerable')
    }
    return (& $verdict 'Clean')
}
