<#
.SYNOPSIS
    Typography guard for GitHub release notes: rejects every character that cannot be
    typed on the French AZERTY keyboard, so the publish step can refuse to ship it.

.DESCRIPTION
    Dot-source this file to get Get-NotesTypographyViolations.

    The rule is an ALLOW-LIST, not a deny-list. A character is accepted only when the
    French AZERTY layout can actually produce it: printable ASCII, the accented
    letters carried by the direct keys and by the three dead keys, and the handful of
    symbols on the layout. Everything else is rejected, including characters no
    deny-list would have thought to enumerate: word-processor "smart" punctuation,
    non-standard spaces, non-breaking hyphens, arrows, mathematical look-alikes and
    emoji.

    Rationale (decision 2026-07-26). The previous implementation froze ten known
    offenders in a deny-list and silently accepted every other non-typeable
    character. The stated criterion is the keyboard, so the keyboard is what the
    guard encodes. Consequence worth naming: the French guillemets U+00AB / U+00BB
    are now rejected. They are absent from the AZERTY layout and reach a document
    only through word-processor autocorrection, exactly like the curly quotes they
    resemble; the ASCII double quote is the replacement.

    The guard runs at publish time over the hand-written release-notes file only
    (see Build.ps1). It is fail-closed: any violation aborts the publish unless
    -AllowNonAsciiNotes is passed.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

# Single source of truth #1: the non-ASCII code points the French AZERTY layout can
# produce. Printable ASCII is added as a range in the allow-list built below, so it
# is not repeated here.
#
# Deliberately ABSENT because the layout does not produce them - listing them here
# as a comment so a future reader does not re-add them by reflex:
#   U+00AB / U+00BB  French guillemets      -> use the ASCII double quote
#   U+0153 / U+0152  oe ligature            -> write "oe"
#   U+00E6 / U+00C6  ae ligature            -> write "ae"
$script:AzertyNonAsciiChars = @(
    # Direct and shifted keys.
    0x00E9, 0x00C9,  # e with acute
    0x00E8, 0x00C8,  # e with grave
    0x00E7, 0x00C7,  # c with cedilla
    0x00E0, 0x00C0,  # a with grave
    0x00F9, 0x00D9,  # u with grave
    0x00B0,          # degree sign
    0x00B2,          # superscript two
    0x00B5,          # micro sign
    0x00A7,          # section sign
    0x00A3,          # pound sign
    0x00A4,          # currency sign
    0x00A8,          # diaeresis, dead key followed by a space
    0x20AC,          # euro sign, AltGr
    # Circumflex dead key.
    0x00E2, 0x00C2, 0x00EA, 0x00CA, 0x00EE, 0x00CE, 0x00F4, 0x00D4, 0x00FB, 0x00DB,
    # Diaeresis dead key.
    0x00E4, 0x00C4, 0x00EB, 0x00CB, 0x00EF, 0x00CF, 0x00F6, 0x00D6, 0x00FC, 0x00DC,
    0x00FF,
    # Tilde dead key.
    0x00E3, 0x00C3, 0x00F1, 0x00D1, 0x00F5, 0x00D5,
    # Grave dead key, letters not already carried by a direct key.
    0x00EC, 0x00CC, 0x00F2, 0x00D2
)

# The allow-list itself: tab, printable ASCII, and the layout characters above.
$script:AllowedNoteChars = New-Object 'System.Collections.Generic.HashSet[int]'
$null = $script:AllowedNoteChars.Add(0x09)
for ($script:cp = 0x20; $script:cp -le 0x7E; $script:cp++) {
    $null = $script:AllowedNoteChars.Add($script:cp)
}
foreach ($script:cp in $script:AzertyNonAsciiChars) {
    $null = $script:AllowedNoteChars.Add($script:cp)
}
Remove-Variable -Name 'cp' -Scope 'Script' -ErrorAction SilentlyContinue

