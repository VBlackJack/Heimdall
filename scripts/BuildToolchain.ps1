<#
.SYNOPSIS
    Reads which toolchain a Release is built with: the .NET SDK, the runtime the
    self-contained publish carries, and the pinned installer tools.

.DESCRIPTION
    Dot-source this file to get Get-SdkVersion, Get-PublishedRuntimeVersion,
    Get-PinnedDotnetTool, Get-WixCliVersion and Resolve-PinnedToolCheck.

    Both editions publish with --self-contained true, so the runtime they ship is
    whatever the build machine's SDK resolved: global.json says `latestPatch`, and a
    machine with a newer patch ships a newer runtime than the machine that built the
    release before it. Nothing recorded which. Build.ps1 now prints the SDK version
    before it builds and, after each publish, the version of the runtime that landed in
    the output folder (the product version of System.Private.CoreLib.dll, the file that
    IS the runtime), so the log of a release says what shipped.

    The refusal for an SDK older than global.json is not here: the SDK resolver already
    fails that build.

    WiX is a .NET tool. It is pinned in .config/dotnet-tools.json (the one place; the
    packaging workflow installs the version read from it), and a Release compares the
    installed CLI with the pin. A different version is a warning for a dry run and a
    refusal for a real publish: an MSI built by a tool nobody chose is not reproducible.

    Inno Setup has no such manifest; its version is pinned where it is installed (the
    packaging workflow). Its compiler prints its own version in the compile output,
    which scripts/InnoSetupCompile.ps1 keeps.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

#Requires -Version 5.1

param()

# The file that IS the .NET runtime in a self-contained publish.
$script:RuntimeCoreLibrary = 'System.Private.CoreLib.dll'

# FileVersionInfo.ProductVersion of a framework assembly is "<version>+<commit>".
$script:ProductVersionSeparator = '+'

function Get-SdkVersion {
    <#
    .SYNOPSIS
        The version of the SDK `dotnet` resolves from the current directory (global.json applies).
    #>
    param()
    $version = & dotnet --version 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$version")) { return $null }
    return ("$version").Trim()
}

function Get-PublishedRuntimeVersion {
    <#
    .SYNOPSIS
        The version of the .NET runtime a self-contained publish folder carries, or $null
        when the folder holds no runtime.
    #>
    param([Parameter(Mandatory)] [string] $PublishDirectory)

    $library = Join-Path $PublishDirectory $script:RuntimeCoreLibrary
    if (-not (Test-Path -LiteralPath $library)) { return $null }

    $productVersion = (Get-Item -LiteralPath $library).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) { return $null }
    # The leading numbers only: the product version carries "-servicing.<build>" after them.
    if ($productVersion -match '^\d+(\.\d+)+') { return $Matches[0] }
    return $null
}

function Get-PinnedDotnetTool {
    <#
    .SYNOPSIS
        The version a dotnet tool manifest pins for a package, or $null when it pins none.
    #>
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $PackageId
    )

    if (-not (Test-Path -LiteralPath $ManifestPath)) { return $null }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $tool = $manifest.tools.PSObject.Properties | Where-Object { $_.Name -eq $PackageId } | Select-Object -First 1
    if ($null -eq $tool) { return $null }
    return [string]$tool.Value.version
}

function Get-WixCliVersion {
    <#
    .SYNOPSIS
        The version a WiX CLI reports, without the "+<commit>" suffix, or $null when it does not answer.
    #>
    param([Parameter(Mandatory)] [string] $WixPath)

    $reported = & $WixPath --version 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace("$reported")) { return $null }
    return ("$reported").Trim().Split($script:ProductVersionSeparator)[0]
}

function Resolve-PinnedToolCheck {
    <#
    .SYNOPSIS
        Compares an installed tool version with its pin.
    .OUTPUTS
        An object with Matches and Message. A tool with no pin matches: nothing to enforce.
    #>
    param(
        [Parameter(Mandatory)] [string] $Tool,
        [string] $Pinned,
        [string] $Actual
    )

    if ([string]::IsNullOrWhiteSpace($Pinned)) {
        return [pscustomobject]@{ Matches = $true; Message = "$Tool is not pinned" }
    }
    if ([string]::IsNullOrWhiteSpace($Actual)) {
        return [pscustomobject]@{ Matches = $false; Message = "$Tool did not report a version; the pin is $Pinned" }
    }
    if ($Actual -eq $Pinned) {
        return [pscustomobject]@{ Matches = $true; Message = "$Tool $Actual (pinned $Pinned)" }
    }
    return [pscustomobject]@{ Matches = $false; Message = "$Tool is $Actual but .config/dotnet-tools.json pins $Pinned" }
}
