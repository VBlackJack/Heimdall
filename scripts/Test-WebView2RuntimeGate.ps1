<#
.SYNOPSIS
    Tests the WebView2 runtime gate (scripts/WebView2RuntimeGate.ps1) and the CAB
    installer that feeds it (scripts/Update-WebView2FixedRuntime.ps1).

.DESCRIPTION
    The SelfContained edition shipped a Fixed Version Runtime seven months out of date
    because nothing aged it, and a Release without that runtime dropped the edition in
    silence. Each case below would pass on one of those two defects if it were absent:

      1. No SelfContained variant requested                -> NotRequired, whatever
                                                              the folder holds.
      2. Release, SelfContained, no runtime                -> Abort (C-04). It used to
                                                              fall back to Standard.
      3. Debug, SelfContained, no runtime                  -> FallBackToStandard, the
                                                              old behaviour kept for
                                                              local builds.
      4. Release, runtime with no manifest, or a manifest  -> Stale (a release-gate
         without runtimeFileUtc                               finding Build.ps1 aborts
                                                              on unless overridden).
      5. Release, runtime built three days ago             -> Ready.
      6. Release, runtime one day past the limit           -> Stale, and the limit is
                                                              exactly the named
                                                              constant (one day inside
                                                              it is Ready).
      7. The age is the runtime's, not the install's       -> an old runtime installed
                                                              today is Stale.
      8. Release, runtime dated in the future              -> Stale.
      9. Manifest text written by the installer            -> read back by the gate.
     10. Signer rule on fixture signatures                 -> only a valid embedded
                                                              Microsoft signature.
     11. Installer on an unsigned CAB                      -> refuses, leaves the
                                                              target folder untouched.
     12. Installer on a CAB holding a Microsoft-signed     -> installs, records the
         msedgewebview2.exe (when one exists on this box)     exe's FileVersion and
                                                              its timestamp from the
                                                              CAB; an old one is Stale
                                                              the day it is installed.
     13. Build.ps1 decides through the gate, before the   -> the gate is wired in,
         build, has no stale-runtime switch, and no          and -AcceptCiFailure is
         longer names Setup-WebView2.ps1                      its only override.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-WebView2RuntimeGate.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'WebView2RuntimeGate.ps1')

$failures = 0
$now = [datetime]::new(2026, 10, 27, 12, 0, 0, [System.DateTimeKind]::Utc)
$installer = Join-Path $PSScriptRoot 'Update-WebView2FixedRuntime.ps1'
# A cabinet stores file times in DOS format, to two seconds.
$cabTimestampToleranceSeconds = 2

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures++
    }
}

function New-Sandbox {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-wv2gate-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function New-RuntimeFixture {
    param([string]$Root, [AllowNull()] [string]$ManifestJson)
    $runtime = Join-Path $Root 'runtimes\webview2'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $runtime 'msedgewebview2.exe'), 'fixture')
    if ($null -ne $ManifestJson) {
        [System.IO.File]::WriteAllText((Join-Path $runtime 'heimdall-runtime.json'), $ManifestJson)
    }
    return $runtime
}

function New-Manifest {
    param([datetime]$RuntimeFileUtc, [datetime]$InstalledUtc = $now)
    return (New-WebView2RuntimeManifestJson -FileVersion '150.0.1.2' -RuntimeFileUtc $RuntimeFileUtc -CabSha256 'ABCDEF' -InstalledUtc $InstalledUtc)
}

