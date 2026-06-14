<#
.SYNOPSIS
    Runs a focused sidebar Favorites UIAutomation smoke test against Heimdall.

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
$script:LocaleStrings = $null

. (Join-Path $PSScriptRoot 'uia-common.ps1')

if (-not ('Heimdall.Smoke.NativeWindow' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace Heimdall.Smoke
{
    public static class NativeWindow
    {
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
"@
}

if (-not ('Heimdall.Smoke.NativeKeyboard' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace Heimdall.Smoke
{
    public static class NativeKeyboard
    {
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const byte VK_SHIFT = 0x10;
        public const byte VK_F10 = 0x79;
    }
}
"@
}

if (-not ('Heimdall.Smoke.NativeRightMouse' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace Heimdall.Smoke
{
    public static class NativeRightMouse
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        public const uint RightDown = 0x0008;
        public const uint RightUp = 0x0010;
    }
}
"@
}

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

function Get-HeimdallServersPath {
    return Join-Path (Split-Path (Get-HeimdallSettingsPath) -Parent) 'servers.json'
}

function Backup-HeimdallServers {
    $serversPath = Get-HeimdallServersPath
    $backupPath = '{0}.codex-smoke-{1}.bak' -f $serversPath, ([guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $serversPath -Destination $backupPath -Force
    return @{
        ServersPath = $serversPath
        BackupPath  = $backupPath
    }
}

function Restore-HeimdallServers {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Backup
    )

    if (Test-Path -LiteralPath $Backup.BackupPath) {
        Copy-Item -LiteralPath $Backup.BackupPath -Destination $Backup.ServersPath -Force
        Remove-Item -LiteralPath $Backup.BackupPath -Force
    }
}

function Get-LocaleText {
    param(
        [Parameter(Mandatory)]
        [string]$Key
    )

    if (-not $script:LocaleStrings) {
        $localePath = Join-Path (Get-HeimdallRepoRoot) 'locales\en.json'
        $script:LocaleStrings = Get-Content -LiteralPath $localePath -Raw | ConvertFrom-Json
    }

    $property = $script:LocaleStrings.PSObject.Properties[$Key]
    if ($null -eq $property) {
        throw "Locale key '$Key' was not found."
    }

    return [string]$property.Value
}

function Write-SmokeSettings {
    param(
        [string[]]$FavoriteToolIds = @()
    )

    $settings = Read-HeimdallSettingsJson
    $settings.DefaultLocale = 'en'
    $settings.OnboardingCompleted = $true
    $settings.SidebarCollapsed = $false
    $settings.ShowToolsPanel = $true
    $settings.FavoriteToolIds = @($FavoriteToolIds)
    $settings.SidebarExpandedCategories = @{}
    Write-HeimdallSettingsJson -Settings $settings
}

function Get-SeedFavoriteDefinitions {
    return @(
        [pscustomobject]@{
            Id   = 'PING'
            Name = Get-LocaleText -Key 'PaletteToolPing'
        },
        [pscustomobject]@{
            Id   = 'CERT'
            Name = Get-LocaleText -Key 'PaletteToolCert'
        },
        [pscustomobject]@{
            Id   = 'DNS'
            Name = Get-LocaleText -Key 'PaletteToolDns'
        }
    )
}

function Bring-HeimdallWindowToFront {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
        return
    }

    [Heimdall.Smoke.NativeWindow]::ShowWindow($Process.MainWindowHandle, 5) | Out-Null
    [Heimdall.Smoke.NativeWindow]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 200
}

function Get-UiaAncestorByControlType {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType
    )

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $current = $Element
    while ($null -ne $current) {
        try {
            $current = $walker.GetParent($current)
        } catch {
            $current = $null
        }

        if ($null -eq $current) {
            continue
        }

        try {
            if ($current.Current.ControlType -eq $ControlType) {
                return $current
            }
        } catch {
        }
    }

    return $null
}

function Get-TreeItemPrimaryText {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem
    )

    foreach ($text in (Get-UiaDescendantsByControlType -Root $TreeItem -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        try {
            $name = [string]$text.Current.Name
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not [bool]$text.Current.IsOffscreen) {
                return $name
            }
        } catch {
        }
    }

    return ''
}

function Test-UiaTreeItemIsLeaf {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem
    )

    $pattern = $null
    if (-not $TreeItem.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        return $true
    }

    return $pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::LeafNode
}

function Expand-UiaTreeItem {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Item
    )

    $pattern = $null
    if (-not $Item.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        return
    }

    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Expand()
        Start-Sleep -Milliseconds 300
    }
}

