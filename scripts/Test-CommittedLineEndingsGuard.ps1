<#
.SYNOPSIS
    Tests the committed line-endings guard (scripts/CommittedLineEndingsGuard.ps1).

.DESCRIPTION
    The guard is proven in both directions on throwaway repositories, because a
    gate that only ever passes proves nothing:

      1. A clean LF blob under text=auto        -> no violation, and the listing
                                                   was actually read (a parser that
                                                   matched nothing would be green).
      2. A CRLF blob forced into the index      -> the column rule reports it
         past the clean filter                     (`i/crlf`), as the old grep did.
      3. A text file with one lone carriage     -> git classes it `-text` under
         return and CRLF lines, added normally     `attr/text=auto`, the column rule
                                                   is silent, and the BYTES rule
                                                   reports it. This is BL-0100: the
                                                   blind spot the old grep had, and
                                                   the test asserts the old filter
                                                   would indeed have missed it.
      4. A declared binary holding the bytes    -> no violation: an explicit
         0D 0A                                     `binary` attribute is trusted.
      5. The repository this script lives in    -> no violation: the guard must be
                                                   green on the tree it ships in.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-CommittedLineEndingsGuard.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CommittedLineEndingsGuard.ps1')

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

function New-FixtureRepository {
    <#
    .SYNOPSIS
        A fresh repository with the attributes this project uses: text=auto, PNG binary.
    #>
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-eolguard-" + [System.Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $path -Force
    & git -C $path init -q
    & git -C $path config user.name 'fixture'
    & git -C $path config user.email ''
    & git -C $path config core.autocrlf false
    [System.IO.File]::WriteAllBytes((Join-Path $path '.gitattributes'), [System.Text.Encoding]::ASCII.GetBytes("* text=auto`n*.png binary`n"))
    & git -C $path add .gitattributes
    return $path
}

function Remove-FixtureRepository {
    param([string]$Path)
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
}

function Write-FixtureBytes {
    param([string]$Repository, [string]$Name, [byte[]]$Bytes)
    [System.IO.File]::WriteAllBytes((Join-Path $Repository $Name), $Bytes)
}

function Get-EolListing {
    param([string]$Repository)
    return @(& git -C $Repository ls-files --eol)
}

$ascii = [System.Text.Encoding]::ASCII

# 1. A clean LF text file: nothing to report, and something was read.
$repo = New-FixtureRepository
try {
    Write-FixtureBytes -Repository $repo -Name 'clean.txt' -Bytes $ascii.GetBytes("one`ntwo`n")
    & git -C $repo add clean.txt
    $violations = @(Get-CommittedLineEndingViolations -RepositoryPath $repo)
    Assert-True ($violations.Count -eq 0) 'a clean LF blob under text=auto is not a violation'
    Assert-True ((Get-EolListing -Repository $repo).Count -ge 2) 'the fixture listing holds the entries the guard walked'
}
finally { Remove-FixtureRepository -Path $repo }

# 2. A CRLF text blob forced past the clean filter: the column rule reports it.
$repo = New-FixtureRepository
try {
    $crlfPath = Join-Path $repo 'forced.txt'
    Write-FixtureBytes -Repository $repo -Name 'forced.txt' -Bytes $ascii.GetBytes("one`r`ntwo`r`n")
    $sha = (& git -C $repo hash-object -w --no-filters -- $crlfPath).Trim()
    & git -C $repo update-index --add --cacheinfo "100644,$sha,forced.txt"
    $violations = @(Get-CommittedLineEndingViolations -RepositoryPath $repo)
    Assert-True ($violations.Count -eq 1 -and $violations[0].Path -eq 'forced.txt' -and $violations[0].Rule -eq 'column') `
        'a CRLF blob forced into the index is reported by the column rule'
}
finally { Remove-FixtureRepository -Path $repo }

# 3. BL-0100: a lone carriage return makes git class the file binary; the old grep is
#    silent, the bytes rule is not.
$repo = New-FixtureRepository
try {
    Write-FixtureBytes -Repository $repo -Name 'stray.txt' -Bytes $ascii.GetBytes("line1`r`nline2`r`nstray`rline3`r`n")
    & git -C $repo add stray.txt
    $listing = Get-EolListing -Repository $repo
    $strayLine = @($listing | Where-Object { $_ -like "*`tstray.txt" })
    Assert-True ($strayLine.Count -eq 1 -and $strayLine[0] -like 'i/-text*' -and $strayLine[0] -like '*attr/text=auto*') `
        'git classes the lone-carriage-return file as binary under text=auto (the blind spot exists)'
    $oldGrepHits = @($listing | Where-Object { $_ -match '^i/(crlf|mixed)' })
    Assert-True ($oldGrepHits.Count -eq 0) 'the old column-only filter would have missed it'
    $violations = @(Get-CommittedLineEndingViolations -RepositoryPath $repo)
    Assert-True ($violations.Count -eq 1 -and $violations[0].Path -eq 'stray.txt' -and $violations[0].Rule -eq 'bytes') `
        'the bytes rule reports the CRLF blob git classed binary'
}
finally { Remove-FixtureRepository -Path $repo }

# 4. A declared binary holding the bytes 0D 0A is trusted.
$repo = New-FixtureRepository
try {
    Write-FixtureBytes -Repository $repo -Name 'image.png' -Bytes ([byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x0D, 0x0A))
    & git -C $repo add image.png
    $violations = @(Get-CommittedLineEndingViolations -RepositoryPath $repo)
    Assert-True ($violations.Count -eq 0) 'a blob declared binary by attribute is exempt from the bytes rule'
}
finally { Remove-FixtureRepository -Path $repo }

# 5. The repository this guard ships in is clean under both rules.
$repositoryPath = Split-Path -Parent $PSScriptRoot
$selfViolations = @(Get-CommittedLineEndingViolations -RepositoryPath $repositoryPath)
Assert-True ($selfViolations.Count -eq 0) "the guard is green on this repository ($($selfViolations.Count) violations)"
foreach ($violation in $selfViolations) {
    Write-Host ("  {0} {1} {2} [{3}]" -f $violation.Index, $violation.Attribute, $violation.Path, $violation.Rule) -ForegroundColor Red
}

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the committed line-endings guard measures the bytes, not only the column.' -ForegroundColor Green
exit 0
