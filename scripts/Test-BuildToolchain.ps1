<#
.SYNOPSIS
    Tests the toolchain readers of the build (scripts/BuildToolchain.ps1).

.DESCRIPTION
    A Release logs the SDK and the runtime its self-contained publish carries, and
    compares the WiX CLI with the version .config/dotnet-tools.json pins. The readers
    are proven here, and a mismatch is proven to be refused rather than merely
    printed:

      1. The pinned version of a package in a tool manifest          -> read; an unknown
                                                                       package or a
                                                                       missing file -> $null.
      2. The repository's own manifest                               -> pins wix.
      3. A WiX stand-in that reports "6.0.2+<commit>"                -> "6.0.2"; one that
                                                                       fails -> $null.
      4. A pin and an installed version that differ                  -> Matches = $false,
                                                                       and the message
                                                                       names both.
      5. A folder holding System.Private.CoreLib.dll                 -> its version; a
                                                                       folder without it -> $null.
      6. The SDK                                                     -> a version number.
      7. Build.ps1 calls the readers                                 -> they are wired in.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-BuildToolchain.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BuildToolchain.ps1')

$repoRoot = Split-Path -Parent $PSScriptRoot
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

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-toolchain-test-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox -Force

try {
    # 1. The manifest reader.
    $manifest = Join-Path $sandbox 'dotnet-tools.json'
    Set-Content -LiteralPath $manifest -Encoding ASCII -Value '{"version":1,"isRoot":true,"tools":{"wix":{"version":"6.0.2","commands":["wix"]}}}'
    Assert-True ((Get-PinnedDotnetTool -ManifestPath $manifest -PackageId 'wix') -eq '6.0.2') 'The pinned version of wix is read from the manifest'
    Assert-True ($null -eq (Get-PinnedDotnetTool -ManifestPath $manifest -PackageId 'dotnet-ef')) 'A package the manifest does not pin gives $null'
    Assert-True ($null -eq (Get-PinnedDotnetTool -ManifestPath (Join-Path $sandbox 'absent.json') -PackageId 'wix')) 'A missing manifest gives $null'

    # 2. The repository's own pin.
    $realPin = Get-PinnedDotnetTool -ManifestPath (Join-Path $repoRoot '.config\dotnet-tools.json') -PackageId 'wix'
    Assert-True (-not [string]::IsNullOrWhiteSpace($realPin) -and $realPin -match '^\d+\.\d+\.\d+$') "The repository pins wix to a version number (got '$realPin')"

    # 3. The WiX CLI reader, over stand-ins.
    $fakeWix = Join-Path $sandbox 'fake-wix.cmd'
    Set-Content -LiteralPath $fakeWix -Encoding ASCII -Value @('@echo off', 'echo 6.0.2+b3f3403', 'exit /b 0')
    Assert-True ((Get-WixCliVersion -WixPath $fakeWix) -eq '6.0.2') 'The +<commit> suffix of the WiX version is dropped'
    $brokenWix = Join-Path $sandbox 'broken-wix.cmd'
    Set-Content -LiteralPath $brokenWix -Encoding ASCII -Value @('@echo off', 'exit /b 1')
    Assert-True ($null -eq (Get-WixCliVersion -WixPath $brokenWix)) 'A WiX CLI that fails gives $null'

    # 4. The pin check.
    $same = Resolve-PinnedToolCheck -Tool 'WiX' -Pinned '6.0.2' -Actual '6.0.2'
    Assert-True $same.Matches 'An installed version equal to the pin matches'
    $drift = Resolve-PinnedToolCheck -Tool 'WiX' -Pinned '6.0.2' -Actual '6.1.0'
    Assert-True (-not $drift.Matches) 'An installed version different from the pin does not match'
    Assert-True ($drift.Message -match '6\.1\.0' -and $drift.Message -match '6\.0\.2') 'The mismatch message names both versions'
    Assert-True (-not (Resolve-PinnedToolCheck -Tool 'WiX' -Pinned '6.0.2' -Actual '').Matches) 'A tool that reports no version does not match its pin'
    Assert-True (Resolve-PinnedToolCheck -Tool 'WiX' -Pinned '' -Actual '6.1.0').Matches 'A tool with no pin has nothing to enforce'

    # 5. The runtime reader: the real CoreLib of this PowerShell stands for a published runtime.
    $coreLib = [object].Assembly.Location
    $published = Join-Path $sandbox 'publish'
    $null = New-Item -ItemType Directory -Path $published
    Copy-Item -LiteralPath $coreLib -Destination (Join-Path $published 'System.Private.CoreLib.dll')
    $runtime = Get-PublishedRuntimeVersion -PublishDirectory $published
    Assert-True ($runtime -match '^\d+\.\d+\.\d+') "The runtime version is read from System.Private.CoreLib.dll (got '$runtime')"
    Assert-True (-not ($runtime -match '\+')) 'The +<commit> suffix of the runtime version is dropped'
    $empty = Join-Path $sandbox 'empty'
    $null = New-Item -ItemType Directory -Path $empty
    Assert-True ($null -eq (Get-PublishedRuntimeVersion -PublishDirectory $empty)) 'A folder with no runtime gives $null'

    # 6. The SDK.
    Push-Location $repoRoot
    try { $sdk = Get-SdkVersion } finally { Pop-Location }
    Assert-True ($sdk -match '^\d+\.\d+\.\d+') "The SDK version is read (got '$sdk')"
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# 7. Build.ps1 uses the readers.
$build = Get-Content -LiteralPath (Join-Path $repoRoot 'Build.ps1') -Raw
Assert-True ($build -match 'BuildToolchain\.ps1') 'Build.ps1 dot-sources scripts/BuildToolchain.ps1'
Assert-True ($build -match 'Get-SdkVersion') 'Build.ps1 logs the SDK version'
Assert-True ($build -match 'Get-PublishedRuntimeVersion') 'Build.ps1 logs the runtime each publish carries'
Assert-True ($build -match 'Resolve-PinnedToolCheck') 'Build.ps1 compares the WiX CLI with its pin'

if ($failures -gt 0) {
    Write-Host ""
    Write-Host "$failures expectation(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All toolchain expectations passed." -ForegroundColor Green
exit 0
