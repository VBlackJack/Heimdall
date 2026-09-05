<#
.SYNOPSIS
    Refuses tracked blobs stored with CRLF, by measuring their bytes and not only
    the column git prints for them.

.DESCRIPTION
    Every text file is stored with LF in this repository (see .gitattributes) and
    checked out with the line ending its tool chain expects. A blob committed with
    CRLF bypasses that clean filter and stays dirty for the life of the branch, so
    CI refuses it.

    The first version of this gate was one grep over `git ls-files --eol`:

        grep -E '^i/(crlf|mixed)'

    It had a blind spot, measured on 2026-08-26 (BL-0100). git classes a blob as
    binary from its CONTENT - a NUL byte, too many control characters, or a lone
    carriage return - and a blob it classes binary is neither normalized nor
    reported as `i/crlf`: the column reads `i/-text`, the same as a PNG, while the
    bytes carry CRLF on every line. A stray `\r` in one line of a 34 KB text file
    was enough, `git diff --stat` still counted its lines as text, and the gate
    said nothing.

    So this guard applies two rules over the index:

      1. The column rule, unchanged: `i/crlf` or `i/mixed` is a violation.
      2. The byte rule, new: a blob git classed `-text` WITHOUT an explicit
         `-text` or `binary` attribute is read from the index, and it is a
         violation when it holds no NUL byte and holds a CRLF. A blob declared
         binary in .gitattributes is trusted: that declaration is a decision, and
         a real binary can legitimately contain the bytes 0D 0A.

    The attribute column decides the exemption, never the file name: a data file
    added tomorrow under a new extension gets the byte rule for free.

    Dot-source this file to get Get-CommittedLineEndingViolations; run it to check
    the repository it lives in and exit 1 on the first violation list.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
Set-StrictMode -Version Latest

function Get-IndexedBlobBytes {
    <#
    .SYNOPSIS
        The exact bytes of one blob as stored in the index.
    .DESCRIPTION
        Read through a process handle rather than a PowerShell pipeline, which
        would decode the bytes as text and lose the very thing being measured.
    #>
    param(
        [Parameter(Mandatory)] [string] $RepositoryPath,
        [Parameter(Mandatory)] [string] $Path
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'git'
    $startInfo.Arguments = 'cat-file blob ":' + ($Path -replace '"', '\"') + '"'
    $startInfo.WorkingDirectory = $RepositoryPath
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $buffer = New-Object System.IO.MemoryStream
        $process.StandardOutput.BaseStream.CopyTo($buffer)
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed for '$Path': $stderr"
        }
        return $buffer.ToArray()
    }
    finally {
        $process.Dispose()
    }
}

function Test-BytesLookLikeTextWithCrlf {
    <#
    .SYNOPSIS
        Whether a byte sequence is text-like (no NUL) and carries at least one CRLF.
    #>
    param([Parameter(Mandatory)] [byte[]] $Bytes)

    $sawCr = $false
    foreach ($b in $Bytes) {
        if ($b -eq 0) { return $false }
        if ($b -eq 13) { $sawCr = $true; continue }
        if ($b -eq 10 -and $sawCr) { return $true }
        $sawCr = $false
    }
    return $false
}

function Get-CommittedLineEndingViolations {
    <#
    .SYNOPSIS
        Every tracked blob stored with CRLF in the index of a repository.
    .OUTPUTS
        One object per violation: Path, Index (the `i/` column), Attribute (the
        `attr/` column) and Rule ('column' or 'bytes').
    #>
    param([Parameter(Mandatory)] [string] $RepositoryPath)

    $listing = & git -C $RepositoryPath ls-files --eol 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files --eol failed in '$RepositoryPath': $listing"
    }

    $violations = @()
    foreach ($line in @($listing)) {
        $text = [string]$line
        $tab = $text.IndexOf([char]9)
        if ($tab -lt 0) { continue }
        $columns = $text.Substring(0, $tab).Trim() -split '\s+'
        $path = $text.Substring($tab + 1)
        if ($columns.Count -lt 3) { continue }
        $index = $columns[0]
        $attribute = $columns[2]

        if ($index -eq 'i/crlf' -or $index -eq 'i/mixed') {
            $violations += [PSCustomObject]@{
                Path = $path; Index = $index; Attribute = $attribute; Rule = 'column'
            }
            continue
        }

        # The blind spot: classed binary by content, not declared binary by attribute.
        if ($index -eq 'i/-text' -and $attribute -ne 'attr/-text') {
            $bytes = Get-IndexedBlobBytes -RepositoryPath $RepositoryPath -Path $path
            if (Test-BytesLookLikeTextWithCrlf -Bytes $bytes) {
                $violations += [PSCustomObject]@{
                    Path = $path; Index = $index; Attribute = $attribute; Rule = 'bytes'
                }
            }
        }
    }

    return $violations
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    $repositoryPath = Split-Path -Parent $PSScriptRoot
    $found = @(Get-CommittedLineEndingViolations -RepositoryPath $repositoryPath)
    if ($found.Count -gt 0) {
        Write-Host 'Committed blobs must be stored with LF (see .gitattributes):' -ForegroundColor Red
        foreach ($violation in $found) {
            Write-Host ("  {0} {1} {2}  [{3}]" -f $violation.Index, $violation.Attribute, $violation.Path, $violation.Rule) -ForegroundColor Red
        }
        Write-Host ''
        Write-Host 'These blobs were committed with CRLF, bypassing the clean filter. A blob'
        Write-Host 'reported by the bytes rule was classed binary by git from its content -'
        Write-Host 'typically a lone carriage return - so it was never normalized.'
        Write-Host 'Do not fix this by replaying the offending commit: a pure line-ending'
        Write-Host 'renormalization cannot be rebased, and the branch stays permanently dirty.'
        Write-Host 'Regenerate it instead - rebase --onto the target branch, stop after the'
        Write-Host 'offending commit, then run git add --renormalize on the offending paths and'
        Write-Host 'commit that. For a bytes-rule blob, remove the stray carriage return first.'
        exit 1
    }
    Write-Host 'All tracked blobs are stored with LF.' -ForegroundColor Green
    exit 0
}
