<#
.SYNOPSIS
    Tests the MSI ProductVersion derivation (ConvertTo-MsiProductVersion in
    scripts/BuildVersioning.ps1).

.DESCRIPTION
    Windows Installer compares the first three fields of ProductVersion and caps
    them (255, 255, 65535). The MSI used to receive 1.0.MMDD.xx: the year was not
    in it, so the first 2027 build would compare older than an October 2026 one,
    and the sequence sat in the ignored fourth field, so two releases of one day
    compared equal.

      1. A sequence of build numbers across a day, a month and a year  -> strictly
                                                                        increasing.
      2. Every value against the legacy 1.0.1231.99 form               -> above it, so
                                                                        an older MSI
                                                                        is upgraded.
      3. Every field                                                   -> within the
                                                                        MSI limits.
      4. A malformed build number                                      -> throws.
      5. Build.ps1 passes the derived value to WiX                     -> the function
                                                                        is wired in,
                                                                        not merely
                                                                        correct.

    Exits non-zero if any expectation fails.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0

    Run on Windows with:  pwsh -NoProfile -File scripts\Test-MsiProductVersion.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'BuildVersioning.ps1')

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

# Windows Installer compares the three fields numerically, left to right.
function Compare-MsiVersion {
    param([string]$Left, [string]$Right)
    $l = @($Left.Split('.') | Select-Object -First 3 | ForEach-Object { [int]$_ })
    $r = @($Right.Split('.') | Select-Object -First 3 | ForEach-Object { [int]$_ })
    for ($i = 0; $i -lt 3; $i++) {
        if ($l[$i] -ne $r[$i]) { return [Math]::Sign($l[$i] - $r[$i]) }
    }
    return 0
}

# 1. Same day, next day, end of year, next year.
$ordered = @('2026.102701', '2026.102702', '2026.102801', '2026.123101', '2027.010501')
$versions = @($ordered | ForEach-Object { ConvertTo-MsiProductVersion -BuildNumber $_ })
for ($i = 0; $i -lt $ordered.Count; $i++) {
    Write-Host ("      {0} -> {1}" -f $ordered[$i], $versions[$i]) -ForegroundColor DarkGray
}
Assert-True ($versions[1] -eq '26.10.2702') "v2026.102702 maps to 26.10.2702 (got $($versions[1]))"
for ($i = 1; $i -lt $versions.Count; $i++) {
    Assert-True ((Compare-MsiVersion $versions[$i] $versions[$i - 1]) -gt 0) `
        "$($ordered[$i]) ($($versions[$i])) is newer than $($ordered[$i - 1]) ($($versions[$i - 1]))"
}

# 2. Above the legacy form every shipped MSI carries.
$legacy = '1.0.1231.99'
foreach ($v in $versions) {
    Assert-True ((Compare-MsiVersion $v $legacy) -gt 0) "$v is newer than the legacy $legacy"
}

# 3. Each field within the MSI limits, at the extremes the build number allows.
$limits = @(255, 255, 65535)
foreach ($buildNumber in ($ordered + @('2026.010101', '2026.123199', '2255.123199'))) {
    $fields = @((ConvertTo-MsiProductVersion -BuildNumber $buildNumber).Split('.') | ForEach-Object { [int]$_ })
    $inRange = $fields.Count -eq 3
    for ($i = 0; $i -lt $fields.Count; $i++) {
        if ($fields[$i] -lt 0 -or $fields[$i] -gt $limits[$i]) { $inRange = $false }
    }
    Assert-True $inRange "$buildNumber gives three fields within 255.255.65535 ($($fields -join '.'))"
}

# 4. Refusals. The sequence must stay within 1..99, or it would spill into the day
# (DD*100+seq): day 27 seq 100 would read as day 28 seq 0.
Assert-True ($script:MsiDayFactor -eq 100) "the day factor leaves room for a two-digit sequence ($script:MsiDayFactor)"
$maxSeq = ConvertTo-MsiProductVersion -BuildNumber '2026.102799'
Assert-True ($maxSeq -eq '26.10.2799') "sequence 99 is the last one that fits ($maxSeq)"
foreach ($bad in @('2026.1027', '1.0.1027.2', '2026.132701', '2026.100001', '2256.010101', '2026.102700', '2026.1027100')) {
    $threw = $false
    try { $null = ConvertTo-MsiProductVersion -BuildNumber $bad } catch { $threw = $true }
    Assert-True $threw "'$bad' is refused"
}

# 5. Wired into Build.ps1: WiX receives the derived value, not the assembly version.
$buildScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'Build.ps1'
$buildLines = @(Get-Content -LiteralPath $buildScript)
$wixVersionLines = @($buildLines | Where-Object { $_ -match '"AppVersion=\$' })
Assert-True ($wixVersionLines.Count -eq 1) "Build.ps1 passes AppVersion to WiX once ($($wixVersionLines.Count) site(s))"
Assert-True (@($wixVersionLines | Where-Object { $_ -match 'AppVersion=\$msiVersion' }).Count -eq 1) `
    'the WiX AppVersion is the derived MSI version'
Assert-True (@($buildLines | Where-Object { $_ -match '\$msiVersion\s*=\s*ConvertTo-MsiProductVersion' }).Count -eq 1) `
    'Build.ps1 derives the MSI version through ConvertTo-MsiProductVersion'

if ($failures -gt 0) {
    Write-Host "FAILED: $failures expectation(s)" -ForegroundColor Red
    exit 1
}

Write-Host 'PASSED: the MSI ProductVersion carries the year and the sequence, in order and within limits.' -ForegroundColor Green
exit 0
