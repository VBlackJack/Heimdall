<#
.SYNOPSIS
    Installs a WebView2 Fixed Version Runtime CAB into runtimes/webview2 and records
    where it came from.

.DESCRIPTION
    The SelfContained edition bundles whatever runtimes/webview2 holds. This script is
    the one way to put a runtime there: it extracts the CAB into a fresh temporary
    folder, requires a valid Authenticode signature by Microsoft on msedgewebview2.exe,
    then replaces runtimes/webview2 with the extracted runtime and writes
    heimdall-runtime.json beside it ({fileVersion, runtimeFileUtc, cabSha256,
    installedUtc}). runtimeFileUtc is the LastWriteTime of msedgewebview2.exe as the CAB
    carries it, so it dates the runtime itself, not this script run. Build.ps1
    reads that manifest and refuses a Release that would bundle a runtime with no
    manifest or whose executable is older than MaxWebView2RuntimeAgeDays.

    Nothing is downloaded here. Fetch the CAB by hand:
      https://developer.microsoft.com/microsoft-edge/webview2/#download-section
      Fixed Version, x64: Microsoft.WebView2.FixedVersionRuntime.<version>.x64.cab

.PARAMETER CabPath
    The downloaded Microsoft.WebView2.FixedVersionRuntime.<version>.x64.cab.

.PARAMETER RuntimeDirectory
    Where the runtime goes. Defaults to runtimes\webview2 at the repository root.

.EXAMPLE
    .\scripts\Update-WebView2FixedRuntime.ps1 -CabPath "$env:USERPROFILE\Downloads\Microsoft.WebView2.FixedVersionRuntime.150.0.0.0.x64.cab"

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CabPath,

    [string]$RuntimeDirectory
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'WebView2RuntimeGate.ps1')

if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) {
    $RuntimeDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtimes\webview2'
}

if (-not (Test-Path -LiteralPath $CabPath -PathType Leaf)) {
    Write-Host "[!] CAB not found: $CabPath" -ForegroundColor Red
    Write-Host "    Download the Fixed Version x64 CAB from $script:WebView2DownloadPage" -ForegroundColor Yellow
    exit 1
}
$CabPath = (Resolve-Path -LiteralPath $CabPath).Path

$extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-webview2-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
try {
    Write-Host "Extracting $(Split-Path $CabPath -Leaf)..." -ForegroundColor Yellow
    # Relaxed around the native call so a line on stderr cannot end the script before
    # its exit code is read.
    $ErrorActionPreference = 'Continue'
    $expandOut = & expand.exe $CabPath -F:* $extractDir 2>&1
    $expandExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($expandExit -ne 0) {
        Write-Host "[!] expand.exe failed with exit code $expandExit." -ForegroundColor Red
        $expandOut | ForEach-Object { Write-Host "    $_" }
        exit 1
    }

    # The CAB holds one folder, Microsoft.WebView2.FixedVersionRuntime.<version>.x64,
    # whose root is the runtime. Located by its executable rather than by that name.
    $executables = @(Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter $script:WebView2RuntimeExecutable)
    if ($executables.Count -ne 1) {
        Write-Host "[!] Expected exactly one $script:WebView2RuntimeExecutable in the CAB, found $($executables.Count)." -ForegroundColor Red
        exit 1
    }
    $runtimeExe = $executables[0]
    $runtimeRoot = $runtimeExe.DirectoryName

    $signature = Get-AuthenticodeSignature -LiteralPath $runtimeExe.FullName
    if (-not (Test-WebView2RuntimeSigner -Signature $signature)) {
        $subject = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '<none>' }
        Write-Host "[!] $script:WebView2RuntimeExecutable is not validly signed by Microsoft. Refusing to install it." -ForegroundColor Red
        Write-Host "    Status: $($signature.Status); type: $($signature.SignatureType); signer: $subject" -ForegroundColor Red
        exit 1
    }

    $version = [string]$runtimeExe.VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        Write-Host "[!] $script:WebView2RuntimeExecutable carries no file version." -ForegroundColor Red
        exit 1
    }
    # expand.exe restores the timestamp the cabinet stores for each file, so this is when
    # Microsoft built the runtime, whatever day the CAB is installed.
    $runtimeFileUtc = $runtimeExe.LastWriteTimeUtc
    $cabSha = (Get-FileHash -LiteralPath $CabPath -Algorithm SHA256).Hash

    if (Test-Path -LiteralPath $RuntimeDirectory) {
        Remove-Item -LiteralPath $RuntimeDirectory -Recurse -Force
    }
    $parent = Split-Path -Parent $RuntimeDirectory
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    # Copied, not moved: the temporary folder is usually on another volume.
    Copy-Item -LiteralPath $runtimeRoot -Destination $RuntimeDirectory -Recurse

    $manifestJson = New-WebView2RuntimeManifestJson -FileVersion $version -RuntimeFileUtc $runtimeFileUtc `
        -CabSha256 $cabSha -InstalledUtc ([datetime]::UtcNow)
    [System.IO.File]::WriteAllText((Join-Path $RuntimeDirectory $script:WebView2RuntimeManifestName), $manifestJson, [System.Text.UTF8Encoding]::new($false))

    Write-Host "WebView2 Fixed Version Runtime $version installed in $RuntimeDirectory" -ForegroundColor Green
    Write-Host "    signer: $($signature.SignerCertificate.Subject)" -ForegroundColor DarkGray
    Write-Host "    runtime built: $($runtimeFileUtc.ToString('u'))" -ForegroundColor DarkGray
    Write-Host "    cab sha256: $($cabSha.ToLowerInvariant())" -ForegroundColor DarkGray
} finally {
    Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction SilentlyContinue
}
exit 0
