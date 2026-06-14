<#
.SYNOPSIS
    Runs a focused Settings-page UIAutomation smoke test against Heimdall.

.PARAMETER RunBuild
    Runs dotnet build before the smoke test.

.PARAMETER RunTests
    Runs dotnet test before the smoke test.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
[CmdletBinding()]
param(
    [switch]$RunBuild,
    [switch]$RunTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'uia-common.ps1')

function Invoke-OptionalVerification {
    if ($RunBuild) {
        & dotnet build Heimdall.slnx --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    }

    if ($RunTests) {
        & dotnet test Heimdall.slnx --no-build
        if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
    }
}

function Start-SmokeWindow {
    $process = Start-HeimdallApp
    $window = Wait-HeimdallMainWindow -ProcessId $process.Id
    return @{
        Process = $process
        Window  = $window
    }
}

function Open-SmokeSettings {
    param([System.Windows.Automation.AutomationElement]$MainWindow)
    $null = Open-HeimdallSettings -MainWindow $MainWindow
}

function Save-HeimdallSettingsFromUi {
    param([System.Windows.Automation.AutomationElement]$MainWindow)
    $button = Wait-UiaElementById -Root $MainWindow -AutomationId 'Mw_SettingsSaveBtn'
    Invoke-UiaClick -Element $button
    Start-Sleep -Milliseconds 1200
}

function Set-LocaleInSettingsFile {
    param([ValidateSet('en','fr')][string]$Locale)
    $settings = Read-HeimdallSettingsJson
    $settings.DefaultLocale = $Locale
    Write-HeimdallSettingsJson -Settings $settings
}

Push-Location (Get-HeimdallRepoRoot)
$settingsBackup = $null
$process = $null
$report = [ordered]@{ Result = 'FAIL' }