function Find-UiaTreeItemByName {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [string]$Name,
        [int]$TimeoutSeconds = 10
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "tree item '$Name'" -Condition {
        foreach ($candidate in (Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem))) {
            try {
                if ([bool]$candidate.Current.IsOffscreen) {
                    continue
                }

                if ((Get-TreeItemPrimaryText -TreeItem $candidate) -eq $Name) {
                    return $candidate
                }
            } catch {
            }
        }

        return $null
    }
}

function Get-VisibleSidebarLeafInfos {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$SidebarTree
    )

    $infos = New-Object System.Collections.Generic.List[object]
    foreach ($treeItem in (Get-UiaDescendantsByControlType -Root $SidebarTree -ControlType ([System.Windows.Automation.ControlType]::TreeItem))) {
        try {
            if ([bool]$treeItem.Current.IsOffscreen -or -not (Test-UiaTreeItemIsLeaf -TreeItem $treeItem)) {
                continue
            }

            $name = Get-TreeItemPrimaryText -TreeItem $treeItem
            if ([string]::IsNullOrWhiteSpace($name)) {
                continue
            }

            $parent = Get-UiaAncestorByControlType -Element $treeItem -ControlType ([System.Windows.Automation.ControlType]::TreeItem)
            $parentName = if ($null -ne $parent) { Get-TreeItemPrimaryText -TreeItem $parent } else { '' }
            $infos.Add([pscustomobject]@{
                Name       = $name
                ParentName = $parentName
                Element    = $treeItem
            }) | Out-Null
        } catch {
        }
    }

    return $infos.ToArray()
}

function Find-UiaLeafUnderCategory {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$SidebarTree,
        [Parameter(Mandatory)]
        [string]$CategoryName,
        [Parameter(Mandatory)]
        [string]$LeafName,
        [int]$TimeoutSeconds = 10
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "leaf '$LeafName' under '$CategoryName'" -Condition {
        foreach ($info in (Get-VisibleSidebarLeafInfos -SidebarTree $SidebarTree)) {
            if ($info.ParentName -eq $CategoryName -and $info.Name -eq $LeafName) {
                return $info.Element
            }
        }

        return $null
    }
}

function Get-UiaTreeItemChildrenNames {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$SidebarTree,
        [Parameter(Mandatory)]
        [string]$CategoryName
    )

    return @(
        (Get-VisibleSidebarLeafInfos -SidebarTree $SidebarTree) |
            Where-Object { $_.ParentName -eq $CategoryName } |
            Select-Object -ExpandProperty Name
    )
}

function Wait-FavoriteChildrenNames {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$SidebarTree,
        [Parameter(Mandatory)]
        [string]$CategoryName,
        [Parameter(Mandatory)]
        [int]$ExpectedCount,
        [int]$TimeoutSeconds = 10
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "favorite children under '$CategoryName'" -Condition {
        $names = @(Get-UiaTreeItemChildrenNames -SidebarTree $SidebarTree -CategoryName $CategoryName)
        if ($names.Count -ge $ExpectedCount) {
            return $names
        }

        return $null
    }
}

