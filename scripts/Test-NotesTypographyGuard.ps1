<#
.SYNOPSIS
    Tests the release-notes typography guard (scripts/NotesTypographyGuard.ps1).

.DESCRIPTION
    The guard is an allow-list keyed on what the French AZERTY keyboard can produce.
    A guard like this fails in two directions, so the suite proves both:

      1. Word-processor "smart" punctuation      -> must be reported.
      2. French accents with normal spaces       -> must be clean.
      3. French guillemets U+00AB / U+00BB       -> must be reported (they are NOT
                                                    on the layout; this case was
                                                    asserted the other way round
                                                    before the 2026-07-26 decision).
      4. Characters no deny-list anticipated     -> must be reported, which is the
                                                    whole point of an allow-list.
      5. Every tracked release-notes file        -> must be clean, all of them, not
                                                    one pinned version.
      6. Every violation carries a remedy        -> the message has to say what to
                                                    type instead.
      7. A leading byte-order mark               -> must be reported. No keyboard
                                                    types one, so the rule covers it.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  powershell -NoProfile -File scripts\Test-NotesTypographyGuard.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'NotesTypographyGuard.ps1')

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
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

function New-TempNotes {
    param([string]$Content)
    $path = [System.IO.Path]::GetTempFileName()
    [System.IO.File]::WriteAllText($path, $Content, $script:utf8NoBom)
    return $path
}

# ----------------------------------------------------------------------------
# Case 1: word-processor "smart" punctuation must be detected.
# ----------------------------------------------------------------------------
$badPath = New-TempNotes ("First line is clean ASCII." + [char]0x0A +
    "An em-dash " + [char]0x2014 + " here." + [char]0x0A +
    "Curly " + [char]0x201C + "quote" + [char]0x201D + " and apostrophe " + [char]0x2019 + "." + [char]0x0A +
    "Ellipsis" + [char]0x2026 + " plus a NBSP" + [char]0x00A0 + "and a narrow" + [char]0x202F + "and thin" + [char]0x2009 + "spaces.")
try {
    $v = @(Get-NotesTypographyViolations -Path $badPath)
    Assert-True ($v.Count -ge 7) "smart punctuation flagged ($($v.Count) violations, expected >= 7)"
    $kinds = ($v | ForEach-Object { $_.CodePoint } | Sort-Object -Unique) -join ','
    # Extract through the pipeline, never as $v.CodePoint: under Set-StrictMode
    # -Version Latest, member access on an empty array throws PropertyNotFoundStrict
    # and aborts the run just when a regression makes the FAIL lines worth reading.
    $codePoints = @($v | ForEach-Object { $_.CodePoint })
    Assert-True ($codePoints -contains 'U+2014') "em-dash detected (kinds: $kinds)"
    Assert-True ($codePoints -contains 'U+202F') 'narrow no-break space detected'
} finally {
    Remove-Item -LiteralPath $badPath -Force
}

# ----------------------------------------------------------------------------
# Case 2: French accents with normal spaces are allowed - the guard must not
# degenerate into "reject all non-ASCII".
# ----------------------------------------------------------------------------
$goodPath = New-TempNotes ("Recuperation verifiee " + [char]0x00E9 + [char]0x00E0 + [char]0x00E8 + [char]0x00E7 + "." + [char]0x0A +
    "Circonflexe " + [char]0x00EA + [char]0x00EE + [char]0x00F4 + [char]0x00FB + [char]0x00E2 + " et trema " + [char]0x00EB + [char]0x00EF + "." + [char]0x0A +
    "Majuscules " + [char]0x00C9 + [char]0x00C0 + " et symboles " + [char]0x00B0 + [char]0x00B5 + [char]0x00A7 + [char]0x20AC + " avec un tiret ASCII -.")
try {
    $v = @(Get-NotesTypographyViolations -Path $goodPath)
    $seen = ($v | ForEach-Object { $_.CodePoint } | Sort-Object -Unique) -join ','
    Assert-True ($v.Count -eq 0) "AZERTY letters, symbols and normal spaces are clean ($($v.Count) violations: $seen)"
} finally {
    Remove-Item -LiteralPath $goodPath -Force
}

# ----------------------------------------------------------------------------
# Case 3: the French guillemets are NOT on the AZERTY layout, so they must be
# reported. This assertion is the inverse of the pre-2026-07-26 expectation; if it
# is ever flipped back, the allow-list has been quietly widened.
# ----------------------------------------------------------------------------
$guillemetsPath = New-TempNotes ("Le bouton " + [char]0x00AB + " Tester " + [char]0x00BB + " utilise le delai.")
try {
    $v = @(Get-NotesTypographyViolations -Path $guillemetsPath)
    Assert-True ($v.Count -eq 2) "French guillemets flagged ($($v.Count) violations, expected 2)"
    $codePoints = @($v | ForEach-Object { $_.CodePoint })
    Assert-True ($codePoints -contains 'U+00AB') 'opening guillemet U+00AB detected'
    Assert-True ($codePoints -contains 'U+00BB') 'closing guillemet U+00BB detected'
} finally {
    Remove-Item -LiteralPath $guillemetsPath -Force
}

