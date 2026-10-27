<#
.SYNOPSIS
    Decides whether a build may bundle the WebView2 Fixed Version Runtime found in
    runtimes/webview2, and owns the manifest that records where that runtime came from.

.DESCRIPTION
    Dot-source this file to get Resolve-WebView2RuntimeGate, Read-WebView2RuntimeManifest,
    New-WebView2RuntimeManifestJson and Test-WebView2RuntimeSigner.

    The SelfContained edition ships the Fixed Version Runtime that sits in the git-ignored
    runtimes/webview2 folder. Nothing fetched it, pinned it or aged it: the copy shipped in
    October 2026 was 146.0.3856.62 from March, seven months of Chromium security fixes
    behind, and the web consoles, noVNC and the terminal all run on it. A Fixed Version
    Runtime never updates itself.

    scripts/Update-WebView2FixedRuntime.ps1 installs a runtime from a CAB and writes
    runtimes/webview2/heimdall-runtime.json beside it. This module reads that manifest and
    refuses a Release that would bundle a runtime with no manifest, or one whose
    executable was built more than MaxWebView2RuntimeAgeDays ago.

    The age is the runtime's own, not the install script's: runtimeFileUtc is the
    LastWriteTime of msedgewebview2.exe as the CAB carries it (expand.exe restores the
    timestamp stored in the cabinet, and the copy into runtimes/webview2 keeps it).
    Installing an old CAB today therefore does not make it look fresh, which an
    "installed at" timestamp would.

    It also makes a missing runtime fatal in Release mode when the SelfContained edition
    is requested: the build used to fall back to Standard in silence, and a release without
    the SelfContained installer leaves every SelfContained installation unable to update.
    An explicit -Variant Standard never reaches this check, and no override excuses it.

    The one override of the release gates is Build.ps1 -AcceptCiFailure "<reason>"
    (scripts/ReleasePreflight.ps1): it turns a stale or unrecorded runtime into a warning
    and writes the reason into the release notes.

    Decisions:
      NotRequired         no SelfContained variant is requested
      Ready               the runtime is present and recent enough
      Stale               a Release would bundle a runtime that is stale or unrecorded;
                          Build.ps1 aborts unless the release gates are overridden
      Abort               a Release cannot bundle what it was asked to bundle
      FallBackToStandard  Debug only: no runtime, so the Standard edition alone is built

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

# How old the runtime executable may be before a Release refuses to ship it. Chromium
# ships a security release every few weeks and Microsoft publishes the Fixed Version
# CAB with it; two months bounds the lag to about two cycles.
$script:MaxWebView2RuntimeAgeDays = 60

# The runtime's executable: its presence is what makes the folder a runtime.
$script:WebView2RuntimeExecutable = 'msedgewebview2.exe'

# Written by scripts/Update-WebView2FixedRuntime.ps1 beside the runtime it installed.
$script:WebView2RuntimeManifestName = 'heimdall-runtime.json'

# The only organisation trusted to sign the runtime executable.
$script:WebView2TrustedSignerPattern = 'O=Microsoft Corporation'

# Where the owner downloads the CAB by hand. Printed, never fetched.
$script:WebView2DownloadPage = 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section'

$script:ManifestTimestampFormat = 'yyyy-MM-ddTHH:mm:ssZ'

function ConvertTo-UtcDateTime {
    <#
    .SYNOPSIS
        Reads a manifest timestamp whichever way ConvertFrom-Json delivered it.
    .DESCRIPTION
        PowerShell 7 turns an ISO 8601 string into a DateTime on its own; Windows
        PowerShell 5.1 leaves it a string. Returns $null when it is neither.
    #>
    param($Value)

    if ($Value -is [datetime]) {
        return $Value.ToUniversalTime()
    }
    if ($Value -is [string]) {
        $parsed = [datetime]::MinValue
        $styles = [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal
        if ([datetime]::TryParse($Value, [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
            return $parsed
        }
    }
    return $null
}

function Get-ManifestProperty {
    param($Object, [string]$Name)
    if ($Object.PSObject.Properties.Name -contains $Name) { return $Object.$Name }
    return $null
}

function Read-WebView2RuntimeManifest {
    <#
    .SYNOPSIS
        Reads runtimes/webview2/heimdall-runtime.json.
    .OUTPUTS
        An object with FileVersion, RuntimeFileUtc (a UTC DateTime), CabSha256 and
        InstalledUtc, or $null when the manifest is missing, unreadable, or lacks a usable
        runtimeFileUtc.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeDirectory
    )

    $path = Join-Path $RuntimeDirectory $script:WebView2RuntimeManifestName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    try {
        $raw = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($path))
    } catch {
        return $null
    }
    if ($null -eq $raw) {
        return $null
    }

    $runtimeFile = ConvertTo-UtcDateTime -Value (Get-ManifestProperty $raw 'runtimeFileUtc')
    if ($null -eq $runtimeFile) {
        return $null
    }

    return [pscustomobject]@{
        FileVersion    = [string](Get-ManifestProperty $raw 'fileVersion')
        RuntimeFileUtc = $runtimeFile
        CabSha256      = [string](Get-ManifestProperty $raw 'cabSha256')
        InstalledUtc   = ConvertTo-UtcDateTime -Value (Get-ManifestProperty $raw 'installedUtc')
    }
}

function New-WebView2RuntimeManifestJson {
    <#
    .SYNOPSIS
        Returns the manifest text for an installed runtime. Pure: writes nothing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string]$FileVersion,
        [Parameter(Mandatory = $true)] [datetime]$RuntimeFileUtc,
        [Parameter(Mandatory = $true)] [string]$CabSha256,
        [Parameter(Mandatory = $true)] [datetime]$InstalledUtc
    )

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $manifest = [ordered]@{
        fileVersion    = $FileVersion
        runtimeFileUtc = $RuntimeFileUtc.ToUniversalTime().ToString($script:ManifestTimestampFormat, $culture)
        cabSha256      = $CabSha256.ToLowerInvariant()
        installedUtc   = $InstalledUtc.ToUniversalTime().ToString($script:ManifestTimestampFormat, $culture)
    }
    return (ConvertTo-Json -InputObject $manifest)
}

