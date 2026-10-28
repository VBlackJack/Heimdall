<#
.SYNOPSIS
    Tests the Inno Setup compile step (Invoke-InnoSetupCompile in
    scripts/InnoSetupCompile.ps1, called by Build.ps1).

.DESCRIPTION
    The compile used to pipe ISCC into Out-Null, so a failed compile said nothing of why.
    A stand-in compiler (a .cmd that prints its arguments and exits with a chosen code)
    stands in for ISCC, selected the way the build selects the real one, through a path:

      1. A compiler that prints a message and exits 2  -> exit code 2 AND the message in
                                                          the result. The old form kept
                                                          neither.
      2. A compiler that exits 0                       -> exit code 0.
      3. The three defines and the script path         -> reach the compiler, so the
                                                          stand-in proves the arguments
                                                          are the ones ISCC needs.
      4. A message on stderr                           -> captured, and no throw under
                                                          $ErrorActionPreference = 'Stop'.
      5. Build.ps1 goes through the function, prints   -> the function is wired in, not
         what it returns and has no Out-Null on ISCC      merely correct.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-InnoSetupCompile.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'InnoSetupCompile.ps1')

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

$sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-iscc-test-" + [System.Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $sandbox -Force

try {
    # The stand-in compiler: prints the arguments it received, then a diagnostic, and
    # exits with the code in the first line of exit-code.txt beside it.
    $fake = Join-Path $sandbox 'fake-iscc.cmd'
    Set-Content -LiteralPath $fake -Encoding ASCII -Value @(
        '@echo off',
        'echo ARGS: %*',
        'echo Error on line 12: stand-in compiler diagnostic',
        'echo stand-in stderr line 1>&2',
        'set /p CODE=<"%~dp0exit-code.txt"',
        'exit /b %CODE%'
    )
    $exitFile = Join-Path $sandbox 'exit-code.txt'

    Set-Content -LiteralPath $exitFile -Value '2' -Encoding ASCII
    $failed = Invoke-InnoSetupCompile -IsccPath $fake -ScriptPath 'installer\innosetup.iss' `
        -AppVersion '2026.123456' -Variant 'Standard' -SourceDir 'C:\some dir\publish'
    $text = $failed.Lines -join "`n"

    Assert-True ($failed.ExitCode -eq 2) "A failing compile reports its exit code 2 (got $($failed.ExitCode))"
    Assert-True ($text -match 'Error on line 12: stand-in compiler diagnostic') 'A failing compile keeps the compiler message'
    Assert-True ($text -match 'stand-in stderr line') 'A message on stderr is captured without throwing under Stop'
    Assert-True ($text -match '/DAppVersion=2026\.123456') 'The version define reaches the compiler'
    Assert-True ($text -match '/DVariant=Standard') 'The variant define reaches the compiler'
    Assert-True ($text -match '/DSourceDir=C:\\some dir\\publish') 'The source directory define reaches the compiler, spaces included'
    Assert-True ($text -match 'installer\\innosetup\.iss') 'The script path reaches the compiler'

    Set-Content -LiteralPath $exitFile -Value '0' -Encoding ASCII
    $passed = Invoke-InnoSetupCompile -IsccPath $fake -ScriptPath 'installer\innosetup.iss' `
        -AppVersion '2026.123456' -Variant 'SelfContained' -SourceDir 'C:\publish'
    Assert-True ($passed.ExitCode -eq 0) "A compile that succeeds reports exit code 0 (got $($passed.ExitCode))"
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. Build.ps1 uses the function and prints what it returns.
$build = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1') -Raw
Assert-True ($build -match 'InnoSetupCompile\.ps1') 'Build.ps1 dot-sources scripts/InnoSetupCompile.ps1'
Assert-True ($build -match 'Invoke-InnoSetupCompile\s+-IsccPath') 'Build.ps1 compiles through Invoke-InnoSetupCompile'
Assert-True ($build -match '\$compile\.Lines') 'Build.ps1 prints the compiler output it captured'
Assert-True (-not ($build -match '\$iscc[^\r\n]*Out-Null')) 'Build.ps1 no longer pipes ISCC into Out-Null'

if ($failures -gt 0) {
    Write-Host ""
    Write-Host "$failures expectation(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All Inno Setup compile expectations passed." -ForegroundColor Green
exit 0
