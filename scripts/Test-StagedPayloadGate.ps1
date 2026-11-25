<#
.SYNOPSIS
    Tests the gate that refuses a staged build folder carrying unshipped files
    (scripts/StagedPayloadGate.ps1), and that Build.ps1 no longer makes the copy that
    put them there.

.DESCRIPTION
    The releases up to 2026.112201 shipped Assets\milkdown-editor, the
    source of the Notes editor, with the node_modules folder npm had installed on the
    build machine: 6591 files and 70 MB per edition. The application project excluded
    the tree; a later copy of the whole Assets folder in Build.ps1 put it back.

    The suite proves both directions, because a guard that only ever passes proves
    nothing:

      1. The real project names milkdown-editor as unshipped, and not drawio, which
         is excluded from one item and included again by another.
      2. A project that excludes nothing is refused rather than read as "nothing to
         check".
      3. A clean payload has no violation.
      4. The payload of November 2026 is refused: the editor source is reported
         once, and the node_modules below it is not a second finding.
      5. A node_modules folder anywhere else is reported too.
      6. Build.ps1 runs the gate and no longer copies the Assets folder of the
         working tree.

    SCOPE. Tests 1 to 5 cover the gate on fixture folders. Test 6 reads Build.ps1 as
    text: it shows the call and the copy's absence, not that a build produces a clean
    folder. Only a build shows that, and the gate then runs on its real output.

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
. (Join-Path $PSScriptRoot 'StagedPayloadGate.ps1')

$projectFile = Join-Path $repoRoot 'src\Heimdall.App\Heimdall.App.csproj'
$buildScript = Join-Path $repoRoot 'Build.ps1'
$editorSourceTree = 'milkdown-editor'
$conditionallyShippedTree = 'drawio'

$failures = New-Object System.Collections.Generic.List[string]
$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-payloadgate-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null

function New-FixtureFile {
    param([string]$Root, [string]$RelativePath)

    $path = Join-Path $Root $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'fixture' -Encoding ASCII
}

try {
    # 1. The real project: the editor source is unshipped, draw.io is not.
    $unshipped = Get-UnshippedAssetTrees -ProjectFile $projectFile
    if ($unshipped -notcontains $editorSourceTree) {
        $failures.Add("The project no longer names '$editorSourceTree' as an unshipped asset tree (found: $($unshipped -join ', ')).")
    }
    if ($unshipped -contains $conditionallyShippedTree) {
        $failures.Add("'$conditionallyShippedTree' is included again by its own Content item and ships in Release; the gate must not refuse it.")
    }

    # 2. A project that excludes nothing is refused, not read as an empty list.
    $bareProject = Join-Path $sandbox 'Bare.csproj'
    Set-Content -LiteralPath $bareProject -Encoding ASCII -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <Content Include="Assets\**\*" />
  </ItemGroup>
</Project>
'@
    $refused = $false
    try { Get-UnshippedAssetTrees -ProjectFile $bareProject | Out-Null }
    catch { $refused = $_.Exception.Message -like '*excludes an asset tree*' }
    if (-not $refused) {
        $failures.Add("A project with no excluded asset tree was accepted: the gate would pass with nothing to check.")
    }

    # 3. A clean payload: shipped trees only.
    $clean = Join-Path $sandbox 'clean'
    New-FixtureFile -Root $clean -RelativePath 'Heimdall.exe'
    New-FixtureFile -Root $clean -RelativePath 'Assets\milkdown\index.html'
    New-FixtureFile -Root $clean -RelativePath 'Assets\drawio\index.html'
    $cleanViolations = Get-StagedPayloadViolations -PayloadDirectory $clean -UnshippedAssetTrees $unshipped
    if ($cleanViolations.Count -ne 0) {
        $failures.Add("A clean payload was refused: $($cleanViolations -join ', ')")
    }

    # 4. The payload of November 2026: editor source with its node_modules.
    $leaked = Join-Path $sandbox 'leaked'
    New-FixtureFile -Root $leaked -RelativePath 'Heimdall.exe'
    New-FixtureFile -Root $leaked -RelativePath 'Assets\milkdown\index.html'
    New-FixtureFile -Root $leaked -RelativePath 'Assets\milkdown-editor\package.json'
    New-FixtureFile -Root $leaked -RelativePath 'Assets\milkdown-editor\node_modules\@esbuild\win32-x64\esbuild.exe'
    $leakedViolations = Get-StagedPayloadViolations -PayloadDirectory $leaked -UnshippedAssetTrees $unshipped
    $expected = Join-Path 'Assets' $editorSourceTree
    if ($leakedViolations.Count -ne 1 -or $leakedViolations[0] -ne $expected) {
        $failures.Add("The leaked payload should report exactly '$expected', got: $($leakedViolations -join ', ')")
    }

    # 5. A node_modules folder outside any unshipped tree is its own finding.
    $stray = Join-Path $sandbox 'stray'
    New-FixtureFile -Root $stray -RelativePath 'Heimdall.exe'
    New-FixtureFile -Root $stray -RelativePath 'Assets\vnc\node_modules\left-pad\index.js'
    $strayViolations = Get-StagedPayloadViolations -PayloadDirectory $stray -UnshippedAssetTrees $unshipped
    $expectedStray = 'Assets\vnc\node_modules'
    if ($strayViolations.Count -ne 1 -or $strayViolations[0] -ne $expectedStray) {
        $failures.Add("A stray node_modules should report exactly '$expectedStray', got: $($strayViolations -join ', ')")
    }

    # 6. Build.ps1 runs the gate, and the copy that defeated the project's exclusions is gone.
    $build = Get-Content -LiteralPath $buildScript -Raw
    if ($build -notmatch 'Get-StagedPayloadViolations\s+-PayloadDirectory\s+\$variantDir') {
        $failures.Add("Build.ps1 does not run Get-StagedPayloadViolations on the staged variant folder.")
    }
    if ($build -match "src\\Heimdall\.App\\Assets'") {
        $failures.Add("Build.ps1 names the Assets folder of the working tree again: copying it defeats the project's exclusions. dotnet publish already stages the assets the project includes.")
    }
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "FAILED" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host "PASSED: the staged payload gate refuses unshipped asset trees and package install folders." -ForegroundColor Green
exit 0
