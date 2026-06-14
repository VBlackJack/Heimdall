# Setup-WebView2.ps1
# Downloads and installs the WebView2 Evergreen Runtime (user-level, no admin required).
# Run once on machines where Edge/WebView2 is not installed (e.g. Windows Server gateways).

$ErrorActionPreference = 'Stop'

# Check if already installed
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'src\Heimdall.App\lib\webview2\Microsoft.Web.WebView2.Core.dll') -ErrorAction SilentlyContinue
} catch {}

$installed = $false
try {
    $regPaths = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($p in $regPaths) {
        if (Test-Path $p) {
            $ver = (Get-ItemProperty $p -ErrorAction SilentlyContinue).pv
            if ($ver) {
                Write-Host "WebView2 Runtime already installed: v$ver" -ForegroundColor Green
                $installed = $true
                break
            }
        }
    }
} catch {}

if ($installed) {
    exit 0
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WebView2 Evergreen Runtime Installer" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "WebView2 Runtime is required for embedded SSH terminals and VNC sessions." -ForegroundColor White
Write-Host ""

$bootstrapper = Join-Path ([System.IO.Path]::GetTempPath()) 'MicrosoftEdgeWebview2Setup.exe'

Write-Host "[1/2] Downloading WebView2 Evergreen Bootstrapper..." -ForegroundColor Yellow
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper -UseBasicParsing

Write-Host "[2/2] Installing WebView2 Runtime..." -ForegroundColor Yellow
$proc = Start-Process $bootstrapper -ArgumentList '/silent /install' -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Host "Installation may require elevation. Retrying with admin..." -ForegroundColor DarkYellow
    Start-Process $bootstrapper -ArgumentList '/silent /install' -Verb RunAs -Wait
}

Remove-Item $bootstrapper -Force -ErrorAction SilentlyContinue

# Verify
$verified = $false
foreach ($p in $regPaths) {
    if (Test-Path $p) {
        $ver = (Get-ItemProperty $p -ErrorAction SilentlyContinue).pv
        if ($ver) {
            Write-Host ""
            Write-Host "========================================" -ForegroundColor Green
            Write-Host "  WebView2 Runtime installed: v$ver" -ForegroundColor Green
            Write-Host "  Restart Heimdall to use embedded terminals." -ForegroundColor Green
            Write-Host "========================================" -ForegroundColor Green
            $verified = $true
            break
        }
    }
}

if (-not $verified) {
    Write-Host "Installation could not be verified. Check manually." -ForegroundColor Red
    exit 1
}
