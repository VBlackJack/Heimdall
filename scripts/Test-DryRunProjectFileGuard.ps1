<#
.SYNOPSIS
    Tests that a dry run does not rewrite the application project file
    (scripts/BuildVersioning.ps1).

.DESCRIPTION
    Build.ps1 -DryRun is documented as making no git changes. It used to rewrite
    the tracked src/Heimdall.App/Heimdall.App.csproj anyway, and because the
    auto-increment reads <InformationalVersion> back out of that same file, two
    consecutive dry runs produced two different build numbers.

    SCOPE. This suite covers exactly one file: the application project. It is
    NOT a whole-tree cleanliness check, and its silence is not evidence that a
    dry run leaves the working tree clean. It does not.

    Measured on 2026-07-26 with a full Build.ps1 -Mode Release -DryRun: the RID
    restore inside dotnet publish -r win-x64, combined with
    RestorePackagesWithLockFile in Directory.Build.props, rewrites nine tracked
    packages.lock.json files under src/. That mutation predates this guard,
    happens identically on the real publish path, is tracked separately, and is
    deliberately out of scope here. Do not read a green run as its absence.

    The suite proves both directions, because a guard that only ever passes
    proves nothing:

      1. A dry run leaves the project file byte-identical    -> the file is not
                                                                rewritten.
      2. A dry run reports that it skipped the write         -> the skip is real,
                                                                not an accident of
                                                                writing identical
                                                                bytes back.
      3. A real run DOES write the file                      -> case 1 is not
                                                                passing because the
                                                                function became a
                                                                no-op for everyone.
      4. A real run stamps both elements correctly           -> the write that the
                                                                release commit
                                                                carries is right.
      5. A dry run supplies the versions to MSBuild instead  -> skipping the write
                                                                does not silently
                                                                drop the version.
      6. A real run supplies no MSBuild overrides            -> exactly one of the
                                                                two mechanisms is
                                                                active per run.
      7. A dry run does not move the auto-increment baseline -> the drift loop is
                                                                closed.
      8. The transform inserts the element when absent       -> the fallback branch
                                                                is covered too.
      9. Build.ps1 writes the project file only through the  -> a future edit cannot
         helper                                                 reintroduce the
                                                                 unguarded write.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  powershell -NoProfile -File scripts\Test-DryRunProjectFileGuard.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BuildVersioning.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\Heimdall.App\Heimdall.App.csproj'
$buildScript = Join-Path $repoRoot 'Build.ps1'
$failures = 0

# Values deliberately unlike anything the project file could already contain, so
# a passing assertion cannot be a coincidence.
$testAssemblyVersion = '1.0.9999.42'
$testInformationalVersion = '9999.999942'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-SandboxProject {
    <#
    .SYNOPSIS
        Copies the real tracked project file into a temp sandbox.
    .DESCRIPTION
        The real file is used rather than a synthetic fragment so the suite
        exercises the element shapes actually shipped. Every case works on its
        own copy; the tracked file is never opened for writing.
    #>
    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-projectfileguard-" + [System.Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $sandbox -Force
    $copy = Join-Path $sandbox 'Heimdall.App.csproj'
    Copy-Item -LiteralPath $script:appProject -Destination $copy -Force
    return $copy
}

function Remove-Sandbox {
    param([string]$ProjectPath)
    Remove-Item -LiteralPath (Split-Path $ProjectPath -Parent) -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $appProject)) {
    Write-Host "FAIL: application project not found at $appProject" -ForegroundColor Red
    exit 1
}

