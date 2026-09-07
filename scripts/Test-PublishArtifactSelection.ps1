<#
.SYNOPSIS
    Tests the publish artifact selection (scripts/PublishArtifactSelection.ps1).

.DESCRIPTION
    The rule decides what a release uploads, so a version of it that filtered too much
    would silently publish a release with no installer, and a version that filtered too
    little would put the release body back in the asset list. Both directions are
    proven:

      1. An installer, a zip and a checksum list  -> all three kept, in order. A filter
                                                     that rejected too much would drop
                                                     the very thing a release is for.
      2. `RELEASE_NOTES_v<build>.md` beside them  -> dropped. This is the defect: it is
                                                     the release body, not an asset.
      3. Uppercase `.MD`                          -> dropped too, because a case-sensitive
                                                     comparison would pass case 2 and
                                                     still let this through.
      4. An empty candidate list                  -> an empty result and no throw. A
                                                     publish with nothing built must fail
                                                     on its own message, not on this.
      5. Build.ps1 reaches the installer directory -> the rule is actually wired in. Case
         only through this rule                      2 would pass just as well on a
                                                     Build.ps1 that never calls it.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-PublishArtifactSelection.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'PublishArtifactSelection.ps1')

$failures = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-FixtureDirectory {
    param([string[]] $FileName)

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-artifacts-" + [System.Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path -Force
    foreach ($name in $FileName) {
        [System.IO.File]::WriteAllText((Join-Path $path $name), 'fixture')
    }
    return $path
}

function Remove-FixtureDirectory {
    param([string] $Path)
    if ($Path -and (Test-Path -LiteralPath $Path)) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 1 and 2. The real assets survive; the generated release notes do not.
$fixture = New-FixtureDirectory -FileName @(
    'Heimdall_2026.090701_Standard_Setup.exe',
    'Heimdall_build.2026.090701.zip',
    'SHA256SUMS.txt',
    'RELEASE_NOTES_v2026.090701.md'
)
try {
    $candidates = @(Get-ChildItem -LiteralPath $fixture -File)
    $kept = @(Select-PublishArtifact -Candidate $candidates)
    $keptNames = @($kept | ForEach-Object { $_.Name })

    Assert-True ($kept.Count -eq 3) "the three real assets are kept ($($kept.Count) kept of $($candidates.Count))"
    Assert-True ($keptNames -contains 'Heimdall_2026.090701_Standard_Setup.exe') 'the installer is kept'
    Assert-True ($keptNames -contains 'Heimdall_build.2026.090701.zip') 'the portable archive is kept'
    Assert-True ($keptNames -contains 'SHA256SUMS.txt') 'the checksum list is kept'
    Assert-True (-not ($keptNames -contains 'RELEASE_NOTES_v2026.090701.md')) 'the generated release notes are not an asset'
}
finally { Remove-FixtureDirectory -Path $fixture }

# 3. The extension comparison is case-insensitive.
$fixture = New-FixtureDirectory -FileName @('Heimdall_Setup.exe', 'RELEASE_NOTES_V2026.090701.MD')
try {
    $kept = @(Select-PublishArtifact -Candidate @(Get-ChildItem -LiteralPath $fixture -File))
    Assert-True ($kept.Count -eq 1) "an uppercase .MD is dropped just the same ($($kept.Count) kept)"
}
finally { Remove-FixtureDirectory -Path $fixture }

# 4. Nothing to publish is not an error here.
$kept = @(Select-PublishArtifact -Candidate @())
Assert-True ($kept.Count -eq 0) 'an empty candidate list gives an empty result without throwing'

# 5. The rule is wired in: Build.ps1 must not enumerate the installer directory past it.
$buildScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1'
$buildText = Get-Content -LiteralPath $buildScript -Raw
Assert-True ($buildText -match 'Select-PublishArtifact') 'Build.ps1 selects its artifacts through the shared rule'
Assert-True ($buildText -match "PublishArtifactSelection\.ps1") 'Build.ps1 dot-sources the rule it calls'

# Every enumeration of that directory must be captured and then filtered, never piped
# straight into its consumer: a piped one has already skipped the rule by construction.
$enumerations = @(
    Get-Content -LiteralPath $buildScript | Where-Object { $_ -match 'Get-ChildItem\s+\$installerDir' }
)
$piped = @($enumerations | Where-Object { $_ -match '\|' })
Assert-True ($enumerations.Count -gt 0) "the installer directory is still enumerated somewhere ($($enumerations.Count) site(s))"
Assert-True ($piped.Count -eq 0) "no enumeration bypasses the rule by piping straight on ($($piped.Count) found)"
foreach ($line in $piped) {
    Write-Host ("      {0}" -f $line.Trim()) -ForegroundColor DarkYellow
}

$selectCalls = @(
    Get-Content -LiteralPath $buildScript | Where-Object { $_ -match 'Select-PublishArtifact\s+-Candidate' }
)
Assert-True ($selectCalls.Count -eq $enumerations.Count) `
    "each of the $($enumerations.Count) enumeration(s) has its own filtered consumer ($($selectCalls.Count) call(s))"

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: a publish uploads its installers and its checksums, never its own release body.' -ForegroundColor Green
exit 0
