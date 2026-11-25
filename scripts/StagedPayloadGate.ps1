<#
.SYNOPSIS
    Refuses a staged build folder that carries files the application project keeps out
    of the product.

.DESCRIPTION
    Dot-source this file to get Get-UnshippedAssetTrees and Get-StagedPayloadViolations.

    The ZIP, the Inno Setup installer and the MSI are all built from one staged folder,
    and each takes everything in it. The application project is the one place that says
    which asset trees are product files: it excludes Assets\milkdown-editor, the source
    the Notes editor bundle is built from.

    Until November 2026 the build copied the whole Assets folder of the working tree
    over the published one. That copy knew nothing of the project's exclusions and
    nothing of git either: it carried the editor source and the node_modules folder
    npm had installed beside it on the build machine. Version 2026.112201 shipped 6591
    such files (70 MB, a third-party esbuild.exe among them) in every edition, and so
    did every earlier build still on disk to compare, back to 2026.111201.

    The copy is gone. This gate is what keeps it gone: it reads the exclusions from the
    project file, so the project stays the single source of truth, and it reports

      - an asset tree the project excludes and never includes again;
      - any node_modules directory, wherever it is: a package manager's install
        folder is a build input on every platform, never a product file.

    Build.ps1 runs it on each staged folder before anything is archived, in every mode.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Kept compatible with Windows PowerShell 5.1: Build.ps1 is invoked through
    powershell.exe, so no PowerShell 7 only syntax here.
#>

# The folder of the application project whose first-level directories are asset trees.
$script:AssetsFolderName = 'Assets'

# A package manager's install folder. Never part of a product, at any depth.
$script:PackageInstallFolderName = 'node_modules'

# An item pattern that names one whole first-level asset tree: Assets\<tree>\**
$script:WholeAssetTreePattern = '^\s*' + [regex]::Escape($script:AssetsFolderName) + '[\\/](?<tree>[^\\/*;]+)[\\/]\*\*\s*$'

function Get-WholeAssetTreeNames {
    <#
    .SYNOPSIS
        Returns the asset trees a semicolon-separated item specification names whole.
    #>
    param([string]$Specification)

    $names = @()
    if ([string]::IsNullOrWhiteSpace($Specification)) { return , $names }

    foreach ($part in $Specification.Split(';')) {
        $match = [regex]::Match($part, $script:WholeAssetTreePattern)
        if ($match.Success) { $names += $match.Groups['tree'].Value }
    }
    return , $names
}

function Get-UnshippedAssetTrees {
    <#
    .SYNOPSIS
        Reads from the application project the asset trees that are never product files.
    .DESCRIPTION
        A tree is unshipped when a Content item excludes it whole (Assets\<tree>\**) and
        no Content item includes it again. Assets\drawio is excluded from the general
        item and included again by its own, conditional one: it ships, so it is not
        returned.

        Only the form Assets\<tree>\** is read, on either attribute. Throws when no
        Content item excludes a tree in that form: the gate would then pass by having
        nothing to check.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    if (-not (Test-Path -LiteralPath $ProjectFile)) {
        throw "Application project not found: $ProjectFile"
    }

    [xml]$project = Get-Content -LiteralPath $ProjectFile -Raw
    $contentItems = @($project.SelectNodes('//Content'))

    $excluded = @()
    $included = @()
    foreach ($item in $contentItems) {
        $excluded += Get-WholeAssetTreeNames -Specification $item.GetAttribute('Exclude')
        $included += Get-WholeAssetTreeNames -Specification $item.GetAttribute('Include')
    }

    if ($excluded.Count -eq 0) {
        throw "No Content item of '$ProjectFile' excludes an asset tree. The staged payload gate reads its list there and cannot run without one."
    }

    $unshipped = @($excluded | Where-Object { $included -notcontains $_ } | Sort-Object -Unique)
    return , $unshipped
}

function Get-StagedPayloadViolations {
    <#
    .SYNOPSIS
        Lists what a staged build folder holds that must not ship.
    .DESCRIPTION
        Returns paths relative to the payload folder, empty when the payload is clean.
        A package install folder is reported once, at its top: what is below it is the
        same finding.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$PayloadDirectory,

        [string[]]$UnshippedAssetTrees = @()
    )

    if (-not (Test-Path -LiteralPath $PayloadDirectory)) {
        throw "Staged payload folder not found: $PayloadDirectory"
    }

    $root = (Resolve-Path -LiteralPath $PayloadDirectory).ProviderPath.TrimEnd('\', '/')
    $violations = @()

    foreach ($tree in $UnshippedAssetTrees) {
        $relative = Join-Path $script:AssetsFolderName $tree
        if (Test-Path -LiteralPath (Join-Path $root $relative)) {
            $violations += $relative
        }
    }

    # Walked level by level and never into a finding: the trees below a package install
    # folder are the ones whose paths exceed MAX_PATH, which a recursive listing under
    # Windows PowerShell 5.1 would stop on before anything was reported.
    $pending = New-Object System.Collections.Generic.Queue[string]
    $pending.Enqueue($root)
    while ($pending.Count -gt 0) {
        $current = $pending.Dequeue()
        foreach ($directory in @(Get-ChildItem -LiteralPath $current -Directory -Force | Sort-Object Name)) {
            $relative = $directory.FullName.Substring($root.Length + 1)
            if ($violations -contains $relative) { continue }
            if ($directory.Name -eq $script:PackageInstallFolderName) {
                $violations += $relative
                continue
            }
            $pending.Enqueue($directory.FullName)
        }
    }

    return , $violations
}
