<#
.SYNOPSIS
    Helpers for running an installer that was downloaded: a private folder to download it
    into, and a signature check it must pass before it is started.

.DESCRIPTION
    Dot-source this file to get New-PrivateDownloadFolder, Test-MicrosoftInstallerSignature
    and Assert-MicrosoftSignedInstaller.

    Setup-WebView2.ps1 used to download the bootstrapper to a fixed name in %TEMP% and run
    it, then run it again elevated when the first attempt failed. A file at a predictable
    path can be swapped between the download and the run, and nothing checked who had
    signed what was about to be started with administrator rights.

    The rule is deliberately narrow: the Authenticode status is Valid, and the signer
    certificate subject STARTS WITH the exact text "CN=Microsoft Corporation,". A substring
    match would accept a certificate that merely mentions the name in another field, or a
    look-alike common name such as "CN=Microsoft Corporation Fake,".

    Compatible with Windows PowerShell 5.1 and PowerShell 7.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
Set-StrictMode -Version Latest

# The subject prefix the signer certificate of a Microsoft installer must carry. The trailing
# comma ends the common name, so "CN=Microsoft Corporation Fake," does not match.
$script:MicrosoftInstallerSignerPrefix = 'CN=Microsoft Corporation,'

function New-PrivateDownloadFolder {
    <#
    .SYNOPSIS
        Creates a new, empty, uniquely named folder under the temporary root and returns its path.
    #>
    [CmdletBinding()]
    param(
        # Where the folder is created; the user's temporary folder when omitted.
        [string]$Root = [System.IO.Path]::GetTempPath(),
        [string]$Prefix = 'heimdall-download-'
    )

    $path = Join-Path $Root ($Prefix + [Guid]::NewGuid().ToString('N'))
    # Not -Force: a folder that already exists at a random name is a surprise to stop on.
    New-Item -ItemType Directory -Path $path -ErrorAction Stop | Out-Null
    return $path
}

function Test-MicrosoftInstallerSignature {
    <#
    .SYNOPSIS
        True only for a Valid Authenticode signature whose signer subject starts with
        "CN=Microsoft Corporation,".
    .DESCRIPTION
        Takes the object Get-AuthenticodeSignature returns, so the rule is testable with
        fixture objects as well as with real files.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()]
        $Signature
    )

    if ($null -eq $Signature) { return $false }
    if ([string]$Signature.Status -ne 'Valid') { return $false }
    if ($null -eq $Signature.SignerCertificate) { return $false }
    $subject = [string]$Signature.SignerCertificate.Subject
    return $subject.StartsWith($script:MicrosoftInstallerSignerPrefix, [System.StringComparison]::Ordinal)
}

function Assert-MicrosoftSignedInstaller {
    <#
    .SYNOPSIS
        Throws unless the file at Path passes Test-MicrosoftInstallerSignature.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if (-not (Test-MicrosoftInstallerSignature -Signature $signature)) {
        $signer = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '<none>' }
        throw "Refusing to run '$Path': signature status '$($signature.Status)', signer '$signer'. A Microsoft-signed installer is required."
    }
}