# Single source of truth #2: named remedies for the offenders that actually turn up
# in hand-written notes. A character absent from this table is still rejected - the
# table only improves the message, it never grants permission.
$script:NoteCharRemedies = @{
    0x2014 = @{ Name = 'EM DASH';                           Remedy = 'use the ASCII hyphen -' }
    0x2013 = @{ Name = 'EN DASH';                           Remedy = 'use the ASCII hyphen -' }
    0x2010 = @{ Name = 'HYPHEN';                            Remedy = 'use the ASCII hyphen -' }
    0x2011 = @{ Name = 'NON-BREAKING HYPHEN';               Remedy = 'use the ASCII hyphen -' }
    0x2212 = @{ Name = 'MINUS SIGN';                        Remedy = 'use the ASCII hyphen -' }
    0x201C = @{ Name = 'LEFT DOUBLE QUOTATION MARK';        Remedy = 'use the ASCII double quote' }
    0x201D = @{ Name = 'RIGHT DOUBLE QUOTATION MARK';       Remedy = 'use the ASCII double quote' }
    0x201E = @{ Name = 'DOUBLE LOW-9 QUOTATION MARK';       Remedy = 'use the ASCII double quote' }
    0x00AB = @{ Name = 'LEFT-POINTING DOUBLE ANGLE QUOTATION MARK';  Remedy = 'not on the AZERTY layout; use the ASCII double quote' }
    0x00BB = @{ Name = 'RIGHT-POINTING DOUBLE ANGLE QUOTATION MARK'; Remedy = 'not on the AZERTY layout; use the ASCII double quote' }
    0x2018 = @{ Name = 'LEFT SINGLE QUOTATION MARK';        Remedy = 'use the ASCII apostrophe' }
    0x2019 = @{ Name = 'RIGHT SINGLE QUOTATION MARK';       Remedy = 'use the ASCII apostrophe' }
    0x2026 = @{ Name = 'HORIZONTAL ELLIPSIS';               Remedy = 'use three ASCII dots ...' }
    0x00A0 = @{ Name = 'NO-BREAK SPACE';                    Remedy = 'use a normal space' }
    0x202F = @{ Name = 'NARROW NO-BREAK SPACE';             Remedy = 'use a normal space' }
    0x2009 = @{ Name = 'THIN SPACE';                        Remedy = 'use a normal space' }
    0x00AD = @{ Name = 'SOFT HYPHEN';                       Remedy = 'delete it, it is invisible' }
    0xFEFF = @{ Name = 'ZERO WIDTH NO-BREAK SPACE (BOM)';   Remedy = 'delete it, save the file as UTF-8 without BOM' }
    0x200B = @{ Name = 'ZERO WIDTH SPACE';                  Remedy = 'delete it, it is invisible' }
    0x2192 = @{ Name = 'RIGHTWARDS ARROW';                  Remedy = 'write ->' }
    0x2264 = @{ Name = 'LESS-THAN OR EQUAL TO';             Remedy = 'write <=' }
    0x2265 = @{ Name = 'GREATER-THAN OR EQUAL TO';          Remedy = 'write >=' }
    0x0153 = @{ Name = 'LATIN SMALL LIGATURE OE';           Remedy = 'not on the AZERTY layout; write oe' }
    0x0152 = @{ Name = 'LATIN CAPITAL LIGATURE OE';         Remedy = 'not on the AZERTY layout; write OE' }
}

$script:GenericNoteRemedy = 'not typeable on the French AZERTY layout; use an ASCII equivalent'
$script:GenericNoteCharName = 'CHARACTER ABSENT FROM THE AZERTY LAYOUT'

function Get-NotesTypographyViolations {
    <#
    .SYNOPSIS
        Returns one record per character of the file at $Path that the French AZERTY
        layout cannot produce.
    .OUTPUTS
        Objects with File, Line, Column, Char, CodePoint (U+XXXX), Name and Remedy.
        An empty result means the file is clean.
        A surrogate pair (emoji) is reported once, at its real code point.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    # Function-scoped strict mode: enforced during validation without leaking to a
    # dot-sourcing caller (Build.ps1 relies on its own non-strict runtime).
    Set-StrictMode -Version Latest

    $violations = @()
    $lines = [System.IO.File]::ReadAllLines($Path, [System.Text.UTF8Encoding]::new($false))
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $c = 0
        while ($c -lt $line.Length) {
            $current = $line[$c]
            $width = 1
            $codePoint = [int][char]$current

            # Astral characters (emoji) arrive as two UTF-16 units. Report the pair
            # once, at the code point a human would look up, not as two lone halves.
            if ([char]::IsHighSurrogate($current) -and
                ($c + 1) -lt $line.Length -and
                [char]::IsLowSurrogate($line[$c + 1])) {
                $codePoint = [char]::ConvertToUtf32($current, $line[$c + 1])
                $width = 2
            }

            if (-not $script:AllowedNoteChars.Contains($codePoint)) {
                if ($script:NoteCharRemedies.Contains($codePoint)) {
                    $name = $script:NoteCharRemedies[$codePoint].Name
                    $remedy = $script:NoteCharRemedies[$codePoint].Remedy
                } else {
                    $name = $script:GenericNoteCharName
                    $remedy = $script:GenericNoteRemedy
                }
                $violations += [PSCustomObject]@{
                    File      = $Path
                    Line      = $i + 1
                    Column    = $c + 1
                    Char      = $line.Substring($c, $width)
                    CodePoint = ('U+{0:X4}' -f $codePoint)
                    Name      = $name
                    Remedy    = $remedy
                }
            }

            $c += $width
        }
    }
    return $violations
}
