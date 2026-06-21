<#
.SYNOPSIS
    Typography guard for GitHub release notes: detects banned "smart" punctuation
    so the publish step can refuse to ship it.

.DESCRIPTION
    Dot-source this file to get Get-NotesTypographyViolations. The banned set is the
    single source of truth for the project's release-notes typography rule. French
    accents and the French guillemets (U+00AB / U+00BB) are intentionally allowed;
    only "smart" punctuation and non-standard spaces are rejected. ASCII equivalents
    are expected instead (for example '-' rather than an em-dash, a normal space
    rather than a no-break space).

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>

Set-StrictMode -Version Latest

# Single source of truth: code point -> Unicode name. Keep all banned characters
# here (no scattered literals elsewhere). Allowed on purpose and absent below:
# French accents, and the French guillemets U+00AB / U+00BB.
# Plain hashtable (not [ordered]): integer keys must resolve by key, whereas an
# OrderedDictionary would treat an integer index as a positional lookup.
$script:BannedNoteChars = @{
    0x2014 = 'EM DASH'
    0x2013 = 'EN DASH'
    0x201C = 'LEFT DOUBLE QUOTATION MARK'
    0x201D = 'RIGHT DOUBLE QUOTATION MARK'
    0x2018 = 'LEFT SINGLE QUOTATION MARK'
    0x2019 = 'RIGHT SINGLE QUOTATION MARK'
    0x2026 = 'HORIZONTAL ELLIPSIS'
    0x00A0 = 'NO-BREAK SPACE'
    0x202F = 'NARROW NO-BREAK SPACE'
    0x2009 = 'THIN SPACE'
}

function Get-NotesTypographyViolations {
    <#
    .SYNOPSIS
        Returns one record per banned character found in the file at $Path.
    .OUTPUTS
        Objects with File, Line, Column, Char, CodePoint (U+XXXX) and Name.
        An empty result means the file is clean.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $violations = @()
    $lines = [System.IO.File]::ReadAllLines($Path, [System.Text.UTF8Encoding]::new($false))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        for ($c = 0; $c -lt $line.Length; $c++) {
            $codePoint = [int][char]$line[$c]
            if ($script:BannedNoteChars.Contains($codePoint)) {
                $violations += [PSCustomObject]@{
                    File      = $Path
                    Line      = $i + 1
                    Column    = $c + 1
                    Char      = [string]$line[$c]
                    CodePoint = ('U+{0:X4}' -f $codePoint)
                    Name      = $script:BannedNoteChars[$codePoint]
                }
            }
        }
    }
    return $violations
}
