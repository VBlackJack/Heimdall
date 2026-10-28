<#
.SYNOPSIS
    Tests the downloaded-installer helpers (scripts/VerifiedInstaller.ps1) and that
    Setup-WebView2.ps1 uses them.

.DESCRIPTION
    Setup-WebView2.ps1 ran a bootstrapper it had just downloaded to a fixed %TEMP% name,
    elevated when the first run failed, without checking who had signed it. Each case
    below fails if one part of the fix is missing:

      1. Download folder                -> new, empty, unique, under the given root.
      2. Signature rule on fixtures     -> Valid + subject starting with exactly
                                           "CN=Microsoft Corporation," is accepted;
                                           a bad Status with a Microsoft subject, a
                                           substring match, a look-alike common name, and
                                           a missing certificate are all refused.
      3. Unsigned file on disk          -> Assert-MicrosoftSignedInstaller throws.
      4. Signed file on disk            -> accepted (when this host runs a binary signed
                                           by Microsoft Corporation).
      5. Setup-WebView2.ps1 wiring      -> dot-sources the helpers, downloads into a
                                           private folder, verifies before it starts the
                                           file, and no longer carries the fixed temp
                                           path or the dead Add-Type.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-VerifiedInstaller.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'VerifiedInstaller.ps1')

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

function New-FixtureSignature {
    param([string]$Status, [AllowNull()] [string]$Subject)
    $certificate = $null
    if ($null -ne $Subject) {
        $certificate = [pscustomobject]@{ Subject = $Subject }
    }
    return [pscustomobject]@{ Status = $Status; SignerCertificate = $certificate }
}

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ('heimdall-verifiedinstaller-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    # 1.
    $first = New-PrivateDownloadFolder -Root $sandbox
    $second = New-PrivateDownloadFolder -Root $sandbox
    Assert-True ((Test-Path -LiteralPath $first -PathType Container) -and @(Get-ChildItem -LiteralPath $first -Force).Count -eq 0) 'the download folder exists and is empty'
    Assert-True ($first -ne $second) 'two download folders never share a path'
    Assert-True ((Split-Path -Parent $first) -eq $sandbox) 'the download folder is created under the given root'

    # 2.
    $microsoft = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
    Assert-True (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'Valid' $microsoft)) 'a Valid Microsoft Corporation signature is accepted'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'HashMismatch' $microsoft))) 'a Microsoft subject with a failed status is refused'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'NotSigned' $null))) 'an unsigned file is refused'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'Valid' $null))) 'a Valid status with no certificate is refused'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'Valid' 'CN=Evil Ltd, O=Microsoft Corporation, C=US'))) 'the name inside another field is not a match'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'Valid' 'CN=Microsoft Corporation Fake, O=Evil, C=US'))) 'a look-alike common name is not a match'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature (New-FixtureSignature 'Valid' 'O=Evil, CN=Microsoft Corporation, C=US'))) 'the common name must come first'
    Assert-True (-not (Test-MicrosoftInstallerSignature -Signature $null)) 'a missing signature object is refused'

    # 3.
    $unsigned = Join-Path $sandbox 'unsigned-setup.exe'
    [System.IO.File]::WriteAllBytes($unsigned, [byte[]](0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00))
    $threw = $false
    try { Assert-MicrosoftSignedInstaller -Path $unsigned } catch { $threw = $true }
    Assert-True $threw 'an unsigned temporary executable is refused'

    # 4. Positive control: a real signature must pass, otherwise the rule could be refusing everything.
    $hostPath = (Get-Process -Id $PID).Path
    $hostSignature = Get-AuthenticodeSignature -LiteralPath $hostPath
    if ([string]$hostSignature.Status -eq 'Valid' -and $null -ne $hostSignature.SignerCertificate -and
        ([string]$hostSignature.SignerCertificate.Subject).StartsWith('CN=Microsoft Corporation,', [System.StringComparison]::Ordinal)) {
        $threw = $false
        try { Assert-MicrosoftSignedInstaller -Path $hostPath } catch { $threw = $true }
        Assert-True (-not $threw) 'a file signed by Microsoft Corporation is accepted'
    } else {
        Write-Host 'SKIP: this host runs no binary signed by CN=Microsoft Corporation, so the real-file positive control did not run.' -ForegroundColor Yellow
    }
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. Wiring.
$setupScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Setup-WebView2.ps1'
$setupText = Get-Content -LiteralPath $setupScript -Raw
Assert-True ($setupText -match 'VerifiedInstaller\.ps1') 'Setup-WebView2.ps1 dot-sources the helpers'
Assert-True ($setupText -match 'New-PrivateDownloadFolder') 'Setup-WebView2.ps1 downloads into a private folder'
Assert-True (-not ($setupText -match "GetTempPath\(\)\)\s+'MicrosoftEdgeWebview2Setup\.exe'")) 'Setup-WebView2.ps1 no longer downloads to a fixed temporary path'
Assert-True (-not ($setupText -match 'Add-Type')) 'Setup-WebView2.ps1 no longer carries the dead Add-Type'
$verifyIndex = $setupText.IndexOf('Assert-MicrosoftSignedInstaller')
$firstStart = $setupText.IndexOf('Start-Process')
Assert-True ($verifyIndex -ge 0 -and $firstStart -gt $verifyIndex) 'the signature is verified before the first Start-Process'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: a downloaded installer runs only from a private folder and only when Microsoft Corporation signed it.' -ForegroundColor Green
exit 0