# ----------------------------------------------------------------------------
# Case 4: characters the old deny-list never listed. An allow-list catches them
# without being told about them; a deny-list could not. The rocket is a surrogate
# pair and must be reported ONCE, at U+1F680, not twice as two lone halves.
# ----------------------------------------------------------------------------
$exoticPath = New-TempNotes ("Non-breaking hyphen " + [char]0x2011 + " and arrow " + [char]0x2192 + "." + [char]0x0A +
    "Ligature c" + [char]0x0153 + "ur and an emoji " + [char]::ConvertFromUtf32(0x1F680) + ".")
try {
    $v = @(Get-NotesTypographyViolations -Path $exoticPath)
    Assert-True ($v.Count -eq 4) "unanticipated characters flagged ($($v.Count) violations, expected 4)"
    $codePoints = @($v | ForEach-Object { $_.CodePoint })
    Assert-True ($codePoints -contains 'U+2011') 'non-breaking hyphen detected'
    Assert-True ($codePoints -contains 'U+2192') 'rightwards arrow detected'
    Assert-True ($codePoints -contains 'U+0153') 'oe ligature detected'
    Assert-True ($codePoints -contains 'U+1F680') 'emoji reported once at its real code point'
    $rocket = @($v | Where-Object { $_.CodePoint -eq 'U+1F680' })
    Assert-True ($rocket.Count -eq 1) "emoji reported exactly once ($($rocket.Count) records)"
} finally {
    Remove-Item -LiteralPath $exoticPath -Force
}

# ----------------------------------------------------------------------------
# Case 5: every tracked release-notes file must be clean, not one pinned version.
# The old suite hard-coded v2026.062101.md, so a dirty file added afterwards went
# unnoticed until publish day.
# ----------------------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$notesDir = Join-Path (Join-Path $repoRoot 'docs') 'release-notes'
if (Test-Path -LiteralPath $notesDir) {
    $notesFiles = @(Get-ChildItem -LiteralPath $notesDir -Filter '*.md' -File | Sort-Object Name)
    Assert-True ($notesFiles.Count -gt 0) "release-notes directory is not empty ($($notesFiles.Count) files)"
    $dirty = 0
    foreach ($file in $notesFiles) {
        $v = @(Get-NotesTypographyViolations -Path $file.FullName)
        if ($v.Count -gt 0) {
            $dirty++
            foreach ($item in $v) {
                Write-Host ("      {0}:{1}:{2}  {3} {4}" -f $file.Name, $item.Line, $item.Column, $item.CodePoint, $item.Name) -ForegroundColor DarkYellow
            }
        }
    }
    Assert-True ($dirty -eq 0) "all $($notesFiles.Count) tracked release-notes files are clean ($dirty dirty)"
} else {
    Write-Host "SKIP: $notesDir not found" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------------
# Case 6: a violation the author cannot act on is a violation reported badly.
# ----------------------------------------------------------------------------
$remedyPath = New-TempNotes ("Em-dash " + [char]0x2014 + " and an unlisted character " + [char]0x2603 + ".")
try {
    $v = @(Get-NotesTypographyViolations -Path $remedyPath)
    Assert-True ($v.Count -eq 2) "remedy sample flagged ($($v.Count) violations, expected 2)"
    $withoutRemedy = @($v | Where-Object { [string]::IsNullOrWhiteSpace($_.Remedy) })
    Assert-True ($withoutRemedy.Count -eq 0) "every violation carries a remedy ($($withoutRemedy.Count) without)"
    $unlisted = @($v | Where-Object { $_.CodePoint -eq 'U+2603' })
    Assert-True ($unlisted.Count -eq 1 -and -not [string]::IsNullOrWhiteSpace($unlisted[0].Remedy)) 'an unlisted character still gets the generic remedy'
} finally {
    Remove-Item -LiteralPath $remedyPath -Force
}

# ----------------------------------------------------------------------------
# Case 7: a leading byte-order mark must be reported, not swallowed. The bytes are
# written RAW on purpose: WriteAllText with a no-BOM encoding cannot produce this
# file, so it would test nothing. A reader that leaves BOM detection enabled eats
# the mark before the scan begins and calls the file clean.
# ----------------------------------------------------------------------------
$bomPath = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllBytes(
    $bomPath,
    ([byte[]]@(0xEF, 0xBB, 0xBF) + [System.Text.Encoding]::UTF8.GetBytes('Clean ASCII line.')))
try {
    $v = @(Get-NotesTypographyViolations -Path $bomPath)
    Assert-True ($v.Count -eq 1) "leading BOM flagged ($($v.Count) violations, expected 1)"
    # When the guard regresses, this case yields zero violations. Extract through the
    # pipeline and short-circuit before indexing so the failure prints as FAIL lines:
    # under Set-StrictMode -Version Latest, $v.CodePoint on an empty array throws
    # PropertyNotFoundStrict and would abort the run before the later cases report.
    $bomCodePoints = @($v | ForEach-Object { $_.CodePoint })
    Assert-True ($bomCodePoints -contains 'U+FEFF') 'BOM reported at code point U+FEFF'
    $bomAt = if ($v.Count -ge 1) { "$($v[0].Line):$($v[0].Column)" } else { 'no violation' }
    Assert-True ($v.Count -eq 1 -and $v[0].Line -eq 1 -and $v[0].Column -eq 1) "BOM located at line 1 column 1 (got $bomAt)"
} finally {
    Remove-Item -LiteralPath $bomPath -Force
}

Write-Host ''
if ($failures -gt 0) {
    Write-Host "$failures test(s) FAILED." -ForegroundColor Red
    exit 1
}
Write-Host 'All typography-guard tests passed.' -ForegroundColor Green