$both = @('Standard', 'SelfContained')
$sandbox = New-Sandbox
try {
    $empty = Join-Path $sandbox 'empty\runtimes\webview2'

    # 1.
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $empty -Mode Release -Variants @('Standard') -NowUtc $now
    Assert-True ($gate.Decision -eq 'NotRequired' -and @($gate.Variants).Count -eq 1) "Standard only needs no runtime ($($gate.Decision))"

    # 2.
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $empty -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Abort') "a Release asked for SelfContained with no runtime aborts ($($gate.Decision))"
    Assert-True ((@($gate.Messages) -join ' ') -match 'Update-WebView2FixedRuntime\.ps1') 'the message names the script that installs a runtime'
    Assert-True ((@($gate.Messages) -join ' ') -match '-Variant Standard') 'the message names the explicit way to build without it'
    Assert-True (-not ((@($gate.Messages) -join ' ') -match 'Setup-WebView2')) 'the message no longer sends the operator to Setup-WebView2.ps1'

    # 3.
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $empty -Mode Debug -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'FallBackToStandard' -and (@($gate.Variants) -join ',') -eq 'Standard') "a Debug build falls back to Standard ($($gate.Decision): $(@($gate.Variants) -join ','))"

    # 4.
    $runtime = New-RuntimeFixture -Root (Join-Path $sandbox 'nomanifest') -ManifestJson $null
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $runtime -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale' -and @($gate.Variants).Count -eq 2) "a runtime with no manifest is a release-gate finding ($($gate.Decision))"
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $runtime -Mode Debug -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Ready') "a Debug build does not age the runtime ($($gate.Decision))"
    $broken = New-RuntimeFixture -Root (Join-Path $sandbox 'broken') -ManifestJson '{ not json'
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $broken -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale') "an unreadable manifest is a finding ($($gate.Decision))"
    $installOnly = New-RuntimeFixture -Root (Join-Path $sandbox 'installonly') -ManifestJson '{ "version": "146.0.3856.62", "installedUtc": "2026-10-26T00:00:00Z" }'
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $installOnly -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale') "a manifest dating only the install, not the runtime, is a finding ($($gate.Decision))"

    # 5.
    $fresh = New-RuntimeFixture -Root (Join-Path $sandbox 'fresh') -ManifestJson (New-Manifest -RuntimeFileUtc $now.AddDays(-3))
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $fresh -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Ready' -and @($gate.Variants).Count -eq 2) "a runtime built three days ago is Ready ($($gate.Decision))"

    # 6. Both sides of the limit, so a changed constant or comparison shows.
    Assert-True ($script:MaxWebView2RuntimeAgeDays -eq 60) "the age limit is 60 days ($script:MaxWebView2RuntimeAgeDays)"
    $inside = New-RuntimeFixture -Root (Join-Path $sandbox 'inside') -ManifestJson (New-Manifest -RuntimeFileUtc $now.AddDays(-($script:MaxWebView2RuntimeAgeDays - 1)))
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $inside -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Ready') "one day inside the limit is Ready ($($gate.Decision))"
    $stale = New-RuntimeFixture -Root (Join-Path $sandbox 'stale') -ManifestJson (New-Manifest -RuntimeFileUtc $now.AddDays(-($script:MaxWebView2RuntimeAgeDays + 1)))
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $stale -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale') "one day past the limit is Stale ($($gate.Decision))"
    Assert-True ((@($gate.Messages) -join ' ') -match '61 days ago') "the finding says how old the runtime is ($(@($gate.Messages)[0]))"

    # 7. Installed this morning, built seven months ago: stale.
    $oldButNew = New-RuntimeFixture -Root (Join-Path $sandbox 'oldbutnew') -ManifestJson (New-Manifest -RuntimeFileUtc $now.AddDays(-226) -InstalledUtc $now.AddHours(-2))
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $oldButNew -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale') "an old runtime installed today is Stale ($($gate.Decision))"

    # 8.
    $future = New-RuntimeFixture -Root (Join-Path $sandbox 'future') -ManifestJson (New-Manifest -RuntimeFileUtc $now.AddDays(2))
    $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $future -Mode Release -Variants $both -NowUtc $now
    Assert-True ($gate.Decision -eq 'Stale') "a runtime dated in the future is a finding ($($gate.Decision))"

    # 9.
    $read = Read-WebView2RuntimeManifest -RuntimeDirectory $fresh
    Assert-True ($null -ne $read -and $read.FileVersion -eq '150.0.1.2' -and $read.CabSha256 -eq 'abcdef') 'the manifest the installer writes reads back'
    Assert-True ($null -ne $read -and $read.RuntimeFileUtc -eq $now.AddDays(-3)) "runtimeFileUtc round-trips in UTC ($(if ($read) { $read.RuntimeFileUtc.ToString('o') }))"
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
# 10. The signer rule, on fixtures shaped like Get-AuthenticodeSignature's result.
function New-Signature {
    param([string]$Status, [string]$Type, [AllowNull()] [string]$Subject)
    $cert = if ($null -eq $Subject) { $null } else { [pscustomobject]@{ Subject = $Subject } }
    return [pscustomobject]@{ Status = $Status; SignatureType = $Type; SignerCertificate = $cert }
}
$msSubject = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
Assert-True (Test-WebView2RuntimeSigner -Signature (New-Signature 'Valid' 'Authenticode' $msSubject)) 'a valid embedded Microsoft signature is trusted'
Assert-True (-not (Test-WebView2RuntimeSigner -Signature (New-Signature 'Valid' 'Authenticode' 'CN=Contoso, O=Contoso Ltd'))) 'a valid signature by someone else is refused'
Assert-True (-not (Test-WebView2RuntimeSigner -Signature (New-Signature 'HashMismatch' 'Authenticode' $msSubject))) 'a tampered file is refused'
Assert-True (-not (Test-WebView2RuntimeSigner -Signature (New-Signature 'NotSigned' 'None' $null))) 'an unsigned file is refused'
Assert-True (-not (Test-WebView2RuntimeSigner -Signature (New-Signature 'Valid' 'Catalog' $msSubject))) 'a catalog signature is refused'
Assert-True (-not (Test-WebView2RuntimeSigner -Signature $null)) 'no signature at all is refused'