# ----------------------------------------------------------------------------
# Cases 1, 2 and 7: the dry run leaves the file alone, says so, and does not
# move the baseline the next auto-increment reads.
# ----------------------------------------------------------------------------
$sandbox = New-SandboxProject
try {
    $before = [System.IO.File]::ReadAllBytes($sandbox)

    $wrote = Update-AppProjectVersion -Path $sandbox `
        -AssemblyVersion $testAssemblyVersion `
        -InformationalVersion $testInformationalVersion `
        -DryRun

    $after = [System.IO.File]::ReadAllBytes($sandbox)
    $identical = ($before.Length -eq $after.Length) -and
        ([System.Linq.Enumerable]::SequenceEqual($before, $after))

    if (-not $identical) {
        # Name the file and the damage: this is the message an operator sees when
        # the unguarded write comes back, so it has to point at the culprit.
        Write-Host "      dry run modified $sandbox" -ForegroundColor DarkYellow
        Write-Host ("      {0} bytes before, {1} bytes after" -f $before.Length, $after.Length) -ForegroundColor DarkYellow
        $changedText = [System.IO.File]::ReadAllText($sandbox)
        foreach ($element in @('Version', 'InformationalVersion')) {
            if ($changedText -match "<$element>([^<]+)</$element>") {
                Write-Host ("      <{0}> is now {1}" -f $element, $Matches[1]) -ForegroundColor DarkYellow
            }
        }
    }

    Assert-True $identical "dry run leaves Heimdall.App.csproj byte-identical ($($before.Length) bytes)"
    Assert-True ($wrote -eq $false) 'dry run reports that it skipped the write'

    # The drift loop: Build.ps1 reads <InformationalVersion> back out of this file
    # to pick the next sequence number. If a dry run moved it, the next dry run
    # would start one higher. Mirrors the pattern at Build.ps1 step "auto-increment".
    $baselineText = [System.IO.File]::ReadAllText($sandbox)
    $baselineMatched = $baselineText -match '<InformationalVersion>(\d{4}\.\d{6})</InformationalVersion>'
    $baseline = if ($baselineMatched) { $Matches[1] } else { '<absent>' }
    Assert-True ($baselineMatched -and $baseline -ne $testInformationalVersion) `
        "dry run leaves the auto-increment baseline at $baseline, so a second dry run computes the same build number"
} finally {
    Remove-Sandbox -ProjectPath $sandbox
}

