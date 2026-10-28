<#
.SYNOPSIS
    Build script for Heimdall - produces portable distributions and installers.

.DESCRIPTION
    In Release mode, before the version bump, the run checks that the whole tree is
    clean, that the branch is master, that HEAD equals origin/master after a fetch, and
    that the latest ci.yml push run for HEAD succeeded, waiting a bounded time for one
    in progress under -Publish (scripts/ReleasePreflight.ps1). A Release with the
    SelfContained edition also requires a recorded WebView2 runtime younger than
    MaxWebView2RuntimeAgeDays (scripts/WebView2RuntimeGate.ps1). A finding is fatal with
    -Publish and a warning otherwise; -AcceptCiFailure is the only override.

.PARAMETER Mode
    Build mode: 'Debug' (default) or 'Release'.

.PARAMETER Variant
    Which editions to produce: 'Both' (default), 'Standard', or 'SelfContained'.

.PARAMETER SkipTests
    Skip running tests before build.

.PARAMETER Publish
    After build, create a GitHub release with all artifacts (Release mode only).

.PARAMETER Version
    Force a specific build number (e.g. '2026.033101') instead of auto-incrementing.

.PARAMETER ReleaseNotesFile
    Optional path to a hand-written notes file (e.g. localized highlights) prepended
    above the auto-generated Downloads/Checksums section of the GitHub release notes.
    When omitted, the tracked convention file 'docs/release-notes/v<version>.md' is used
    and is required unless -AllowAutoNotesOnly is passed.

.PARAMETER AllowNonAsciiNotes
    Override the typography guard. By default the publish step refuses to publish when
    the hand-written notes contain a character the French AZERTY keyboard cannot type
    (smart punctuation, French guillemets, non-standard spaces, arrows, emoji); pass
    this switch to publish anyway.

.PARAMETER AllowAutoNotesOnly
    Override the hand-written release-notes requirement. By default the publish step
    refuses to continue when neither -ReleaseNotesFile nor the tracked convention file
    exists; pass this switch to publish the generated Downloads/Checksums sections only.

.PARAMETER AcceptCiFailure
    The one override of the release gates (the preflight and the WebView2 runtime age),
    given as the reason in words: the publish continues and the reason, with the findings
    it overrode, is written into the generated release notes. It does not excuse a missing
    WebView2 runtime; pass -Variant Standard for that. Refresh the runtime with
    scripts/Update-WebView2FixedRuntime.ps1 rather than overriding its age.

.EXAMPLE
    .\Build.ps1                              # Debug build
    .\Build.ps1 -Mode Release                # Release build with installers
    .\Build.ps1 -Mode Release -Publish       # Release + GitHub publish
    .\Build.ps1 -Mode Release -DryRun        # Full build + simulated publish (no git/gh changes)
    .\Build.ps1 -Mode Release -Version 2026.033101  # Force version
    .\Build.ps1 -Mode Release -Publish -ReleaseNotesFile notes.md  # Prepend custom notes

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Mode = 'Debug',

    [ValidateSet('Standard', 'SelfContained', 'Both')]
    [string]$Variant = 'Both',

    [switch]$SkipTests,

    [switch]$Publish,

    # Simulate -Publish without actually pushing or creating the GitHub release
    [switch]$DryRun,

    [string]$Version,

    # Optional hand-written notes file prepended above the auto-generated release
    # notes. Falls back to docs/release-notes/v<version>.md when omitted.
    [string]$ReleaseNotesFile,

    # Override the release-notes typography guard (publish despite banned characters).
    [switch]$AllowNonAsciiNotes,

    # Override the hand-written release-notes requirement (publish generated notes only).
    [switch]$AllowAutoNotesOnly,

    # The one override of the release gates, as a reason written into the release notes.
    [string]$AcceptCiFailure
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = $PSScriptRoot

# Release-notes typography guard (allow-list of AZERTY-typeable characters).
. (Join-Path $ProjectRoot 'scripts\NotesTypographyGuard.ps1')
# Release-notes path resolution and fail-closed decision.
. (Join-Path $ProjectRoot 'scripts\ReleaseNotesResolution.ps1')
# Version stamping, and the rule that a dry run never writes the project file.
. (Join-Path $ProjectRoot 'scripts\BuildVersioning.ps1')

. (Join-Path $ProjectRoot 'scripts\PublishArtifactSelection.ps1')
# Release preflight (clean tree, master, in step with origin, CI green) and the
# release-create arguments pinned to the release commit.
. (Join-Path $ProjectRoot 'scripts\ReleasePreflight.ps1')
# Whether the SelfContained edition may bundle runtimes/webview2.
. (Join-Path $ProjectRoot 'scripts\WebView2RuntimeGate.ps1')
# The Inno Setup compile, with its output kept for the build to print.
. (Join-Path $ProjectRoot 'scripts\InnoSetupCompile.ps1')
# What the informational CI lanes (CIUnstable, RequiresDesktop) reported for the release
# commit: printed as warnings and written into the release notes, never a gate.
. (Join-Path $ProjectRoot 'scripts\InformationalLaneReport.ps1')
$AppProject = Join-Path $ProjectRoot 'src\Heimdall.App\Heimdall.App.csproj'
$SolutionFile = Get-ChildItem -Path $ProjectRoot -Filter '*.slnx' | Select-Object -First 1
$distDir = Join-Path $ProjectRoot "Dist\$($Mode.ToLower())"

if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

