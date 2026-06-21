<#
.SYNOPSIS
    Tests the release-notes typography guard (scripts/NotesTypographyGuard.ps1).

.DESCRIPTION
    Exercises Get-NotesTypographyViolations against three inputs:
      1. Notes with banned smart punctuation        -> must report violations.
      2. Notes with French accents and guillemets    -> must be clean (allowed).
      3. The tracked release notes for the current
         shipped version                             -> must be clean.
    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
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

# ----------------------------------------------------------------------------
# Case 1: banned smart punctuation must be detected.
# ----------------------------------------------------------------------------
$badPath = [System.IO.Path]::GetTempFileName()
try {
    $bad = "First line is clean ASCII." + [char]0x0A +
           "An em-dash " + [char]0x2014 + " here." + [char]0x0A +
           "Curly " + [char]0x201C + "quote" + [char]0x201D + " and apostrophe " + [char]0x2019 + "." + [char]0x0A +
           "Ellipsis" + [char]0x2026 + " plus a NBSP" + [char]0x00A0 + "and a narrow" + [char]0x202F + "and thin" + [char]0x2009 + "spaces."
    [System.IO.File]::WriteAllText($badPath, $bad, $utf8NoBom)
    $v = @(Get-NotesTypographyViolations -Path $badPath)
    Assert-True ($v.Count -ge 7) "bad notes flagged ($($v.Count) violations, expected >= 7)"
    $kinds = ($v | ForEach-Object { $_.CodePoint } | Sort-Object -Unique) -join ','
    Assert-True ($v.CodePoint -contains 'U+2014') "em-dash detected (kinds: $kinds)"
    Assert-True ($v.CodePoint -contains 'U+202F') "narrow no-break space detected"
} finally {
    Remove-Item -LiteralPath $badPath -Force
}

# ----------------------------------------------------------------------------
# Case 2: French accents and guillemets with normal spaces are allowed.
# ----------------------------------------------------------------------------
$goodPath = [System.IO.Path]::GetTempFileName()
try {
    # e-acute, a-grave, e-grave, c-cedilla, and U+00AB/U+00BB guillemets with
    # ordinary U+0020 spaces inside - all legitimate, must not be flagged.
    $good = "Recuperation verifiee " + [char]0x00E9 + [char]0x00E0 + [char]0x00E8 + [char]0x00E7 + "." + [char]0x0A +
            "Le bouton " + [char]0x00AB + " Tester " + [char]0x00BB + " utilise le delai - et un tiret ASCII."
    [System.IO.File]::WriteAllText($goodPath, $good, $utf8NoBom)
    $v = @(Get-NotesTypographyViolations -Path $goodPath)
    Assert-True ($v.Count -eq 0) "accents + guillemets + normal spaces are clean ($($v.Count) violations)"
} finally {
    Remove-Item -LiteralPath $goodPath -Force
}

# ----------------------------------------------------------------------------
# Case 3: the tracked shipped release notes must already be clean.
# ----------------------------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
$shipped = Join-Path $repoRoot 'docs\release-notes\v2026.062101.md'
if (Test-Path -LiteralPath $shipped) {
    $v = @(Get-NotesTypographyViolations -Path $shipped)
    Assert-True ($v.Count -eq 0) "shipped notes v2026.062101.md are clean ($($v.Count) violations)"
} else {
    Write-Host "SKIP: $shipped not found" -ForegroundColor Yellow
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "$failures test(s) FAILED." -ForegroundColor Red
    exit 1
}
Write-Host "All typography-guard tests passed." -ForegroundColor Green