# ----------------------------------------------------------------------------
# Cases 3 and 4: the real publish path still writes, and writes the right thing.
# Without these, case 1 would also pass if the function stopped writing entirely.
# ----------------------------------------------------------------------------
$sandbox = New-SandboxProject
try {
    $before = [System.IO.File]::ReadAllBytes($sandbox)

    $wrote = Update-AppProjectVersion -Path $sandbox `
        -AssemblyVersion $testAssemblyVersion `
        -InformationalVersion $testInformationalVersion

    $after = [System.IO.File]::ReadAllBytes($sandbox)
    $changed = -not (($before.Length -eq $after.Length) -and
        ([System.Linq.Enumerable]::SequenceEqual($before, $after)))

    Assert-True ($wrote -eq $true -and $changed) 'real run writes Heimdall.App.csproj'

    $text = [System.IO.File]::ReadAllText($sandbox)
    $hasVersion = $text -match "<Version>$([regex]::Escape($testAssemblyVersion))</Version>"
    $hasInformational = $text -match "<InformationalVersion>$([regex]::Escape($testInformationalVersion))</InformationalVersion>"
    Assert-True ($hasVersion -and $hasInformational) `
        "real run stamps <Version>$testAssemblyVersion</Version> and <InformationalVersion>$testInformationalVersion</InformationalVersion>"

    # A no-BOM write, like the original: a BOM here would be a tracked-file change
    # of its own the next time anything rewrites the project.
    $startsWithBom = $after.Length -ge 3 -and $after[0] -eq 0xEF -and $after[1] -eq 0xBB -and $after[2] -eq 0xBF
    Assert-True (-not $startsWithBom) 'real run writes the project file without a byte-order mark'
} finally {
    Remove-Sandbox -ProjectPath $sandbox
}

# ----------------------------------------------------------------------------
# Cases 5 and 6: exactly one mechanism carries the version on any given run.
# Skipping the write is only safe because the properties take over.
# ----------------------------------------------------------------------------
$dryArgs = @(Get-VersionOverrideArgs -AssemblyVersion $testAssemblyVersion -InformationalVersion $testInformationalVersion -DryRun)
Assert-True ($dryArgs.Count -eq 2) "dry run supplies MSBuild overrides ($($dryArgs.Count) args: $($dryArgs -join ' '))"
Assert-True ($dryArgs -contains "-p:Version=$testAssemblyVersion") 'dry run overrides Version'
Assert-True ($dryArgs -contains "-p:InformationalVersion=$testInformationalVersion") 'dry run overrides InformationalVersion'

$realArgs = @(Get-VersionOverrideArgs -AssemblyVersion $testAssemblyVersion -InformationalVersion $testInformationalVersion)
Assert-True ($realArgs.Count -eq 0) "real run supplies no MSBuild overrides ($($realArgs.Count) args), the project file is the mechanism"

# ----------------------------------------------------------------------------
# Case 8: the insert branch, for a project that declares no InformationalVersion.
# ----------------------------------------------------------------------------
$minimalProject = "<Project>`n  <PropertyGroup>`n    <Version>1.0.0.0</Version>`n  </PropertyGroup>`n</Project>"
$inserted = Get-StampedAppProjectText -Text $minimalProject `
    -AssemblyVersion $testAssemblyVersion `
    -InformationalVersion $testInformationalVersion
Assert-True ($inserted -match "<InformationalVersion>$([regex]::Escape($testInformationalVersion))</InformationalVersion>") `
    'transform inserts <InformationalVersion> when the project does not declare one'
Assert-True ($inserted -match "<Version>$([regex]::Escape($testAssemblyVersion))</Version>") `
    'transform still replaces <Version> when inserting'

# ----------------------------------------------------------------------------
# Case 9: Build.ps1 must reach the project file only through the helper. This is
# the assertion that catches a reintroduced direct write - the exact regression
# this suite exists for, which no behavioural test of the helper can see.
# ----------------------------------------------------------------------------
if (Test-Path -LiteralPath $buildScript) {
    # Matched line by line rather than with a multiline regex over the raw text:
    # the repository checks out CRLF, and an anchored (?m)^...$ pattern silently
    # never matches there because $ expects \n and finds \r. That exact mistake
    # made this assertion vacuous when it was first written, and it was only
    # caught by deliberately reintroducing the write to see the suite go red.
    $buildLines = @(Get-Content -LiteralPath $buildScript)
    $directWrites = @($buildLines | Where-Object {
        $_ -match '(?:WriteAllText|WriteAllBytes|WriteAllLines|Set-Content|Out-File)[^\r\n]*\$AppProject'
    })
    foreach ($line in $directWrites) {
        Write-Host ("      Build.ps1 writes the project file directly: {0}" -f $line.Trim()) -ForegroundColor DarkYellow
    }
    Assert-True ($directWrites.Count -eq 0) `
        "Build.ps1 writes Heimdall.App.csproj only through Update-AppProjectVersion ($($directWrites.Count) direct write(s))"
} else {
    Write-Host "SKIP: $buildScript not found" -ForegroundColor Yellow
}

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures dry-run project-file guard test(s) FAILED." -ForegroundColor Red
    Write-Host 'Build.ps1 -DryRun can now rewrite src/Heimdall.App/Heimdall.App.csproj.' -ForegroundColor Red
    exit 1
}
# Deliberately states the scope on the happy path too. The green line is what a
# reader sees most often, so it is where the limit has to be visible: the tracked
# packages.lock.json files are rewritten by the publish RID restore and this
# suite does not look at them.
Write-Host 'Build.ps1 -DryRun does not rewrite the application project file.' -ForegroundColor Green
Write-Host 'Scope: that file only. Not a whole-tree cleanliness check.' -ForegroundColor DarkGray