if ($Publish -and -not $DryRun) {
    if ($Mode -ne 'Release') {
        Write-Output "[!] Publish requires -Mode Release."
        exit 1
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Output "[!] GitHub CLI 'gh' was not found on PATH."
        exit 1
    }

    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & gh auth status *> $null
    $ghAuthExitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($ghAuthExitCode -ne 0) {
        Write-Output "[!] GitHub CLI is not authenticated. Run 'gh auth login' and retry."
        exit 1
    }
}

# ── Build number: YYYY.MMDDxx (xx = sequential within day) ──────────────────

$today = Get-Date
$datePrefix = $today.ToString('yyyy.MMdd')

if ($Version) {
    # Forced version - validate format
    if ($Version -notmatch '^\d{4}\.\d{6}$') {
        Write-Host "[!] Invalid version format '$Version'. Expected YYYY.MMDDxx (e.g. 2026.033101)." -ForegroundColor Red
        exit 1
    }
    $buildNumber = $Version
    if ($Version -match '\d{4}\.\d{4}(\d{2})$') {
        $sequence = [int]$Matches[1]
    } else {
        $sequence = 1
    }
} else {
    # Auto-increment: find highest existing build number for today
    $allDistDirs = @(
        (Join-Path $ProjectRoot 'Dist\debug'),
        (Join-Path $ProjectRoot 'Dist\release')
    ) | Where-Object { Test-Path $_ }

    $existingBuilds = @($allDistDirs | ForEach-Object {
        Get-ChildItem -Path $_ -Directory -Filter "Heimdall_build.${datePrefix}*" -ErrorAction SilentlyContinue
    } | ForEach-Object {
        if ($_.Name -match "build\.${datePrefix}(\d{2})(?:_|$)") { [int]$Matches[1] }
    })

    # Also check the csproj InformationalVersion
    $csprojRaw = Get-Content $AppProject -Raw
    if ($csprojRaw -match '<InformationalVersion>(\d{4}\.\d{6})</InformationalVersion>') {
        $currentVer = $Matches[1]
        if ($currentVer -match "${datePrefix}(\d{2})$") {
            $existingBuilds += [int]$Matches[1]
        }
    }

    # Also check GitHub releases to avoid collisions
    try {
        $ghTags = & gh release list --limit 20 2>$null
        if ($LASTEXITCODE -eq 0 -and $ghTags) {
            $ghTags | ForEach-Object {
                if ($_ -match "v${datePrefix}(\d{2})") {
                    $existingBuilds += [int]$Matches[1]
                }
            }
        }
    } catch {}

    $existingBuilds = @($existingBuilds | Sort-Object -Descending)
    $sequence = if ($existingBuilds.Count -gt 0) { $existingBuilds[0] + 1 } else { 1 }
    $buildNumber = "{0}{1:D2}" -f $datePrefix, $sequence
}

$assemblyDate = $buildNumber.Substring(5, 4)
$assemblyVer = "1.0.${assemblyDate}.$sequence"
# The MSI compares only three fields and caps them, so it gets a form of its own that
# keeps the year and the sequence. See ConvertTo-MsiProductVersion.
$msiVersion = ConvertTo-MsiProductVersion -BuildNumber $buildNumber
$totalSteps = if ($Mode -eq 'Release') { 6 } else { 5 }
$step = 0

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Heimdall Build v${buildNumber}" -ForegroundColor Cyan
Write-Host "  Mode: $Mode" -ForegroundColor Cyan
if ($Publish) { Write-Host "  Publish: GitHub Release" -ForegroundColor Cyan }
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# ── Determine which variants to produce ───────────────────────────────────
#
# Decided before anything is built: a Release that cannot bundle the runtime it was
# asked to bundle stops here rather than after the test run. A missing runtime is
# fatal with no override; a stale or unrecorded one is a release-gate finding below.

$variants = switch ($Variant) {
    'Both'          { @('Standard', 'SelfContained') }
    'Standard'      { @('Standard') }
    'SelfContained' { @('SelfContained') }
}

$releaseGateFailures = @()
$wv2Runtime = Join-Path $ProjectRoot 'runtimes\webview2'
$wv2Gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $wv2Runtime -Mode $Mode -Variants $variants
switch ($wv2Gate.Decision) {
    'NotRequired' { }
    'Ready' {
        foreach ($line in $wv2Gate.Messages) { Write-Host "[webview2] $line" -ForegroundColor DarkGray }
    }
    'Stale' {
        foreach ($line in $wv2Gate.Messages) { Write-Host "[webview2] $line" -ForegroundColor Yellow }
        $releaseGateFailures += @($wv2Gate.Messages)[0]
    }
    'FallBackToStandard' {
        foreach ($line in $wv2Gate.Messages) { Write-Host "[!] $line" -ForegroundColor DarkYellow }
    }
    'Abort' {
        foreach ($line in $wv2Gate.Messages) { Write-Host "[!] $line" -ForegroundColor Red }
        exit 1
    }
    default {
        Write-Host "[!] Unexpected WebView2 runtime decision: $($wv2Gate.Decision)" -ForegroundColor Red
        exit 1
    }
}
$variants = @($wv2Gate.Variants)

# ── Release preflight: the tree about to ship is the one CI verified ──────
#
# Runs before the version bump so a refused release leaves nothing to undo. Fatal
# under -Publish, where a CI run still in progress is waited for (bounded); a dry
# run or a local Release build prints the same findings as warnings. The checks and
# their reasons live in scripts/ReleasePreflight.ps1. -AcceptCiFailure is the one
# override of every release-gate finding, and its reason goes into the notes.

