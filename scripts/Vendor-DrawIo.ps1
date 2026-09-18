<#
.SYNOPSIS
    Re-vendors the embedded draw.io editor under src/Heimdall.App/Assets/drawio.

.DESCRIPTION
    Heimdall ships a subset of the upstream draw.io webapp, plus two edits of its
    own. Doing that by hand is how the tree came to report 24.8.6 while every
    manifest claimed 26.0.9, so the subset and the edits live here instead.

    The script takes an already-checked-out upstream tree (its src/main/webapp
    directory), copies exactly the paths listed in COPY_PATHS, re-applies both
    Heimdall edits, and rewrites the version row of VENDORED.md and of the two
    THIRD-PARTY-NOTICES files from the version the bundle itself reports.

    Get the upstream tree with:

        git clone --depth 1 --branch v<version> --filter=blob:none --sparse `
            https://github.com/jgraph/drawio.git drawio-<version>
        cd drawio-<version>
        git sparse-checkout set src/main/webapp

    After running this, smoke-test the tool end to end before committing: open,
    edit, save, Save As, export PNG, export SVG, shape picker, image picker,
    context menu. The assets only ship in Release builds.

.PARAMETER UpstreamWebapp
    Path to the upstream src/main/webapp directory.

.PARAMETER RepositoryRoot
    Repository root. Defaults to the parent of the directory holding this script.

.PARAMETER WhatIf
    Reports what would change without touching anything.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $UpstreamWebapp,

    [string] $RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The shipped subset. Anything not listed here is deliberately left out; see
# VENDORED.md for what each exclusion costs.
$COPY_DIRECTORIES = @(
    'styles',
    'images',
    'img',
    'mxgraph/css',
    'js/orgchart',
    'js/mermaid',
    'js/gliffy',
    'js/elk',
    'js/jquery'
)

$COPY_FILES = @(
    'index.html',
    'favicon.ico',
    'js/bootstrap.js',
    'js/main.js',
    'js/PreConfig.js',
    'js/PostConfig.js',
    'js/app.min.js',
    'js/extensions.min.js',
    'js/stencils.min.js',
    'js/shapes-14-6-5.min.js',
    'js/math-print.js',
    'js/orgchart.min.js',
    'resources/README.md',
    'resources/dia.txt',
    'resources/dia_fr.txt',
    'resources/dia_es.txt',
    'resources/dia_i18n.txt'
)

# Paths replaced wholesale on every run. Files Heimdall owns (heimdall-host.html,
# VENDORED.md) are never in this list.
$REPLACED_ROOTS = @('styles', 'images', 'img', 'mxgraph', 'js', 'resources', 'index.html', 'favicon.ico')

function Assert-Path {
    param([string] $Path, [string] $Description)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

Assert-Path -Path $UpstreamWebapp -Description 'Upstream webapp directory'
Assert-Path -Path (Join-Path $UpstreamWebapp 'js/app.min.js') -Description 'Upstream js/app.min.js'

$target = Join-Path $RepositoryRoot 'src/Heimdall.App/Assets/drawio'
Assert-Path -Path $target -Description 'Vendored draw.io directory'
Assert-Path -Path (Join-Path $target 'heimdall-host.html') -Description 'Heimdall iframe host page'

# Read the version before copying anything: an upstream tree that does not report
# one is not something to vendor blind.
$upstreamAppJs = Get-Content -LiteralPath (Join-Path $UpstreamWebapp 'js/app.min.js') -Raw
$versionMatch = [regex]::Match($upstreamAppJs, 'EditorUi\.VERSION\s*=\s*"(?<version>[0-9][0-9.]*)"')
if (-not $versionMatch.Success) {
    throw 'Could not read EditorUi.VERSION from the upstream js/app.min.js.'
}
$version = $versionMatch.Groups['version'].Value
Write-Host "Upstream draw.io version: $version"

if ($PSCmdlet.ShouldProcess($target, "Replace the vendored draw.io subset with $version")) {
    foreach ($root in $REPLACED_ROOTS) {
        $path = Join-Path $target $root
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    foreach ($directory in $COPY_DIRECTORIES) {
        $source = Join-Path $UpstreamWebapp $directory
        Assert-Path -Path $source -Description "Upstream directory '$directory'"

        $destination = Join-Path $target $directory
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
    }

    foreach ($file in $COPY_FILES) {
        $source = Join-Path $UpstreamWebapp $file
        Assert-Path -Path $source -Description "Upstream file '$file'"

        $destination = Join-Path $target $file
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    # Heimdall edit 1: expose the EditorUi instance to the iframe host page.
    $bootstrapPath = Join-Path $target 'js/bootstrap.js'
    $bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw
    $mainCall = [regex]::Matches($bootstrap, 'App\.main\(\);')
    if ($mainCall.Count -ne 1) {
        throw "Expected exactly one 'App.main();' call in bootstrap.js, found $($mainCall.Count). Re-apply the heimdallDrawioApp hook by hand."
    }
    $hook = @"
// Heimdall: expose the EditorUi instance to the iframe host (heimdall-host.html).
        App.main(function(app)
        {
            window.heimdallDrawioApp = app;
        });
"@
    $bootstrap = $bootstrap.Replace('App.main();', $hook.Trim())
    Set-Content -LiteralPath $bootstrapPath -Value $bootstrap -NoNewline -Encoding UTF8

    # Heimdall edit 2: a Content-Security-Policy that keeps every request local.
    $indexPath = Join-Path $target 'index.html'
    $index = Get-Content -LiteralPath $indexPath -Raw
    $charsetAnchor = '    <meta charset="utf-8">'
    if (-not $index.Contains($charsetAnchor)) {
        throw 'Could not find the charset meta tag in index.html. Re-apply the Content-Security-Policy by hand.'
    }
    $csp = @"
$charsetAnchor
    <!-- Heimdall: the editor runs offline inside WebView2; nothing may leave the local origin. -->
    <meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; media-src 'self' data: blob:; connect-src 'self' data: blob:; frame-src 'self' blob: data:; child-src 'self' blob: data:; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'none'">
"@
    $index = $index.Replace($charsetAnchor, $csp.Trim())
    Set-Content -LiteralPath $indexPath -Value $index -NoNewline -Encoding UTF8

    # The manifests must state the version the bundle actually reports.
    $manifests = @(
        (Join-Path $target 'VENDORED.md'),
        (Join-Path $RepositoryRoot 'THIRD-PARTY-NOTICES.md'),
        (Join-Path $RepositoryRoot 'THIRD-PARTY-NOTICES.fr.md')
    )

    foreach ($manifest in $manifests) {
        Assert-Path -Path $manifest -Description 'Manifest'

        $content = Get-Content -LiteralPath $manifest -Raw
        $updated = [regex]::Replace(
            $content,
            '(?m)^(?<prefix>\|\s*draw\.io embed\s*\|\s*)(?<version>[^|\s]+)(?<suffix>\s*\|)',
            { param($m) $m.Groups['prefix'].Value + $version + $m.Groups['suffix'].Value })

        if ($updated -ne $content) {
            Set-Content -LiteralPath $manifest -Value $updated -NoNewline -Encoding UTF8
            Write-Host "Updated the draw.io version row in $(Split-Path -Leaf $manifest)."
        }
        else {
            Write-Warning "No draw.io version row updated in $(Split-Path -Leaf $manifest); check it by hand."
        }
    }

    $fileCount = (Get-ChildItem -LiteralPath $target -Recurse -File).Count
    Write-Host "Vendored draw.io $version: $fileCount files."
    Write-Host 'Now run the Diagram Editor smoke test and the DiagramEditorGuardTests before committing.'
}
