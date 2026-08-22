<#
.SYNOPSIS
    Tests that the build's staging copy is idempotent and that its cleanup reaches
    paths Remove-Item cannot (Build.ps1: Copy-TreeContents, Remove-TreeRobust).

.DESCRIPTION
    Two defects met in the same place on 2026-08-22, and each made the other worse.

    Copy-Item -Recurse copies the source FOLDER into the destination when the
    destination already exists. The Assets staging did exactly that, so a second
    build of the same version produced Assets\Assets, a third Assets\Assets\Assets,
    each level duplicating the node_modules trees inside.

    That would have been harmless if the folder were cleaned first, and it was
    meant to be. But Remove-Item -Recurse -Force fails with "the directory is not
    empty" on trees deeper than MAX_PATH, which those node_modules trees are. The
    cleanup failed, the copy nested, and the build after that failed harder. A
    release had to be abandoned and Dist purged by hand.

    SCOPE. This suite covers the two helpers, not the build. It says nothing about
    whether Publish-Variant calls them, and a green run is not evidence that a
    build produces a correct layout.

    The suite proves both directions, because a guard that only ever passes proves
    nothing:

      1. Copying twice leaves one level, not two   -> the copy is idempotent.
      2. Copy-Item -Recurse on the same input DOES
         nest                                      -> the hazard is real, so test 1
                                                      is not asserting a tautology.
      3. A tree past MAX_PATH is removed           -> the cleanup reaches it.

    Test 3 also reports whether plain Remove-Item could have done it. On a machine
    with long paths enabled it can, and the report says so rather than pretending
    the comparison was made.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1, like Build.ps1 itself.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $repoRoot 'Build.ps1'

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "Build.ps1 not found next to this suite: $buildScript"
}

# Build.ps1 runs its whole pipeline when dot-sourced, so the two helpers are lifted
# out of it by parsing the file and re-declaring only those functions. Reading them
# from the real script is the point: a copy pasted here would drift.
$ast = [System.Management.Automation.Language.Parser]::ParseFile($buildScript, [ref]$null, [ref]$null)
$wanted = @('Remove-TreeRobust', 'Copy-TreeContents')
$found = @()
foreach ($name in $wanted) {
    $fn = $ast.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name },
        $true)
    if ($fn.Count -ne 1) {
        throw "Expected exactly one '$name' in Build.ps1, found $($fn.Count). The suite cannot test what it cannot find."
    }
    . ([scriptblock]::Create($fn[0].Extent.Text))
    $found += $name
}
Write-Host "Lifted from Build.ps1: $($found -join ', ')" -ForegroundColor DarkGray

$failures = New-Object System.Collections.Generic.List[string]
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-treecopy-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

try {
    $source = Join-Path $sandbox 'src'
    New-Item -ItemType Directory -Path (Join-Path $source 'nested') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'top.txt') -Value 'top' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $source 'nested\leaf.txt') -Value 'leaf' -Encoding ASCII

    # 1. Copying twice must leave one level, not two.
    $dest = Join-Path $sandbox 'dest'
    Copy-TreeContents -Source $source -Destination $dest
    Copy-TreeContents -Source $source -Destination $dest

    if (Test-Path -LiteralPath (Join-Path $dest 'src')) {
        $failures.Add("Copy-TreeContents nested the source folder inside the destination on the second run.")
    }
    if (-not (Test-Path -LiteralPath (Join-Path $dest 'nested\leaf.txt'))) {
        $failures.Add("Copy-TreeContents did not place the source contents at the destination root.")
    }
    $stray = @(Get-ChildItem -LiteralPath $dest -Directory | Where-Object { $_.Name -ne 'nested' })
    if ($stray.Count -ne 0) {
        $failures.Add("Copy-TreeContents left unexpected directories: $($stray.Name -join ', ')")
    }

    # 2. The hazard is real: the naive form DOES nest, so test 1 discriminates.
    # The destination is the NAMED target, as in Build.ps1 where it is
    # <variant>\Assets, not a parent directory. That is the shape that nests.
    $naiveDest = Join-Path $sandbox 'naiveDest'
    Copy-Item $source $naiveDest -Recurse -Force
    Copy-Item $source $naiveDest -Recurse -Force
    if (-not (Test-Path -LiteralPath (Join-Path $naiveDest 'src'))) {
        $failures.Add("Copy-Item -Recurse did not nest, so test 1 proves nothing on this platform.")
    }

    # 3. A tree past MAX_PATH is removed.
    $deep = Join-Path $sandbox 'deep'
    New-Item -ItemType Directory -Path $deep -Force | Out-Null
    $cursor = $deep
    while ($cursor.Length -lt 400) {
        $cursor = Join-Path $cursor 'node_modules_segment'
        New-Item -ItemType Directory -Path $cursor -Force -ErrorAction SilentlyContinue | Out-Null
        if (-not (Test-Path -LiteralPath $cursor)) { break }
    }
    Write-Host "Deepest path built: $($cursor.Length) characters" -ForegroundColor DarkGray

    $plainRemoveWorked = $false
    $probe = Join-Path $sandbox 'probe'
    Copy-TreeContents -Source $deep -Destination $probe
    try {
        Remove-Item -LiteralPath $probe -Recurse -Force -ErrorAction Stop
        $plainRemoveWorked = -not (Test-Path -LiteralPath $probe)
    }
    catch {
        $plainRemoveWorked = $false
    }

    if ($plainRemoveWorked) {
        Write-Host "Note: plain Remove-Item cleared the deep tree here, so long paths are enabled on this machine. Test 3 still asserts the helper works; it does not assert that the helper was necessary." -ForegroundColor DarkYellow
    }
    else {
        Write-Host "Confirmed: plain Remove-Item could NOT clear the deep tree." -ForegroundColor DarkGray
    }

    Remove-TreeRobust -Path $deep
    if (Test-Path -LiteralPath $deep) {
        $failures.Add("Remove-TreeRobust left the deep tree in place.")
    }

    # 4. Removing something that is not there is not an error.
    Remove-TreeRobust -Path (Join-Path $sandbox 'never-existed')
}
finally {
    if (Test-Path -LiteralPath $sandbox) {
        $mirror = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-treecopy-empty-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $mirror -Force | Out-Null
        & robocopy $mirror $sandbox /MIR /NFL /NDL /NJH /NJS /NC /NS /R:1 /W:1 | Out-Null
        $global:LASTEXITCODE = 0
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $mirror -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Host "FAILED" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host "PASSED: the staging copy is idempotent and the cleanup reaches deep trees." -ForegroundColor Green
exit 0