$releaseGateOverride = $null
$informationalLaneNotes = ''
if ($Mode -eq 'Release') {
    $releasePreflightFatal = $Publish.IsPresent -and -not $DryRun.IsPresent
    $preflight = Invoke-ReleasePreflight -WaitForCi:$releasePreflightFatal
    if ($preflight.Passed) {
        Write-Host "[preflight] Clean tree on master at origin, CI green for $($preflight.HeadSha)" -ForegroundColor Green
    }
    $releaseGateFailures += @($preflight.Failures)

    $gateArgs = @{ Failures = @($releaseGateFailures); Fatal = $releasePreflightFatal }
    if ($PSBoundParameters.ContainsKey('AcceptCiFailure')) { $gateArgs['AcceptCiFailure'] = $AcceptCiFailure }
    $gateOutcome = Resolve-ReleaseGateOutcome @gateArgs
    switch ($gateOutcome.Decision) {
        'Pass' { }
        'Warn' {
            foreach ($line in $gateOutcome.Messages) { Write-Host "[release gate] WARNING: $line" -ForegroundColor Yellow }
        }
        'Overridden' {
            Write-Host "[release gate] ************************************************************" -ForegroundColor Yellow
            foreach ($line in $gateOutcome.Messages) { Write-Host "[release gate] OVERRIDDEN: $line" -ForegroundColor Yellow }
            Write-Host "[release gate] ************************************************************" -ForegroundColor Yellow
            $releaseGateOverride = [pscustomobject]@{ Reason = $AcceptCiFailure; Failures = @($releaseGateFailures) }
        }
        'Abort' {
            foreach ($line in $gateOutcome.Messages) { Write-Host "[!] $line" -ForegroundColor Red }
            exit 1
        }
        default {
            Write-Host "[!] Unexpected release gate decision: $($gateOutcome.Decision)" -ForegroundColor Red
            exit 1
        }
    }

    # The informational lanes cannot turn a CI run red, so "CI green" above says nothing
    # about them. They do not gate the release either; their result for this commit is
    # printed and written into the notes, so nobody ships a failure unknowingly. Read after
    # the decision: a refused release pays no artifact download.
    if ($null -ne $preflight.HeadSha) {
        $informationalLaneReport = Get-InformationalLaneReport -Sha $preflight.HeadSha
        $informationalLaneMessages = @(Get-InformationalLaneMessages -Report $informationalLaneReport)
        if ($informationalLaneMessages.Count -eq 0) {
            Write-Host "[informational lanes] No failure in $($informationalLaneReport.ResultCount) test result(s) of CIUnstable and RequiresDesktop." -ForegroundColor Green
        }
        foreach ($line in $informationalLaneMessages) { Write-Host "[informational lanes] WARNING: $line" -ForegroundColor Yellow }
        $informationalLaneNotes = Get-InformationalLaneNotes -Report $informationalLaneReport
    }
}

# ── Step 1: Update version in csproj ───────────────────────────────────────
#
# The real Release path writes the project file: that write is what the release
# commit carries. A dry run and a Debug build must not touch it, so they receive
# the same two versions as MSBuild global properties instead, splatted into the
# build and publish invocations below. See scripts/BuildVersioning.ps1 for why,
# and for the measurement that chose this over writing the file and restoring it.