# 11 and 12. The installer end to end, on CABs built here with makecab.
function New-Cab {
    <#
    .SYNOPSIS
        Builds a real cabinet laid out like Microsoft's: one folder named after the
        runtime, with msedgewebview2.exe at its root. A bare `makecab src dest` makes a
        single compressed file instead, and expand.exe restores a one-file cabinet under
        the CAB's own name, so a companion file keeps the fixture a real multi-file one.
    #>
    param([string]$SourceFile, [string]$Directory)
    $cabName = 'Microsoft.WebView2.FixedVersionRuntime.fixture.x64.cab'
    $ddf = Join-Path $Directory 'fixture.ddf'
    $companion = Join-Path $Directory 'resources.pak'
    [System.IO.File]::WriteAllText($companion, 'fixture companion')
    $lines = @(
        ('.Set CabinetNameTemplate=' + $cabName),
        ('.Set DiskDirectoryTemplate=' + $Directory),
        '.Set CompressionType=MSZIP',
        # Without it makecab splits anything over a 1.44 MB floppy into several cabinets.
        '.Set MaxDiskSize=0',
        '.Set Cabinet=on',
        '.Set Compress=on',
        '.Set DestinationDir=Microsoft.WebView2.FixedVersionRuntime.fixture.x64',
        ('"{0}"' -f $SourceFile),
        ('"{0}"' -f $companion)
    )
    [System.IO.File]::WriteAllLines($ddf, $lines)
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    Push-Location $Directory
    try {
        & makecab.exe /F $ddf 2>&1 | Out-Null
        $code = $LASTEXITCODE
    } finally {
        Pop-Location
        $ErrorActionPreference = $prevEAP
    }
    $cab = Join-Path $Directory $cabName
    if ($code -ne 0 -or -not (Test-Path -LiteralPath $cab)) { throw "makecab failed ($code)" }
    return $cab
}
function Invoke-Installer {
    param([string]$Cab, [string]$Target)
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -CabPath $Cab -RuntimeDirectory $Target 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    return [pscustomobject]@{ ExitCode = $code; Output = (@($out) -join "`n") }
}