try {
    Invoke-OptionalVerification

    $settingsBackup = Backup-HeimdallSettings
    Stop-HeimdallProcesses

    Set-LocaleInSettingsFile -Locale 'en'
    $session = Start-SmokeWindow
    $process = $session.Process
    $mainWindow = $session.Window
    Open-SmokeSettings -MainWindow $mainWindow
    $report.SettingsOpened = $true

    Select-HeimdallSettingsTab -MainWindow $mainWindow -AutomationId 'Mw_SettingsTabSecurity' | Out-Null
    $credEnabled = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCredProviderEnabled'
    Set-UiaToggleState -Element $credEnabled -Checked $true
    $report.CredentialProviderEnabled = $true

    $presetCombo = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCredProvPreset'
    $commandBox = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCredProvCmd'
    $commandBefore = Get-UiaValue -Element $commandBox
    Select-UiaComboByOffset -Combo $presetCombo -Offset 1
    $commandAfter = Get-UiaValue -Element $commandBox
    $report.CredentialProviderCommandBefore = $commandBefore
    $report.CredentialProviderCommandAfter = $commandAfter
    $report.CredentialProviderPresetSelectionChangedCommand = ($commandBefore -ne $commandAfter)

    Save-HeimdallSettingsFromUi -MainWindow $mainWindow
    $savedSettings = Read-HeimdallSettingsJson
    $report.CredentialProviderCommandSavedToDisk = $savedSettings.CredentialProviderCommand

    Stop-HeimdallApp -Process $process
    $process = $null

    $session = Start-SmokeWindow
    $process = $session.Process
    $mainWindow = $session.Window
    Open-SmokeSettings -MainWindow $mainWindow
    Select-HeimdallSettingsTab -MainWindow $mainWindow -AutomationId 'Mw_SettingsTabSecurity' | Out-Null
    $commandBox = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCredProvCmd'
    $commandAfterRestart = Get-UiaValue -Element $commandBox
    $report.CredentialProviderCommandAfterRestart = $commandAfterRestart
    $report.CredentialProviderCommandPersistedAfterRestart = ($commandAfterRestart -eq $commandAfter)

    Select-HeimdallSettingsTab -MainWindow $mainWindow -AutomationId 'Mw_SettingsTabAdvanced' | Out-Null
    $tokenStatus = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCmdLibSyncTokenStatus'
    $report.CommandLibraryTokenStatusText = Get-UiaValue -Element $tokenStatus
    $report.CommandLibraryTokenClearButtonPresent = ($null -ne (Try-GetUiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsCmdLibSyncTokenClear'))

    $providerStatus = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsExtProvStatus'
    $toolList = Get-HeimdallFirstListWithItems -Root $mainWindow
    $toolItems = Find-UiaAll -Root $toolList -Scope ([System.Windows.Automation.TreeScope]::Children) -Condition (Get-UiaControlTypeCondition ([System.Windows.Automation.ControlType]::ListItem))
    $report.ExternalToolCount = $toolItems.Count
    $report.ExternalToolStatusEn = Get-UiaValue -Element $providerStatus

    Invoke-UiaClick -Element $toolItems.Item(0)
    Start-Sleep -Milliseconds 300
    $placeholderList = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_ExtToolPlaceholderList'
    $preview = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_ExtToolPreview'
    $placeholderTextsEn = @(Get-UiaTextChildren -Root $placeholderList | Select-Object -Unique)
    $report.ExternalToolPlaceholderListPopulated = ($placeholderTextsEn.Count -gt 0)
    $report.ExternalToolPlaceholderSampleEn = if ($placeholderTextsEn.Count -gt 0) { $placeholderTextsEn[0] } else { '' }
    $previewFirst = Get-UiaValue -Element $preview
    $report.ExternalToolPreviewFirst = $previewFirst

    if ($toolItems.Count -gt 1) {
        Invoke-UiaClick -Element $toolItems.Item(1)
        Start-Sleep -Milliseconds 300
    }
    $previewSecond = Get-UiaValue -Element $preview
    $report.ExternalToolPreviewSecond = $previewSecond
    $report.ExternalToolPreviewChangedOnSelection = ($previewFirst -ne $previewSecond)

    Stop-HeimdallApp -Process $process
    $process = $null

    Set-LocaleInSettingsFile -Locale 'fr'
    $session = Start-SmokeWindow
    $process = $session.Process
    $mainWindow = $session.Window
    Open-SmokeSettings -MainWindow $mainWindow
    $saveButton = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsSaveBtn'
    $report.SaveButtonNameFr = [string]$saveButton.Current.Name

    Select-HeimdallSettingsTab -MainWindow $mainWindow -AutomationId 'Mw_SettingsTabAdvanced' | Out-Null
    $providerStatus = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SettingsExtProvStatus'
    $toolList = Get-HeimdallFirstListWithItems -Root $mainWindow
    $toolItems = Find-UiaAll -Root $toolList -Scope ([System.Windows.Automation.TreeScope]::Children) -Condition (Get-UiaControlTypeCondition ([System.Windows.Automation.ControlType]::ListItem))
    Invoke-UiaClick -Element $toolItems.Item(0)
    Start-Sleep -Milliseconds 300
    $placeholderList = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_ExtToolPlaceholderList'
    $report.ExternalToolStatusFr = Get-UiaValue -Element $providerStatus
    $placeholderTextsFr = @(Get-UiaTextChildren -Root $placeholderList | Select-Object -Unique)
    $report.ExternalToolPlaceholderSampleFr = if ($placeholderTextsFr.Count -gt 0) { $placeholderTextsFr[0] } else { '' }
    $report.CredentialProviderPresetLocalizationNote = 'Static preset catalog; labels are intentionally unchanged by this smoke.'
    $report.ExternalToolRuntimeIntegration = 'Not exercised by this script; runtime session/context-menu path remains a manual smoke.'

    $report.Result = 'OK'
}
catch {
    $report.Error = $_.Exception.Message
    $report.Result = 'FAIL'
}
finally {
    if ($null -ne $process) {
        try { Stop-HeimdallApp -Process $process } catch { }
    }

    if ($null -ne $settingsBackup) {
        try { Restore-HeimdallSettings -Backup $settingsBackup } catch { }
    }

    Stop-HeimdallProcesses
    Pop-Location
    $report | ConvertTo-Json -Depth 6
}

if ($report.Result -ne 'OK') {
    exit 1
}