function Test-WebView2RuntimeSigner {
    <#
    .SYNOPSIS
        True when an Authenticode signature is a valid one embedded in the file and made
        by Microsoft.
    .DESCRIPTION
        Takes the object Get-AuthenticodeSignature returns, so the rule is testable with
        fixtures. A catalog signature is refused: it vouches for a file only on the
        machine whose catalog lists it, and the real runtime executable carries its own.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        $Signature
    )

    if ($null -eq $Signature) { return $false }
    if ([string]$Signature.Status -ne 'Valid') { return $false }
    if (($Signature.PSObject.Properties.Name -contains 'SignatureType') -and [string]$Signature.SignatureType -ne 'Authenticode') {
        return $false
    }
    if ($null -eq $Signature.SignerCertificate) { return $false }
    return ([string]$Signature.SignerCertificate.Subject).Contains($script:WebView2TrustedSignerPattern)
}

function Resolve-WebView2RuntimeGate {
    <#
    .SYNOPSIS
        Decides what a build does about the bundled WebView2 runtime.
    .OUTPUTS
        An object with Decision (see the file header), Variants (what to build) and
        Messages (lines for the operator, in order).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeDirectory,

        [Parameter(Mandatory = $true)]
        [ValidateSet('Debug', 'Release')]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string[]]$Variants,

        # Injectable so the age rule can be tested without waiting for it.
        [datetime]$NowUtc = [datetime]::UtcNow
    )

    $messages = New-Object System.Collections.Generic.List[string]
    $result = {
        param($Decision, $Kept)
        return [pscustomobject]@{ Decision = $Decision; Variants = @($Kept); Messages = $messages.ToArray() }
    }

    if (-not ($Variants -contains 'SelfContained')) {
        return (& $result 'NotRequired' $Variants)
    }

    $updateHint = "Download the Fixed Version x64 CAB from $script:WebView2DownloadPage and run scripts\Update-WebView2FixedRuntime.ps1 -CabPath <cab>."
    $hasRuntime = Test-Path -LiteralPath (Join-Path $RuntimeDirectory $script:WebView2RuntimeExecutable) -PathType Leaf
    if (-not $hasRuntime) {
        $messages.Add("WebView2 Fixed Version Runtime not found in $RuntimeDirectory.")
        $messages.Add($updateHint)
        if ($Mode -eq 'Release') {
            $messages.Add('A Release asked for the SelfContained edition cannot drop it; pass -Variant Standard to build without it.')
            return (& $result 'Abort' $Variants)
        }
        $messages.Add('Building the Standard edition only.')
        return (& $result 'FallBackToStandard' @($Variants | Where-Object { $_ -ne 'SelfContained' }))
    }

    if ($Mode -ne 'Release') {
        return (& $result 'Ready' $Variants)
    }

    $manifest = Read-WebView2RuntimeManifest -RuntimeDirectory $RuntimeDirectory
    $problem = $null
    if ($null -eq $manifest) {
        $problem = "The bundled WebView2 runtime has no readable $script:WebView2RuntimeManifestName with a runtimeFileUtc, so its age is unknown."
    } else {
        $ageDays = ($NowUtc.ToUniversalTime() - $manifest.RuntimeFileUtc).TotalDays
        if ($ageDays -lt 0) {
            $problem = "The bundled WebView2 runtime manifest dates its executable in the future ($($manifest.RuntimeFileUtc.ToString('u')))."
        } elseif ($ageDays -gt $script:MaxWebView2RuntimeAgeDays) {
            $problem = ("The bundled WebView2 runtime {0} dates from {1}, {2:N0} days ago, more than the {3} allowed." -f $manifest.FileVersion, $manifest.RuntimeFileUtc.ToString('u'), [Math]::Floor($ageDays), $script:MaxWebView2RuntimeAgeDays)
        }
    }

    if ($null -eq $problem) {
        $messages.Add(("WebView2 Fixed Version Runtime {0}, built {1}." -f $manifest.FileVersion, $manifest.RuntimeFileUtc.ToString('u')))
        return (& $result 'Ready' $Variants)
    }

    $messages.Add($problem)
    $messages.Add($updateHint)
    return (& $result 'Stale' $Variants)
}