if (-not (Get-Command makecab.exe -ErrorAction SilentlyContinue)) {
    Write-Host 'SKIP: makecab.exe not found; the installer cases need it' -ForegroundColor Yellow
} else {
    $sandbox = New-Sandbox
    try {
        $staging = Join-Path $sandbox 'staging'
        New-Item -ItemType Directory -Path $staging -Force | Out-Null
        $unsigned = Join-Path $staging 'msedgewebview2.exe'
        [System.IO.File]::WriteAllText($unsigned, 'not a signed executable')
        $cab = New-Cab -SourceFile $unsigned -Directory $sandbox

        $target = Join-Path $sandbox 'repo\runtimes\webview2'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $target 'sentinel.txt'), 'previous runtime')

        $run = Invoke-Installer -Cab $cab -Target $target
        Assert-True ($run.ExitCode -ne 0) "the installer refuses an unsigned runtime (exit $($run.ExitCode))"
        Assert-True ($run.Output -match 'not validly signed by Microsoft') 'the refusal says why'
        Assert-True (Test-Path -LiteralPath (Join-Path $target 'sentinel.txt')) 'a refused CAB leaves the installed runtime in place'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $target 'heimdall-runtime.json'))) 'a refused CAB writes no manifest'

        $missing = Invoke-Installer -Cab (Join-Path $sandbox 'absent.cab') -Target $target
        Assert-True ($missing.ExitCode -ne 0 -and $missing.Output -match 'developer\.microsoft\.com') 'a missing CAB fails and says where to download one'

        $evergreen = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\EdgeWebView\Application'
        $signedExe = @(Get-ChildItem -LiteralPath $evergreen -Recurse -Filter 'msedgewebview2.exe' -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($signedExe.Count -eq 0) {
            Write-Host 'SKIP: no Microsoft-signed msedgewebview2.exe on this machine for the positive case' -ForegroundColor Yellow
        } else {
            $signedStaging = Join-Path $sandbox 'signed'
            New-Item -ItemType Directory -Path $signedStaging -Force | Out-Null
            $stagedExe = Join-Path $signedStaging 'msedgewebview2.exe'
            Copy-Item -LiteralPath $signedExe[0].FullName -Destination $stagedExe

            # 12a. A recent runtime: the manifest dates it by its own timestamp.
            $builtUtc = [datetime]::UtcNow.AddDays(-3)
            (Get-Item -LiteralPath $stagedExe).LastWriteTimeUtc = $builtUtc
            $signedCab = Join-Path $sandbox 'signedcab'
            New-Item -ItemType Directory -Path $signedCab -Force | Out-Null
            $cab = New-Cab -SourceFile $stagedExe -Directory $signedCab

            $run = Invoke-Installer -Cab $cab -Target $target
            Assert-True ($run.ExitCode -eq 0) "the installer accepts a Microsoft-signed runtime (exit $($run.ExitCode))"
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $target 'sentinel.txt'))) 'the previous runtime is replaced, not merged'
            Assert-True (Test-Path -LiteralPath (Join-Path $target 'msedgewebview2.exe')) 'the runtime executable sits at the folder root'
            $manifest = Read-WebView2RuntimeManifest -RuntimeDirectory $target
            $expectedSha = (Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash.ToLowerInvariant()
            Assert-True ($null -ne $manifest -and $manifest.FileVersion -eq $signedExe[0].VersionInfo.FileVersion) "the manifest records the executable's FileVersion ($(if ($manifest) { $manifest.FileVersion }))"
            Assert-True ($null -ne $manifest -and $manifest.CabSha256 -eq $expectedSha) 'the manifest records the CAB hash'
            $drift = if ($manifest) { [Math]::Abs(($manifest.RuntimeFileUtc - $builtUtc).TotalSeconds) } else { [double]::MaxValue }
            Assert-True ($drift -le $cabTimestampToleranceSeconds) "runtimeFileUtc is the executable's timestamp from the CAB, not the install time (off by $([Math]::Round($drift, 1)) s)"
            $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $target -Mode Release -Variants $both
            Assert-True ($gate.Decision -eq 'Ready') "a recent runtime just installed passes the gate ($($gate.Decision))"

            # 12b. An old runtime installed today is still old.
            (Get-Item -LiteralPath $stagedExe).LastWriteTimeUtc = [datetime]::UtcNow.AddDays(-($script:MaxWebView2RuntimeAgeDays + 30))
            $oldCab = Join-Path $sandbox 'oldcab'
            New-Item -ItemType Directory -Path $oldCab -Force | Out-Null
            $cab = New-Cab -SourceFile $stagedExe -Directory $oldCab
            $run = Invoke-Installer -Cab $cab -Target $target
            Assert-True ($run.ExitCode -eq 0) "the installer installs an old runtime too (exit $($run.ExitCode))"
            $gate = Resolve-WebView2RuntimeGate -RuntimeDirectory $target -Mode Release -Variants $both
            Assert-True ($gate.Decision -eq 'Stale') "an old runtime installed a moment ago is Stale ($($gate.Decision))"        }
    } finally {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 13. Wiring.
$buildScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1'
$buildText = Get-Content -LiteralPath $buildScript -Raw
$buildLines = @(Get-Content -LiteralPath $buildScript)
Assert-True ($buildText -match "WebView2RuntimeGate\.ps1") 'Build.ps1 dot-sources the gate'
Assert-True (@($buildLines | Where-Object { $_ -match 'Resolve-WebView2RuntimeGate\s' }).Count -eq 1) 'Build.ps1 decides through Resolve-WebView2RuntimeGate once'
Assert-True (-not ($buildText -match 'AllowStaleWebView2Runtime|AllowStale')) 'Build.ps1 has no stale-runtime switch of its own'
Assert-True ($buildText -match "'Stale'\s*\{" -and $buildText -match '\$releaseGateFailures \+= @\(\$wv2Gate\.Messages\)') 'a stale runtime becomes a release-gate finding'
Assert-True (-not ($buildText -match 'Setup-WebView2\.ps1 first')) 'Build.ps1 no longer sends the operator to Setup-WebView2.ps1'
$gateIndex = $buildText.IndexOf('Resolve-WebView2RuntimeGate -RuntimeDirectory')
$testsIndex = $buildText.IndexOf('Running tests...')
Assert-True ($gateIndex -ge 0 -and $testsIndex -gt $gateIndex) 'the gate runs before the test step, so a refusal costs nothing'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: a Release bundles a signed, recorded, recent WebView2 runtime or says why it will not.' -ForegroundColor Green
exit 0