$step++
$leaveProjectFile = Test-LeavesProjectFileUntouched -Mode $Mode -DryRun:$DryRun
$versionArgs = @(Get-VersionOverrideArgs -AssemblyVersion $assemblyVer -InformationalVersion $buildNumber -DryRun:$leaveProjectFile)
$versionWritten = Update-AppProjectVersion -Path $AppProject `
    -AssemblyVersion $assemblyVer `
    -InformationalVersion $buildNumber `
    -DryRun:$leaveProjectFile
if ($versionWritten) {
    Write-Host "[$step/$totalSteps] Version set to $buildNumber (assembly: $assemblyVer)" -ForegroundColor Green
} else {
    Write-Host "[$step/$totalSteps] Version $buildNumber (assembly: $assemblyVer) passed to MSBuild; $(Split-Path $AppProject -Leaf) left untouched ($Mode$(if ($DryRun) { ' dry run' }))" -ForegroundColor Magenta
}

# Runs a native command with its stderr folded into its output, records every line in
# $LogPath, and returns the lines with the exit code. Under $ErrorActionPreference = 'Stop',
# Windows PowerShell turns the first redirected stderr line into a terminating error: the test
# step died on xUnit's first [FAIL] line, before it could print the failure, so a red gate
# named a test and never said why (seen 2026-10-14 on an SFTP test that failed twice). The
# log keeps the whole run, assertion messages included.
function Invoke-NativeLogged {
    param(
        [Parameter(Mandatory)] [scriptblock] $Command,
        [Parameter(Mandatory)] [string] $LogPath
    )

    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $Command 2>&1 | ForEach-Object { "$_" })
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $prevEAP
    }

    $logDir = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    }
    Set-Content -LiteralPath $LogPath -Value $lines -Encoding UTF8

    return [pscustomobject]@{ ExitCode = $exitCode; Lines = $lines }
}

# The lines of a failed test run worth showing on screen: each failing test and its assertion
# message, up to the stack trace. The rest, the trace included, stays in the log.
function Select-TestFailureLines {
    param([string[]] $Lines)

    $maxBlockLines = 12
    $selected = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -notmatch '\[FAIL\]') {
            continue
        }

        $last = [Math]::Min($Lines.Count - 1, $i + $maxBlockLines)
        $j = $i
        while ($j -le $last -and ($j -eq $i -or $Lines[$j] -notmatch 'Stack Trace:|Arborescence des appels|\[FAIL\]')) {
            $selected.Add($Lines[$j])
            $j++
        }
        $i = $j - 1
    }

    return $selected.ToArray()
}

# ── Step 2: Run tests ─────────────────────────────────────────────────────

$step++
if (-not $SkipTests) {
    Write-Host "[$step/$totalSteps] Running tests..." -ForegroundColor Yellow
    $testLog = Join-Path $ProjectRoot "Dist\logs\tests-$buildNumber.log"
    $testRun = Invoke-NativeLogged -LogPath $testLog -Command {
        # --verbosity quiet alone dropped the assertion messages: the failure was named and
        # never explained. The console logger at minimal writes each failure with its message.
        dotnet test $SolutionFile.FullName --verbosity quiet --nologo --logger "console;verbosity=minimal"
    }
    $testResult = $testRun.Lines
    if ($testRun.ExitCode -ne 0) {
        Write-Host "Tests FAILED. Aborting build." -ForegroundColor Red
        Select-TestFailureLines -Lines $testResult | ForEach-Object { Write-Host $_ }
        Write-Host "Full test output: $testLog" -ForegroundColor Yellow
        exit 1
    }
    $passedMatch = ($testResult | Select-String 'réussite\s*:\s*(\d+)|Passed\s*:\s*(\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value, $_.Matches[0].Groups[2].Value } | Where-Object { $_ }) -join '+'
    Write-Host "[$step/$totalSteps] Tests passed ($passedMatch)" -ForegroundColor Green
} else {
    Write-Host "[$step/$totalSteps] Tests skipped" -ForegroundColor DarkYellow
}

# ── Step 3: Build ─────────────────────────────────────────────────────────

$step++
Write-Host "[$step/$totalSteps] Building $Mode..." -ForegroundColor Yellow
dotnet build $AppProject -c $Mode --nologo --verbosity quiet @versionArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "[$step/$totalSteps] Build succeeded" -ForegroundColor Green

# ── Step 4: Check formatting ───────────────────────────────────────────────

$step++
if (-not $SkipTests) {
    Write-Host "[$step/$totalSteps] Checking code formatting..." -ForegroundColor Yellow
    dotnet format $SolutionFile.FullName --no-restore --verify-no-changes --verbosity diagnostic
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Formatting check FAILED. Run 'dotnet format $($SolutionFile.Name)' to fix." -ForegroundColor Red
        exit 1
    }
    Write-Host "[$step/$totalSteps] Formatting OK" -ForegroundColor Green
} else {
    Write-Host "[$step/$totalSteps] Format check skipped" -ForegroundColor DarkYellow
}

# ── Helper: publish one variant ───────────────────────────────────────────

function Remove-TreeRobust {
    <#
    .SYNOPSIS
        Deletes a directory tree, including paths Remove-Item cannot reach.
    .DESCRIPTION
        Remove-Item -Recurse fails with "the directory is not empty" on trees whose
        paths exceed MAX_PATH, and the bundled terminal assets carry node_modules
        trees that do. The failure is quiet in its consequences rather than its
        message: the stale folder survives, the next copy nests inside it, and the
        build after that fails harder. Mirroring an empty directory over the target
        works at any depth, so it is used first and Remove-Item only finishes the
        job on the emptied folder.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    $empty = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-empty-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $empty -Force | Out-Null
    try {
        & robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NC /NS /R:1 /W:1 | Out-Null
        # robocopy reports work done through its exit code; anything below 8 is
        # success. Cleared so a later $LASTEXITCODE check cannot read it as ours.
        $global:LASTEXITCODE = 0
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    }
    finally {
        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $Path) {
        throw "Could not remove '$Path'. A stale build folder would make the next copy nest inside it."
    }
}

function Copy-TreeContents {
    <#
    .SYNOPSIS
        Replaces a destination directory with the contents of a source directory.
    .DESCRIPTION
        Copy-Item -Recurse copies the source FOLDER into the destination when the
        destination already exists, so a second build produces Assets\Assets and a
        third produces Assets\Assets\Assets, duplicating whatever they contain.
        Copying the contents into a freshly emptied destination is idempotent: any
        number of runs leaves the same layout.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    Remove-TreeRobust -Path $Destination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item (Join-Path $Source '*') $Destination -Recurse -Force
}

function Publish-Variant {
    param([string]$VariantName)

    $suffix = if ($VariantName -eq 'SelfContained') { '_selfcontained' } else { '_standard' }
    if ($Variant -ne 'Both') { $suffix = '' }

    $variantFolder = "Heimdall_build.${buildNumber}${suffix}"
    $variantDir = Join-Path $distDir $variantFolder

    # Clean only this variant's output folder. dotnet publish overwrites files
    # but never removes stale ones, so a surviving folder accumulates cruft
    # across builds of the same version. Scoped to $variantDir, never $distDir,
    # so the auto-increment scan over sibling build folders stays intact.
    Remove-TreeRobust -Path $variantDir

    Write-Host "  Publishing $VariantName to $variantFolder..." -ForegroundColor Yellow
    # HeimdallEdition stamps the edition into the executable (AssemblyMetadata, read by
    # VariantDetector): the runtimes\webview2 folder alone cannot say which edition is
    # running, because a Standard install over a SelfContained one can leave it behind.
    dotnet publish $AppProject -c $Mode -o $variantDir --nologo --verbosity quiet --self-contained true -r win-x64 -p:PublishSingleFile=false "-p:HeimdallEdition=$VariantName" @versionArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publish FAILED." -ForegroundColor Red
        exit 1
    }

    # Copy config defaults and locales
    $configDest = Join-Path $variantDir 'config'
    $localesDest = Join-Path $variantDir 'locales'
    if (-not (Test-Path $configDest)) { New-Item -ItemType Directory -Path $configDest -Force | Out-Null }
    if (-not (Test-Path $localesDest)) { New-Item -ItemType Directory -Path $localesDest -Force | Out-Null }
    Copy-Item (Join-Path $ProjectRoot 'config\settings.default.json') $configDest -Force
    Copy-Item (Join-Path $ProjectRoot 'config\servers.default.json') $configDest -Force
    if (Test-Path (Join-Path $ProjectRoot 'locales\en.json')) {
        Copy-Item (Join-Path $ProjectRoot 'locales\en.json') $localesDest -Force
    }
    if (Test-Path (Join-Path $ProjectRoot 'locales\fr.json')) {
        Copy-Item (Join-Path $ProjectRoot 'locales\fr.json') $localesDest -Force
    }

    # Copy WebView2 native loader
    $wv2Loader = Join-Path $ProjectRoot 'src\Heimdall.App\lib\webview2\WebView2Loader.dll'
    if (Test-Path $wv2Loader) {
        Copy-Item $wv2Loader $variantDir -Force
    }

    # Copy terminal assets
    $assetsSrc = Join-Path $ProjectRoot 'src\Heimdall.App\Assets'
    $assetsDest = Join-Path $variantDir 'Assets'
    if (Test-Path $assetsSrc) {
        Copy-TreeContents -Source $assetsSrc -Destination $assetsDest
    }

    # Bundle WebView2 runtime for SelfContained edition only.
    # Copy the runtime *contents* into a freshly recreated destination so the
    # layout is always runtimes\webview2\<files>. Copying the source directory
    # itself with -Recurse nests it as a child (runtimes\webview2\webview2\...)
    # whenever the destination already exists, duplicating ~400 MB per level.
    if ($VariantName -eq 'SelfContained') {
        $wv2Dest = Join-Path $variantDir 'runtimes\webview2'
        Copy-TreeContents -Source $wv2Runtime -Destination $wv2Dest
        Write-Host "    + WebView2 Fixed Version Runtime bundled" -ForegroundColor DarkGray
    }

    # Create archive in Release mode
    if ($Mode -eq 'Release') {
        $zipPath = Join-Path $distDir "${variantFolder}.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path $variantDir -DestinationPath $zipPath -CompressionLevel Optimal
        Write-Host "    + Archive: $zipPath" -ForegroundColor DarkGray
    }

    return $variantDir
}

# ── Step 5: Publish all variants ──────────────────────────────────────────

$step++
Write-Host "[$step/$totalSteps] Publishing ($($variants -join ' + '))..." -ForegroundColor Yellow

$outputs = @()
foreach ($v in $variants) {
    $dir = Publish-Variant -VariantName $v
    $outputs += @{ Name = $v; Dir = $dir }
}

Write-Host "[$step/$totalSteps] Published" -ForegroundColor Green

# ── Step 6: Installers (Release mode only) ────────────────────────────────

if ($Mode -eq 'Release') {
    $step++
    Write-Host "[$step/$totalSteps] Creating installers..." -ForegroundColor Yellow

    $installerDir = Join-Path $ProjectRoot 'Dist\installers'
    if (-not (Test-Path $installerDir)) { New-Item -ItemType Directory -Path $installerDir -Force | Out-Null }

    $issFile = Join-Path $ProjectRoot 'installer\innosetup.iss'
    $defaultIscc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    $iscc = if ([string]::IsNullOrWhiteSpace($env:HEIMDALL_ISCC_PATH)) {
        $defaultIscc
    } else {
        $env:HEIMDALL_ISCC_PATH
    }
    if ((Test-Path $issFile) -and (Test-Path $iscc)) {
        foreach ($o in $outputs) {
            Write-Host "  Building Inno Setup installer ($($o.Name))..." -ForegroundColor DarkGray
            $compile = Invoke-InnoSetupCompile -IsccPath $iscc -ScriptPath $issFile `
                -AppVersion $buildNumber -Variant $o.Name -SourceDir $o.Dir
            if ($compile.ExitCode -eq 0) {
                foreach ($line in $compile.Lines) { Write-Host "      $line" -ForegroundColor DarkGray }
                Write-Host "    + Heimdall_${buildNumber}_$($o.Name)_Setup.exe" -ForegroundColor DarkGray
            } elseif ($Mode -eq 'Release') {
                # The compiler says which line of the script it stopped on: show it.
                foreach ($line in $compile.Lines) { Write-Host "      $line" -ForegroundColor Red }
                # Fatal, like a WiX failure below. The Setup.exe is the only asset the
                # in-app updater can use: a release published without it leaves every
                # user without in-app updating until the next one.
                Write-Host "    [!] Inno Setup failed for $($o.Name) - a Release build cannot publish without its installer" -ForegroundColor Red
                exit 1
            } else {
                foreach ($line in $compile.Lines) { Write-Host "      $line" -ForegroundColor DarkYellow }
                Write-Host "    [!] Inno Setup failed for $($o.Name)" -ForegroundColor DarkYellow
            }
        }
    } elseif ($Mode -eq 'Release') {
        Write-Host "  [!] Inno Setup compiler not found - a Release build cannot publish without its installer" -ForegroundColor Red
        Write-Host "      Install Inno Setup 6, or set `$env:HEIMDALL_ISCC_PATH if ISCC.exe is installed elsewhere." -ForegroundColor Yellow
        Write-Host "      Checked: $iscc" -ForegroundColor Red
        exit 1
    } else {
        Write-Host "  [!] Inno Setup compiler not found - skipping .exe installer generation" -ForegroundColor DarkYellow
        Write-Host "      Install Inno Setup 6, or set `$env:HEIMDALL_ISCC_PATH if ISCC.exe is installed elsewhere." -ForegroundColor Yellow
        Write-Host "      Checked: $iscc" -ForegroundColor DarkYellow
    }

    $wixProduct = Join-Path $ProjectRoot 'installer\Product.wxs'
    $wixCommand = Get-Command wix -ErrorAction SilentlyContinue
    $wix = if ([string]::IsNullOrWhiteSpace($env:HEIMDALL_WIX_PATH)) {
        if ($null -eq $wixCommand) { $null } else { $wixCommand.Source }
    } else {
        $env:HEIMDALL_WIX_PATH
    }
    if (-not (Test-Path $wixProduct)) {
        Write-Host "  [!] WiX product definition not found: $wixProduct" -ForegroundColor Red
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($wix) -or -not (Test-Path $wix)) {
        Write-Host "  [!] WiX CLI not found. Install WiX 6, or set `$env:HEIMDALL_WIX_PATH." -ForegroundColor Red
        exit 1
    }

    $msiSource = $outputs |
        Where-Object { $_.Name -eq 'Standard' } |
        Select-Object -First 1
    if ($null -eq $msiSource) {
        $msiSource = $outputs | Select-Object -First 1
    }

    $msiPath = Join-Path $installerDir "Heimdall_${buildNumber}.msi"
    Write-Host "  Building WiX MSI from $($msiSource.Name)..." -ForegroundColor DarkGray
    Push-Location (Split-Path $wixProduct -Parent)
    try {
        & $wix build `
            -arch x64 `
            -d "AppVersion=$msiVersion" `
            -d "SourceDir=$($msiSource.Dir)" `
            -o $msiPath `
            -pdbtype none `
            $wixProduct `
            -ext WixToolset.UI.wixext
        if ($LASTEXITCODE -ne 0) {
            Write-Host "    [!] WiX MSI build failed" -ForegroundColor Red
            exit 1
        }
    } finally {
        Pop-Location
    }
    Write-Host "    + Heimdall_${buildNumber}.msi (ProductVersion $msiVersion)" -ForegroundColor DarkGray

    Write-Host "[$step/$totalSteps] Installers done" -ForegroundColor Green
}

# ── Summary ───────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build complete: v${buildNumber} ($Mode)" -ForegroundColor Cyan
foreach ($o in $outputs) {
    $size = [math]::Round((Get-ChildItem $o.Dir -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB, 0)
    Write-Host "  $($o.Name): $($o.Dir) (~${size} MB)" -ForegroundColor Cyan
}
if ($Mode -eq 'Release') {
    $installerDir = Join-Path $ProjectRoot 'Dist\installers'
    if (Test-Path $installerDir) {
        # Through the same rule as the publish list below: this line says "Installer",
        # and a generated release-notes file left in the directory is not one.
        $summaryFiles = @(Get-ChildItem $installerDir -File -Filter "*${buildNumber}*")
        foreach ($file in (Select-PublishArtifact -Candidate $summaryFiles)) {
            $sz = [math]::Round($file.Length / 1MB, 0)
            Write-Host "  Installer: $($file.Name) (~${sz} MB)" -ForegroundColor Cyan
        }
    }
}
Write-Host "========================================" -ForegroundColor Cyan

# ── Publish to GitHub (Release mode + -Publish flag) ──────────────────────

if (($Publish -or $DryRun) -and $Mode -eq 'Release') {
    $isDry = $DryRun.IsPresent
    $label = if ($isDry) { "DRY-RUN" } else { "Publish" }

    Write-Host ""
    Write-Host "[$label] Creating GitHub release v${buildNumber}..." -ForegroundColor $(if ($isDry) { 'Magenta' } else { 'Yellow' })

    # Collect all release artifacts
    $artifacts = @()
    foreach ($o in $outputs) {
        $suffix = if ($o.Name -eq 'SelfContained') { '_selfcontained' } else { '_standard' }
        if ($Variant -ne 'Both') { $suffix = '' }
        $zipPath = Join-Path $distDir "Heimdall_build.${buildNumber}${suffix}.zip"
        if (Test-Path $zipPath) { $artifacts += $zipPath }
    }
    $installerDir = Join-Path $ProjectRoot 'Dist\installers'
    if (Test-Path $installerDir) {
        # Not everything carrying the build number is an asset: this run writes its
        # release notes into the same directory, so a second publish of one build
        # number would upload the release body as a downloadable file. The rule lives
        # in scripts/PublishArtifactSelection.ps1 and is tested there.
        $candidates = @(Get-ChildItem $installerDir -File -Filter "*${buildNumber}*")
        foreach ($file in (Select-PublishArtifact -Candidate $candidates)) {
            $artifacts += $file.FullName
        }
    }

    if ($artifacts.Count -eq 0) {
        Write-Host "[!] No artifacts found for release." -ForegroundColor Red
        exit 1
    }

    Write-Host "[$label] Artifacts ($($artifacts.Count)):" -ForegroundColor DarkGray
    foreach ($a in $artifacts) {
        $sz = [math]::Round((Get-Item $a).Length / 1MB, 0)
        Write-Host "    $(Split-Path $a -Leaf) (~${sz} MB)" -ForegroundColor DarkGray
    }

    # Compute SHA-256 checksums so published downloads can be verified. The
    # checksum lines are derived from the real build outputs (installers/zips)
    # only, never from the checksum file itself. The file is published as an
    # additional release asset and the same lines are embedded in the notes.
    $checksumLines = @(foreach ($a in $artifacts) {
        $hash = (Get-FileHash -Path $a -Algorithm SHA256).Hash.ToLowerInvariant()
        '{0}  {1}' -f $hash, (Split-Path $a -Leaf)
    })
    # A dry run generates the same two files a real publish does - the checksum
    # list and the release notes - but into a temp directory instead of
    # Dist\installers, so the simulation leaves the build output untouched.
    # Redirected rather than suppressed on purpose: both paths appear in the
    # simulated `gh release create` printed below, so suppressing either would
    # leave that command naming a file that does not exist, and the operator
    # could no longer inspect what would actually be published.
    $generatedDir = if ($isDry) {
        $dryRunDir = Join-Path ([System.IO.Path]::GetTempPath()) "heimdall-dryrun-v${buildNumber}"
        if (-not (Test-Path $dryRunDir)) { New-Item -ItemType Directory -Path $dryRunDir -Force | Out-Null }
        $dryRunDir
    } else {
        $installerDir
    }

    $checksumFile = Join-Path $generatedDir 'SHA256SUMS.txt'
    # LF line endings with a single trailing newline, matching the standard
    # sha256sum format consumed by `sha256sum -c`.
    [System.IO.File]::WriteAllText($checksumFile, (($checksumLines -join "`n") + "`n"))
    $checksumLabel = if ($isDry) { $checksumFile } else { Split-Path $checksumFile -Leaf }
    Write-Host "[$label] Wrote $checksumLabel ($($checksumLines.Count) entries)." -ForegroundColor DarkGray

    # Build release notes
    $notes = "## Heimdall v${buildNumber}`n`n"

    # Resolve the hand-written notes once. This block only translates the pure
    # decision into the publish path's messages and exit behaviour.
    $releaseNotesResolution = Resolve-ReleaseNotes `
        -ProjectRoot $ProjectRoot `
        -BuildNumber $buildNumber `
        -ReleaseNotesFile $ReleaseNotesFile `
        -AllowAutoNotesOnly:$AllowAutoNotesOnly
    $customNotesPath = $releaseNotesResolution.Path

    switch ($releaseNotesResolution.Decision) {
        'UseNotes' { }
        'Abort' {
            if ($releaseNotesResolution.Source -eq 'Explicit') {
                Write-Host "[!] -ReleaseNotesFile not found: $($releaseNotesResolution.CandidatePath)" -ForegroundColor Red
            } else {
                Write-Host "[!] Required hand-written release notes not found: $($releaseNotesResolution.CandidatePath)" -ForegroundColor Red
                Write-Host "    Create that file, pass -ReleaseNotesFile, or pass -AllowAutoNotesOnly to override. Refusing to publish." -ForegroundColor Red
            }
            exit 1
        }
        'AutoOnly' {
            Write-Host "[$label] WARNING: no hand-written release notes at $($releaseNotesResolution.CandidatePath)." -ForegroundColor Yellow
            Write-Host "    Publishing with auto-generated notes only because -AllowAutoNotesOnly was passed." -ForegroundColor Yellow
        }
        default {
            Write-Host "[!] Unexpected release-notes decision: $($releaseNotesResolution.Decision)" -ForegroundColor Red
            exit 1
        }
    }
    if ($customNotesPath) {
        # Typography guard: refuse to publish notes containing any character the
        # French AZERTY keyboard cannot produce. French accents are allowed; the
        # French guillemets U+00AB / U+00BB are NOT (decision 2026-07-26: they are
        # absent from the layout). Fail-closed unless -AllowNonAsciiNotes is set.
        $noteViolations = @(Get-NotesTypographyViolations -Path $customNotesPath)
        if ($noteViolations.Count -gt 0) {
            if ($AllowNonAsciiNotes) {
                Write-Host "[$label] WARNING: release notes contain $($noteViolations.Count) banned character(s); publishing anyway (-AllowNonAsciiNotes)." -ForegroundColor Yellow
                foreach ($v in $noteViolations) {
                    Write-Host ("    {0}:{1}:{2}  '{3}' {4} ({5}) - {6}" -f (Split-Path $v.File -Leaf), $v.Line, $v.Column, $v.Char, $v.CodePoint, $v.Name, $v.Remedy) -ForegroundColor DarkYellow
                }
            } else {
                Write-Host "[!] Release notes contain characters that cannot be typed on the French AZERTY keyboard." -ForegroundColor Red
                foreach ($v in $noteViolations) {
                    Write-Host ("    {0}:{1}:{2}  '{3}' {4} ({5}) - {6}" -f $v.File, $v.Line, $v.Column, $v.Char, $v.CodePoint, $v.Name, $v.Remedy) -ForegroundColor Red
                }
                Write-Host "    Fix the notes file, or pass -AllowNonAsciiNotes to override. Refusing to publish." -ForegroundColor Red
                exit 1
            }
        }
        $customNotes = [System.IO.File]::ReadAllText($customNotesPath)
        $notes += $customNotes.TrimEnd() + "`n`n"
        Write-Host "[$label] Prepended custom notes from $(Split-Path $customNotesPath -Leaf)." -ForegroundColor DarkGray
    }

    # An -AcceptCiFailure override is recorded where every reader of the release sees it.
    if ($null -ne $releaseGateOverride) {
        $notes += Get-ReleaseGateOverrideNotes -Reason $releaseGateOverride.Reason -Failures $releaseGateOverride.Failures
    }

    # The informational lanes' result for this commit, when there is anything to say.
    $notes += $informationalLaneNotes

    $notes += "### Downloads`n`n"
    $notes += "| File | Description |`n|------|-------------|`n"
    foreach ($a in $artifacts) {
        $name = Split-Path $a -Leaf
        $sz = [math]::Round((Get-Item $a).Length / 1MB, 0)
        $desc = if ($name -match 'selfcontained.*\.zip') { "Portable, bundled WebView2 (~${sz} MB)" }
                elseif ($name -match 'standard.*\.zip') { "Portable, requires Edge/WebView2 (~${sz} MB)" }
                elseif ($name -match 'SelfContained.*Setup') { "Installer, bundled WebView2 (~${sz} MB)" }
                elseif ($name -match 'Standard.*Setup') { "Installer, requires Edge/WebView2 (~${sz} MB)" }
                elseif ($name -match '\.msi$') { "Managed deployment only (GPO/SCCM), needs an administrator and is not updated in place (~${sz} MB)" }
                else { "~${sz} MB" }
        $notes += "| ``$name`` | $desc |`n"
    }

    # Embed the SHA-256 checksums and publish the checksum file as a release asset.
    $fence = '```'
    $notes += "`n### Checksums (SHA-256)`n`n"
    $notes += "$fence`n"
    $notes += (($checksumLines -join "`n") + "`n")
    $notes += "$fence`n"
    $artifacts += $checksumFile

    $notesFile = Join-Path $generatedDir "RELEASE_NOTES_v${buildNumber}.md"
    [System.IO.File]::WriteAllText($notesFile, $notes, [System.Text.UTF8Encoding]::new($false))
    $notesLabel = if ($isDry) { $notesFile } else { Split-Path $notesFile -Leaf }
    Write-Output "[$label] Wrote $notesLabel."

    Write-Host "[$label] Release notes:" -ForegroundColor DarkGray
    Write-Host $notes -ForegroundColor DarkGray

    if ($isDry) {
        Write-Output ""
        Write-Output "[$label] Would run: git add $AppProject"
        Write-Output "[$label] Would run: git status --porcelain -- $AppProject"
        Write-Output "[$label] Would run: git commit -m `"release: v${buildNumber}`" (if version file changed)"
        Write-Output "[$label] Would run: git push"
        $dryCreateArgs = Get-ReleaseCreateArgs -BuildNumber $buildNumber -Artifacts $artifacts -NotesFile $notesFile `
            -TargetSha '<sha of the release commit>'
        Write-Output "[$label] Would run: gh $($dryCreateArgs -join ' ')"
        Write-Output ""
        Write-Output "[$label] Dry run complete. No changes made to git or GitHub."
    } else {
        # Commit version bump + push
        # git writes progress to stderr which PowerShell treats as a terminating
        # error under $ErrorActionPreference = 'Stop'. Temporarily relax to
        # Continue so $LASTEXITCODE is the sole success indicator.
        Write-Host "[$label] Committing version bump..." -ForegroundColor DarkGray
        $prevEAP = $ErrorActionPreference
        $prevGitPrompt = $env:GIT_TERMINAL_PROMPT
        $ErrorActionPreference = 'Continue'
        $env:GIT_TERMINAL_PROMPT = '0'
        try {
            $gitAddOut = & git add $AppProject 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Output "[!] Git add failed."
                $gitAddOut | ForEach-Object { Write-Output $_ }
                exit 1
            }

            $gitStatusOut = & git status --porcelain -- $AppProject 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Output "[!] Git status failed."
                $gitStatusOut | ForEach-Object { Write-Output $_ }
                exit 1
            }

            if ($gitStatusOut) {
                $commitOut = & git commit -m "release: v${buildNumber}" 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Output "[!] Git commit failed."
                    $commitOut | ForEach-Object { Write-Output $_ }
                    exit 1
                }
            } else {
                Write-Output "[$label] No version bump changes to commit."
            }

            $pushOut = & git push 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Output "[!] Git push failed. Aborting release creation."
                $pushOut | ForEach-Object { Write-Output $_ }
                exit 1
            }
            Write-Output "[$label] Pushed to remote."

            # The tag must point at the commit this run built and pushed, not at
            # whatever the default branch is when GitHub processes the call.
            $releaseShaOut = & git rev-parse HEAD 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Output "[!] Cannot read the release commit SHA."
                $releaseShaOut | ForEach-Object { Write-Output $_ }
                exit 1
            }
            $releaseSha = ([string]($releaseShaOut | Select-Object -First 1)).Trim()
        } finally {
            $ErrorActionPreference = $prevEAP
            if ($null -eq $prevGitPrompt) {
                Remove-Item Env:\GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue
            } else {
                $env:GIT_TERMINAL_PROMPT = $prevGitPrompt
            }
        }

        # Create or update the release.
        $prevEAP = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & gh release view "v$buildNumber" *> $null
            $releaseExists = ($LASTEXITCODE -eq 0)

            if ($releaseExists) {
                $uploadOut = & gh release upload "v$buildNumber" @artifacts --clobber 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Output "[!] GitHub release asset upload failed."
                    $uploadOut | ForEach-Object { Write-Output $_ }
                    exit 1
                }

                $editOut = & gh release edit "v$buildNumber" --notes-file $notesFile 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Output "[!] GitHub release notes update failed."
                    $editOut | ForEach-Object { Write-Output $_ }
                    exit 1
                }
            } else {
                $createArgs = Get-ReleaseCreateArgs -BuildNumber $buildNumber -Artifacts $artifacts -NotesFile $notesFile `
                    -TargetSha $releaseSha
                $createOut = & gh @createArgs 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Output "[!] GitHub release creation failed."
                    $createOut | ForEach-Object { Write-Output $_ }
                    exit 1
                }
            }
        } finally {
            $ErrorActionPreference = $prevEAP
        }

        Write-Output "[$label] Release published: https://github.com/VBlackJack/Heimdall/releases/tag/v${buildNumber}"
    }
}