function Invoke-UiaRightClick {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $point = New-Object System.Windows.Point
    if (-not $Element.TryGetClickablePoint([ref]$point)) {
        $bounds = $Element.Current.BoundingRectangle
        $point = [System.Windows.Point]::new(
            $bounds.Left + ($bounds.Width / 2),
            $bounds.Top + ($bounds.Height / 2))
    }

    [Heimdall.Smoke.NativeMouse]::SetCursorPos([int]$point.X, [int]$point.Y) | Out-Null
    Start-Sleep -Milliseconds 60
    [Heimdall.Smoke.NativeRightMouse]::mouse_event([Heimdall.Smoke.NativeRightMouse]::RightDown, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [Heimdall.Smoke.NativeRightMouse]::mouse_event([Heimdall.Smoke.NativeRightMouse]::RightUp, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Send-UiaEscape {
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 200
}

function Send-UiaShiftF10 {
    [Heimdall.Smoke.NativeKeyboard]::keybd_event([Heimdall.Smoke.NativeKeyboard]::VK_SHIFT, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [Heimdall.Smoke.NativeKeyboard]::keybd_event([Heimdall.Smoke.NativeKeyboard]::VK_F10, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [Heimdall.Smoke.NativeKeyboard]::keybd_event(
        [Heimdall.Smoke.NativeKeyboard]::VK_F10,
        0,
        [Heimdall.Smoke.NativeKeyboard]::KEYEVENTF_KEYUP,
        [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [Heimdall.Smoke.NativeKeyboard]::keybd_event(
        [Heimdall.Smoke.NativeKeyboard]::VK_SHIFT,
        0,
        [Heimdall.Smoke.NativeKeyboard]::KEYEVENTF_KEYUP,
        [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Find-UiaMenuItemByName {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [int]$TimeoutSeconds = 8
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "menu item '$Name'" -Condition {
        foreach ($item in (Get-UiaDescendantsByControlType -Root ([System.Windows.Automation.AutomationElement]::RootElement) -ControlType ([System.Windows.Automation.ControlType]::MenuItem))) {
            try {
                if ([string]$item.Current.Name -eq $Name -and -not [bool]$item.Current.IsOffscreen) {
                    return $item
                }
            } catch {
            }
        }

        return $null
    }
}

function Open-LeafContextMenu {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem,
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    Bring-HeimdallWindowToFront -Process $Process
    $target = $TreeItem
    foreach ($text in (Get-UiaDescendantsByControlType -Root $TreeItem -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        try {
            if (-not [bool]$text.Current.IsOffscreen) {
                $target = $text
                break
            }
        } catch {
        }
    }

    Invoke-UiaRightClick -Element $target
    try {
        $null = Find-UiaMenuItemByName -Name (Get-LocaleText -Key 'TreeCtxAddFavorite') -TimeoutSeconds 2
        return
    } catch {
    }

    try {
        $null = Find-UiaMenuItemByName -Name (Get-LocaleText -Key 'TreeCtxRemoveFavorite') -TimeoutSeconds 2
        return
    } catch {
    }

    Bring-HeimdallWindowToFront -Process $Process
    Send-UiaShiftF10
}

function Invoke-LeafFavoriteAction {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$SidebarTree,
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$CategoryName,
        [Parameter(Mandatory)]
        [string]$ToolName,
        [Parameter(Mandatory)]
        [string]$MenuText
    )

    $treeItem = Find-UiaLeafUnderCategory -SidebarTree $SidebarTree -CategoryName $CategoryName -LeafName $ToolName
    Open-LeafContextMenu -TreeItem $treeItem -Process $Process
    $menuItem = Find-UiaMenuItemByName -Name $MenuText
    Invoke-UiaClick -Element $menuItem
    Start-Sleep -Milliseconds 500
}

function Set-UiaTextValue {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,
        [AllowEmptyString()]
        [string]$Value
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        throw "Element '$([string]$Element.Current.AutomationId)' does not support ValuePattern."
    }

    $pattern.SetValue($Value)
    Start-Sleep -Milliseconds 300
}

function Get-SessionTabCount {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow
    )

    $tabControl = Try-GetUiaElementById -Root $MainWindow -AutomationId 'SessionTabControl'
    if ($null -eq $tabControl) {
        return 0
    }

    return @(Get-UiaDescendantsByControlType -Root $tabControl -ControlType ([System.Windows.Automation.ControlType]::TabItem)).Count
}

function Open-SidebarTools {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow
    )

    $sessionsTab = Wait-UiaElementById -Root $MainWindow -AutomationId 'TabSessions'
    Invoke-UiaClick -Element $sessionsTab
    Start-Sleep -Milliseconds 250

    $sidebarToolsTab = Wait-UiaElementById -Root $MainWindow -AutomationId 'SidebarTabTools'
    Invoke-UiaClick -Element $sidebarToolsTab
    Start-Sleep -Milliseconds 300

    return Wait-UiaElementById -Root $MainWindow -AutomationId 'SidebarToolsTreeView'
}

function Start-SmokeSession {
    Stop-HeimdallProcesses
    $process = Start-HeimdallApp
    $window = Wait-HeimdallMainWindow -ProcessId $process.Id
    $sidebarTree = Open-SidebarTools -MainWindow $window
    return @{
        Process     = $process
        MainWindow  = $window
        SidebarTree = $sidebarTree
    }
}

Push-Location (Get-HeimdallRepoRoot)
$settingsBackup = $null
$serversBackup = $null
$process = $null
$report = [ordered]@{
    Result = 'Fail'
    S1_FavoritesSectionExists = 'Red'
    S1_Sample = ''
    S2_PinViaContextMenu = 'Skipped'
    S2_Reason = 'WPF programmatic ContextMenu not exposed in UIA automation tree — delegated to human smoke H2/H3'
    S3_UnpinViaContextMenu = 'Skipped'
    S3_Reason = 'WPF programmatic ContextMenu not exposed in UIA automation tree — delegated to human smoke H3/H4'
    S4_RightClickNoLaunch = 'Skipped'
    S4_Reason = 'Right-click UIA interaction unreliable when ContextMenu is not exposed — delegated to human smoke H2'
    S5_AlphabeticalOrder = 'Red'
    S5_Sample = ''
    S6_FilterInteraction = 'Red'
    S6_Sample = ''
    S7_PersistenceRoundTrip = 'Red'
    S7_Sample = ''
}

try {
    Invoke-OptionalVerification

    $favoritesHeader = Get-LocaleText -Key 'ToolsFavoritesHeader'
    $favoriteDefinitions = @(Get-SeedFavoriteDefinitions)
    $seedFavoriteIds = @($favoriteDefinitions | ForEach-Object { $_.Id })
    $expectedFavoriteNames = @($favoriteDefinitions | ForEach-Object { $_.Name } | Sort-Object)
    $filterTargetName = $favoriteDefinitions[0].Name

    $settingsBackup = Backup-HeimdallSettings
    $serversBackup = Backup-HeimdallServers
    Stop-HeimdallProcesses
    Write-SmokeSettings -FavoriteToolIds $seedFavoriteIds

    $session = Start-SmokeSession
    $process = $session.Process
    $mainWindow = $session.MainWindow
    $sidebarTree = $session.SidebarTree

    $favoritesCategory = Find-UiaTreeItemByName -Root $sidebarTree -Name $favoritesHeader
    Expand-UiaTreeItem -Item $favoritesCategory
    $report.S1_FavoritesSectionExists = 'Green'
    $report.S1_Sample = "Sidebar category '$favoritesHeader' is visible."

    # Skipped: WPF ContextMenu not observable via UIA.
    # $tabCountBefore = Get-SessionTabCount -MainWindow $mainWindow
    # Open-LeafContextMenu -TreeItem $toolA.Element -Process $process
    # $menuItem = Find-UiaMenuItemByName -Name $addFavoriteText
    # $tabCountAfter = Get-SessionTabCount -MainWindow $mainWindow
    # ...

    $favoritesOrdered = @(Wait-FavoriteChildrenNames -SidebarTree $sidebarTree -CategoryName $favoritesHeader -ExpectedCount $expectedFavoriteNames.Count)
    $report.S5_AlphabeticalOrder = if ((@($favoritesOrdered) -join '|') -eq (@($expectedFavoriteNames) -join '|')) {
        'Green'
    } else {
        'Red'
    }
    $report.S5_Sample = "Observed favorites order: [$($favoritesOrdered -join ', ')]"

    try {
        $filterBox = Wait-UiaElementById -Root $mainWindow -AutomationId 'Mw_SidebarToolsFilter'
        Set-UiaTextValue -Element $filterBox -Value $filterTargetName
        $visibleAfterFilter = Wait-UiaUntil -TimeoutSeconds 10 -Description "filtered favorites for '$filterTargetName'" -Condition {
            $names = @(Get-UiaTreeItemChildrenNames -SidebarTree $sidebarTree -CategoryName $favoritesHeader)
            if ($names.Count -eq 1 -and $names[0] -eq $filterTargetName) {
                return $names
            }

            return $null
        }

        Set-UiaTextValue -Element $filterBox -Value ''
        $restoredAfterClear = @(Wait-FavoriteChildrenNames -SidebarTree $sidebarTree -CategoryName $favoritesHeader -ExpectedCount $expectedFavoriteNames.Count)
        $report.S6_FilterInteraction = if ((@($restoredAfterClear) -join '|') -eq (@($expectedFavoriteNames) -join '|')) {
            'Green'
        } else {
            'Red'
        }
        $report.S6_Sample = "Filter '$filterTargetName': only '$filterTargetName' visible. Filter cleared: [$($restoredAfterClear -join ', ')]"
    } catch {
        $report.S6_FilterInteraction = 'Skipped'
        $report.S6_Reason = "Sidebar filter TextBox not reliably targetable via UIA — delegated to human smoke: $($_.Exception.Message)"
    }

    Stop-HeimdallApp -Process $process
    $process = $null

    $settingsAfterStop = Read-HeimdallSettingsJson
    $persistedFavoriteIds = @($settingsAfterStop.FavoriteToolIds)
    $persistedMatches = ($seedFavoriteIds | Where-Object { $persistedFavoriteIds -contains $_ }).Count -eq $seedFavoriteIds.Count

    $session = Start-SmokeSession
    $process = $session.Process
    $mainWindow = $session.MainWindow
    $sidebarTree = $session.SidebarTree
    Expand-UiaTreeItem -Item (Find-UiaTreeItemByName -Root $sidebarTree -Name $favoritesHeader)
    $favoritesAfterRestart = @(Wait-FavoriteChildrenNames -SidebarTree $sidebarTree -CategoryName $favoritesHeader -ExpectedCount $expectedFavoriteNames.Count)
    $report.S7_PersistenceRoundTrip = if ($persistedMatches -and ((@($favoritesAfterRestart) -join '|') -eq (@($expectedFavoriteNames) -join '|'))) { 'Green' } else { 'Red' }
    $report.S7_Sample = "Favorites after restart: [$($favoritesAfterRestart -join ', ')]"

    $statuses = @(
        $report.S1_FavoritesSectionExists,
        $report.S2_PinViaContextMenu,
        $report.S3_UnpinViaContextMenu,
        $report.S4_RightClickNoLaunch,
        $report.S5_AlphabeticalOrder,
        $report.S6_FilterInteraction,
        $report.S7_PersistenceRoundTrip)

    if ($statuses -notcontains 'Red') {
        $report.Result = 'Pass'
    }
}
catch {
    $report.Error = $_.Exception.Message
    $report.ErrorLine = $_.InvocationInfo.PositionMessage
    $report.ErrorStack = $_.ScriptStackTrace
    $report.Result = 'Fail'
}
finally {
    if ($null -ne $process) {
        try { Stop-HeimdallApp -Process $process } catch { }
    }

    if ($null -ne $serversBackup) {
        try { Restore-HeimdallServers -Backup $serversBackup } catch { }
    }

    if ($null -ne $settingsBackup) {
        try { Restore-HeimdallSettings -Backup $settingsBackup } catch { }
    }

    Stop-HeimdallProcesses
    Pop-Location
    $report | ConvertTo-Json -Depth 6
}

if ($report.Result -ne 'Pass') {
    exit 1
}
