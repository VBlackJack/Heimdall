<#
.SYNOPSIS
    Tests that Build.bat and Test.bat hand the failure of the command they wrap to
    their caller.

.DESCRIPTION
    Both files ended with a bare `pause`, and `pause` succeeds: the batch file exited 0
    whatever the build or the tests had done, so a script or a scheduled task calling
    them saw a green run over a red one. They now keep the status in %RC% before the
    pause and exit with it.

    Each batch file is copied into a sandbox beside a stand-in for the command it
    wraps (a Build.ps1 that exits 4, a dotnet.exe that exits 3) and run through
    cmd.exe with stdin closed, so `pause` returns at once. The cases:

      1. Build.bat over a build that fails (exit 4)    -> Build.bat exits 4.
      2. Build.bat over a build that succeeds          -> Build.bat exits 0.
      3. Test.bat over tests that fail (exit 3)        -> Test.bat exits 3.
      4. Test.bat over tests that pass                 -> Test.bat exits 0.

    The failing cases are the ones that tell the old and the new file apart; the
    passing cases keep the suite from going green on a batch file that always fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-BatchExitCodes.ps1
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "PASS: $Message" -ForegroundColor Green
    } else {
        Write-Host "FAIL: $Message" -ForegroundColor Red
        $script:failures.Add($Message)
    }
}

# Runs one batch file in a fresh sandbox beside the given stand-ins and returns its exit code.
function Invoke-BatchInSandbox {
    param(
        [Parameter(Mandatory)] [string]$BatchName,
        [Parameter(Mandatory)] [hashtable]$StandIns,
        [hashtable]$Copies = @{},
        [int]$FakeExit = 0
    )

    $source = Join-Path $repoRoot $BatchName
    if (-not (Test-Path -LiteralPath $source)) {
        throw "$BatchName not found at $source. The suite cannot test what it cannot find."
    }

    $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-batch-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    try {
        Copy-Item -LiteralPath $source -Destination (Join-Path $sandbox $BatchName)
        foreach ($name in $StandIns.Keys) {
            Set-Content -LiteralPath (Join-Path $sandbox $name) -Value $StandIns[$name] -Encoding ASCII
        }
        foreach ($name in $Copies.Keys) {
            Copy-Item -LiteralPath $Copies[$name] -Destination (Join-Path $sandbox $name)
        }

        # The sandbox leads PATH so its dotnet.exe shadows the real dotnet. Not left to the
        # current-directory lookup of cmd.exe: NoDefaultCurrentDirectoryInExePath turns it off.
        $savedPath = $env:PATH
        $env:PATH = "$sandbox;$savedPath"
        $env:HEIMDALL_FAKE_EXIT = [string]$FakeExit
        try {
            $process = Start-Process -FilePath 'cmd.exe' `
                -ArgumentList @('/c', (Join-Path $sandbox $BatchName)) `
                -WorkingDirectory $sandbox `
                -RedirectStandardInput (Join-Path $sandbox 'stdin.txt') `
                -RedirectStandardOutput (Join-Path $sandbox 'stdout.txt') `
                -RedirectStandardError (Join-Path $sandbox 'stderr.txt') `
                -NoNewWindow -Wait -PassThru
        } finally {
            $env:PATH = $savedPath
            Remove-Item Env:\HEIMDALL_FAKE_EXIT -ErrorAction SilentlyContinue
        }
        return $process.ExitCode
    } finally {
        Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Start-Process wants the redirected stdin file to exist before it starts, so each
# sandbox gets an empty one: `pause` reads it to its end and returns.
function Invoke-Case {
    param([string]$BatchName, [hashtable]$StandIns = @{}, [hashtable]$Copies = @{}, [int]$FakeExit = 0)
    $withStdin = @{} + $StandIns
    $withStdin['stdin.txt'] = ''
    return Invoke-BatchInSandbox -BatchName $BatchName -StandIns $withStdin -Copies $Copies -FakeExit $FakeExit
}

# A stand-in for dotnet.exe that exits with the code in HEIMDALL_FAKE_EXIT. It has to be a
# real executable: a batch file runs a .cmd by transferring control to it without `call`,
# so a dotnet.cmd stand-in would end the whole batch with its own exit code and hide
# exactly what this suite measures. Built once, by Windows PowerShell 5.1 (which can compile
# an executable; PowerShell 7 cannot).
$fakeDotnet = Join-Path ([System.IO.Path]::GetTempPath()) ("heimdall-fake-dotnet-" + [Guid]::NewGuid().ToString('N') + '.exe')
$fakeSource = 'public static class FakeDotnet { public static int Main() { return int.Parse(System.Environment.GetEnvironmentVariable("HEIMDALL_FAKE_EXIT")); } }'
& powershell.exe -NoProfile -Command "Add-Type -OutputType ConsoleApplication -OutputAssembly '$fakeDotnet' -TypeDefinition '$fakeSource'"
if (-not (Test-Path -LiteralPath $fakeDotnet)) {
    throw "The stand-in dotnet.exe could not be built, so Test.bat cannot be measured."
}

$buildFails = Invoke-Case -BatchName 'Build.bat' -StandIns @{ 'Build.ps1' = 'exit 4' }
Assert-True ($buildFails -eq 4) "Build.bat exits with the failing build's code 4 (got $buildFails)"

$buildPasses = Invoke-Case -BatchName 'Build.bat' -StandIns @{ 'Build.ps1' = 'exit 0' }
Assert-True ($buildPasses -eq 0) "Build.bat exits 0 over a build that succeeds (got $buildPasses)"

$testsFail = Invoke-Case -BatchName 'Test.bat' -Copies @{ 'dotnet.exe' = $fakeDotnet } -FakeExit 3
Assert-True ($testsFail -eq 3) "Test.bat exits with the failing run's code 3 (got $testsFail)"

$testsPass = Invoke-Case -BatchName 'Test.bat' -Copies @{ 'dotnet.exe' = $fakeDotnet } -FakeExit 0
Assert-True ($testsPass -eq 0) "Test.bat exits 0 over tests that pass (got $testsPass)"

Remove-Item -LiteralPath $fakeDotnet -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "$($failures.Count) expectation(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All batch exit-code expectations passed." -ForegroundColor Green
exit 0
