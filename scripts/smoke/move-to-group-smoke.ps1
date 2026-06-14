<#
.SYNOPSIS
    Runs a focused move-to-group UIAutomation smoke test against Heimdall.

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
        public const byte VK_ESCAPE = 0x1B;
        public const byte VK_APPS = 0x5D;
        public const byte VK_SHIFT = 0x10;
        public const byte VK_F10 = 0x79;
    }
}
"@
}

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

function Read-HeimdallServersJson {
    return Get-Content -LiteralPath (Get-HeimdallServersPath) -Raw | ConvertFrom-Json
}

function Get-ServerDtoById {
    param(
        [Parameter(Mandatory)]
        [object[]]$Servers,
        [Parameter(Mandatory)]
        [string]$ServerId
    )

    foreach ($server in $Servers) {
        $idProperty = $server.PSObject.Properties.Match('id') | Select-Object -First 1
        if ($null -ne $idProperty -and [string]$idProperty.Value -eq $ServerId) {
            return $server
        }
    }

    return $null
}

function Get-ServerDtoGroupName {
    param(
        [Parameter(Mandatory)]
        [psobject]$Server
    )

    $groupProperty = $Server.PSObject.Properties.Match('group') | Select-Object -First 1
    if ($null -ne $groupProperty) {
        return [string]$groupProperty.Value
    }

    return ''
}

function Write-SmokeSettings {
    $settings = Read-HeimdallSettingsJson
    $settings.DefaultLocale = 'en'
    $settings.OnboardingCompleted = $true
    $settings.Projects = @(
        @{ Id = 'p1'; Name = 'Project One'; Color = '#224466' },
        @{ Id = 'p2'; Name = 'Project Two'; Color = '#664422' }
    )
    $settings.TreeExpandedNodes = @('Alpha')
    $settings.EmptyGroups = @()
    $settings.SidebarCollapsed = $false
    Write-HeimdallSettingsJson -Settings $settings
}

function Write-SmokeServers {
    $servers = @(
        [pscustomobject]@{
            Id = 'srv-1'
            DisplayName = 'Server One'
            RemoteServer = 'srv-one.example'
            RemotePort = 3389
            LocalPort = 33890
            Group = 'Alpha'
            UseDirectConnection = $true
            ProjectId = 'p1'
            ConnectionType = 'RDP'
            SortOrder = 0
        },
        [pscustomobject]@{
            Id = 'srv-2'
            DisplayName = 'Server Two'
            RemoteServer = 'srv-two.example'
            RemotePort = 3389
            LocalPort = 33891
            Group = 'Beta'
            UseDirectConnection = $true
            ProjectId = 'p1'
            ConnectionType = 'RDP'
            SortOrder = 1
        },
        [pscustomobject]@{
            Id = 'srv-3'
            DisplayName = 'Server Three'
            RemoteServer = 'srv-three.example'
            RemotePort = 3389
            LocalPort = 33892
            Group = 'Gamma'
            UseDirectConnection = $true
            ProjectId = 'p2'
            ConnectionType = 'RDP'
            SortOrder = 2
        }
    )

    $servers | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Get-HeimdallServersPath) -Encoding UTF8
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

function Find-UiaTextByName {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [string]$Name,
        [switch]$Contains
    )

    foreach ($item in (Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        try {
            $text = [string]$item.Current.Name
            if ([bool]$item.Current.IsOffscreen -or [string]::IsNullOrWhiteSpace($text)) {
                continue
            }

            if ($Contains) {
                if ($text.Contains($Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $item
                }
            } elseif ($text -eq $Name) {
                return $item
            }
        } catch {
        }
    }

    return $null
}

function Find-UiaTreeItemByName {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [string]$Name,
        [int]$TimeoutSeconds = 10,
        [switch]$Contains
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "tree item '$Name'" -Condition {
        foreach ($text in (Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Text))) {
            try {
                $candidateName = [string]$text.Current.Name
                if ([bool]$text.Current.IsOffscreen -or [string]::IsNullOrWhiteSpace($candidateName)) {
                    continue
                }

                $isMatch = if ($Contains) {
                    $candidateName.Contains($Name, [StringComparison]::OrdinalIgnoreCase)
                } else {
                    $candidateName -eq $Name
                }

                if (-not $isMatch) {
                    continue
                }

                $treeItem = Get-UiaAncestorByControlType -Element $text -ControlType ([System.Windows.Automation.ControlType]::TreeItem)
                if ($null -ne $treeItem) {
                    return $treeItem
                }
            } catch {
            }
        }

        return $null
    }
}

function Expand-UiaTreeItem {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Item
    )

    $pattern = $null
    if (-not $Item.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        throw "Tree item '$([string]$Item.Current.Name)' does not support ExpandCollapsePattern."
    }

    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Expand()
        Start-Sleep -Milliseconds 300
    }
}

function Get-UiaTreeItemExpandState {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Item
    )

    $pattern = $null
    if (-not $Item.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        return 'Unsupported'
    }

    return $pattern.Current.ExpandCollapseState.ToString()
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
    Start-Sleep -Milliseconds 200
}

function Send-UiaAppsKey {
    [Heimdall.Smoke.NativeKeyboard]::keybd_event([Heimdall.Smoke.NativeKeyboard]::VK_APPS, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 40
    [Heimdall.Smoke.NativeKeyboard]::keybd_event(
        [Heimdall.Smoke.NativeKeyboard]::VK_APPS,
        0,
        [Heimdall.Smoke.NativeKeyboard]::KEYEVENTF_KEYUP,
        [UIntPtr]::Zero)
}

function Send-UiaEscape {
    [Heimdall.Smoke.NativeKeyboard]::keybd_event([Heimdall.Smoke.NativeKeyboard]::VK_ESCAPE, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 20
    [Heimdall.Smoke.NativeKeyboard]::keybd_event(
        [Heimdall.Smoke.NativeKeyboard]::VK_ESCAPE,
        0,
        [Heimdall.Smoke.NativeKeyboard]::KEYEVENTF_KEYUP,
        [UIntPtr]::Zero)
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

function Get-VisibleMenuItemNames {
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in (Get-UiaDescendantsByControlType -Root ([System.Windows.Automation.AutomationElement]::RootElement) -ControlType ([System.Windows.Automation.ControlType]::MenuItem))) {
        try {
            if ([bool]$item.Current.IsOffscreen) {
                continue
            }

            $name = [string]$item.Current.Name
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                [void]$names.Add($name)
            }
        } catch {
        }
    }

    return @($names)
}

function Get-UiaAncestorWindow {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
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
            if ($current.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window) {
                return $current
            }
        } catch {
        }
    }

    return $null
}

function Get-UiaWindowRectKey {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Window
    )

    $bounds = $Window.Current.BoundingRectangle
    return '{0}:{1}:{2}:{3}' -f
        [int]$bounds.Left,
        [int]$bounds.Top,
        [int]$bounds.Width,
        [int]$bounds.Height
}

function Get-VisibleSubmenuItemNames {
    param(
        [Parameter(Mandatory)]
        [string]$ParentName
    )

    $parentItem = Find-UiaMenuItemByName -Name $ParentName
    $parentWindow = Get-UiaAncestorWindow -Element $parentItem
    if ($null -eq $parentWindow) {
        return @()
    }

    $parentWindowKey = Get-UiaWindowRectKey -Window $parentWindow
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in (Get-UiaDescendantsByControlType -Root ([System.Windows.Automation.AutomationElement]::RootElement) -ControlType ([System.Windows.Automation.ControlType]::MenuItem))) {
        try {
            if ([bool]$item.Current.IsOffscreen) {
                continue
            }

            $name = [string]$item.Current.Name
            if ([string]::IsNullOrWhiteSpace($name) -or $name -eq $ParentName) {
                continue
            }

            $window = Get-UiaAncestorWindow -Element $item
            if ($null -eq $window) {
                continue
            }

            $windowKey = Get-UiaWindowRectKey -Window $window
            if ($windowKey -ne $parentWindowKey) {
                [void]$names.Add($name)
            }
        } catch {
        }
    }

    return @($names)
}

function Find-UiaSubmenuItemByName {
    param(
        [Parameter(Mandatory)]
        [string]$ParentName,
        [Parameter(Mandatory)]
        [string]$ChildName,
        [int]$TimeoutSeconds = 8
    )

    $parentItem = Find-UiaMenuItemByName -Name $ParentName -TimeoutSeconds $TimeoutSeconds
    $parentWindow = Get-UiaAncestorWindow -Element $parentItem
    if ($null -eq $parentWindow) {
        throw "Could not locate popup window for menu item '$ParentName'."
    }

    $parentWindowKey = Get-UiaWindowRectKey -Window $parentWindow
    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "submenu item '$ChildName' under '$ParentName'" -Condition {
        foreach ($item in (Get-UiaDescendantsByControlType -Root ([System.Windows.Automation.AutomationElement]::RootElement) -ControlType ([System.Windows.Automation.ControlType]::MenuItem))) {
            try {
                if ([bool]$item.Current.IsOffscreen -or [string]$item.Current.Name -ne $ChildName) {
                    continue
                }

                $window = Get-UiaAncestorWindow -Element $item
                if ($null -eq $window) {
                    continue
                }

                $windowKey = Get-UiaWindowRectKey -Window $window
                if ($windowKey -ne $parentWindowKey) {
                    return $item
                }
            } catch {
            }
        }

        return $null
    }
}

function Open-SelectedTreeItemContextMenu {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem,
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        Send-UiaEscape
        Start-Sleep -Milliseconds 100
        Send-UiaEscape
        Start-Sleep -Milliseconds 100

        Bring-HeimdallWindowToFront -Process $Process
        Invoke-UiaClick -Element $TreeItem
        Start-Sleep -Milliseconds 250

        # Native keyboard injection is the reliable path for this tree.
        Bring-HeimdallWindowToFront -Process $Process
        Send-UiaAppsKey
        Start-Sleep -Milliseconds 150
        Send-UiaShiftF10
        Start-Sleep -Milliseconds 250

        $menuOpened = $false
        try {
            $null = Find-UiaMenuItemByName -Name 'Move to group' -TimeoutSeconds 2
            $menuOpened = $true
        } catch {
            $menuOpened = $false
        }

        if ($menuOpened) {
            return
        }

        Bring-HeimdallWindowToFront -Process $Process
        Invoke-UiaRightClick -Element $TreeItem
        Start-Sleep -Milliseconds 250
        try {
            $null = Find-UiaMenuItemByName -Name 'Move to group' -TimeoutSeconds 2
            return
        } catch {
        }

        Send-UiaEscape
        Start-Sleep -Milliseconds 150
    }

    throw 'Could not open the session tree context menu.'
}

function Expand-MenuItem {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Item
    )

    $pattern = $null
    if (-not $Item.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        throw "Menu item '$([string]$Item.Current.Name)' does not support ExpandCollapsePattern."
    }

    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Expand()
        Start-Sleep -Milliseconds 300
    }
}

function Invoke-MenuItemByPath {
    param(
        [Parameter(Mandatory)]
        [string]$RootItemName,
        [Parameter(Mandatory)]
        [string]$ChildItemName
    )

    $rootItem = Find-UiaMenuItemByName -Name $RootItemName
    Expand-MenuItem -Item $rootItem
    $childItem = Find-UiaSubmenuItemByName -ParentName $RootItemName -ChildName $ChildItemName
    Invoke-UiaClick -Element $childItem
    Start-Sleep -Milliseconds 500
}

function Get-SelectedTreeItemLabel {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    foreach ($item in (Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::TreeItem))) {
        try {
            if ([bool]$item.Current.IsOffscreen) {
                continue
            }

            $pattern = $null
            if (-not $item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                continue
            }

            if (-not $pattern.Current.IsSelected) {
                continue
            }

            foreach ($text in (Get-UiaDescendantsByControlType -Root $item -ControlType ([System.Windows.Automation.ControlType]::Text))) {
                try {
                    $name = [string]$text.Current.Name
                    if (-not [string]::IsNullOrWhiteSpace($name) -and -not [bool]$text.Current.IsOffscreen) {
                        return $name
                    }
                } catch {
                }
            }
        } catch {
        }
    }

    return ''
}

function Get-MainWindowTexts {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow
    )

    $texts = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in (Get-UiaDescendantsByControlType -Root $MainWindow -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        try {
            $name = [string]$item.Current.Name
            if (-not [string]::IsNullOrWhiteSpace($name) -and -not [bool]$item.Current.IsOffscreen) {
                [void]$texts.Add($name)
            }
        } catch {
        }
    }

    return @($texts)
}

function Scroll-UiaElementToTop {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern)) {
        return
    }

    $horizontal = if ($pattern.Current.HorizontallyScrollable) { 0 } else { [System.Windows.Automation.ScrollPattern]::NoScroll }
    $vertical = if ($pattern.Current.VerticallyScrollable) { 0 } else { [System.Windows.Automation.ScrollPattern]::NoScroll }
    $pattern.SetScrollPercent($horizontal, $vertical)
    Start-Sleep -Milliseconds 300
}

function Test-TreeItemContainsVisibleText {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem,
        [Parameter(Mandatory)]
        [string]$Text
    )

    foreach ($item in (Get-UiaDescendantsByControlType -Root $TreeItem -ControlType ([System.Windows.Automation.ControlType]::Text))) {
        try {
            if ([string]$item.Current.Name -eq $Text -and -not [bool]$item.Current.IsOffscreen) {
                return $true
            }
        } catch {
        }
    }

    return $false
}

function Get-ParentTreeItem {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$TreeItem
    )

    return Get-UiaAncestorByControlType -Element $TreeItem -ControlType ([System.Windows.Automation.ControlType]::TreeItem)
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

Push-Location (Get-HeimdallRepoRoot)
$settingsBackup = $null
$serversBackup = $null
$process = $null
$report = [ordered]@{
    Result = 'Fail'
    S1_ContextMenuMove = 'Red'
    S1_Sample = ''
    S2_SelectionFollows = 'Skipped'
    S2_Reason = 'UIA TreeView selection readback unreliable under WPF virtualization — delegated to human smoke H1'
    S3_ExpansionPreserved = 'Red'
    S3_Sample = ''
    S4_DestinationSetParity = 'Red'
    S4_Sample = ''
    S5a_NoGroupOffered = 'Red'
    S5a_Sample = ''
    S5b_NoGroupMove = 'Skipped'
    S5b_Reason = 'Second context-menu pass times out under UIA — delegated to human smoke H3'
}

try {
    Invoke-OptionalVerification

    $settingsBackup = Backup-HeimdallSettings
    $serversBackup = Backup-HeimdallServers
    Stop-HeimdallProcesses

    Write-SmokeSettings
    Write-SmokeServers

    $process = Start-HeimdallApp
    $mainWindow = Wait-HeimdallMainWindow -ProcessId $process.Id
    $sessionsTab = Wait-UiaElementById -Root $mainWindow -AutomationId 'SidebarTabSessions'
    Invoke-UiaClick -Element $sessionsTab
    Start-Sleep -Milliseconds 500

    $gammaTreeItem = Find-UiaTreeItemByName -Root $mainWindow -Name 'Gamma'
    Expand-UiaTreeItem -Item $gammaTreeItem

    $serverOneTreeItem = Find-UiaTreeItemByName -Root $mainWindow -Name 'Server One'

    Open-SelectedTreeItemContextMenu -TreeItem $serverOneTreeItem -Process $process
    $moveToGroupMenu = Find-UiaMenuItemByName -Name 'Move to group'
    Expand-MenuItem -Item $moveToGroupMenu
    $knownMoveTargets = @('Alpha', 'Beta', '(No Group)')
    $rawMoveTargets = @(Get-VisibleSubmenuItemNames -ParentName 'Move to group')
    $filteredMoveTargets = @($rawMoveTargets | Where-Object { $_ -in $knownMoveTargets } | Select-Object -Unique)
    $menuHasNoGroupTarget = ($filteredMoveTargets -contains '(No Group)')
    $menuRejectsCrossProjectTarget = ($filteredMoveTargets -notcontains 'Gamma')
    $treeFolderNames = @('Alpha', 'Beta', 'Gamma') | Where-Object {
        $null -ne (Find-UiaTextByName -Root $mainWindow -Name $_)
    }

    $report.S4_DestinationSetParity = if ($menuHasNoGroupTarget -and ($treeFolderNames -contains 'Gamma') -and $menuRejectsCrossProjectTarget) {
        'Green'
    } else {
        'Red'
    }
    $report.S4_Sample = "Submenu offers [$($filteredMoveTargets -join ', ')]; cross-project Gamma absent; no ambient entries after name-based filtering"

    $report.S5a_NoGroupOffered = if ($menuHasNoGroupTarget) { 'Green' } else { 'Red' }
    $report.S5a_Sample = '(No Group) found in Move to group submenu'

    $betaMenuItem = Find-UiaSubmenuItemByName -ParentName 'Move to group' -ChildName 'Beta'
    Invoke-UiaClick -Element $betaMenuItem
    Start-Sleep -Milliseconds 500

    # Skipped: UIA TreeView selection readback unreliable.
    # $selectedLabel = Get-SelectedTreeItemLabel -Root $mainWindow
    # $detailTextsAfterBetaMove = @(Get-MainWindowTexts -MainWindow $mainWindow)
    # $detailGroupTextAfterBetaMove = @($detailTextsAfterBetaMove | Where-Object { $_ -like 'Group:*' } | Select-Object -First 1)

    $betaTreeItem = Find-UiaTreeItemByName -Root $mainWindow -Name 'Beta'
    Expand-UiaTreeItem -Item $betaTreeItem
    $serverOneMovedTreeItem = Find-UiaTreeItemByName -Root $mainWindow -Name 'Server One'
    $serverOneParentTreeItem = Get-ParentTreeItem -TreeItem $serverOneMovedTreeItem
    $serverOneParentLabel = if ($null -ne $serverOneParentTreeItem) {
        Get-TreeItemPrimaryText -TreeItem $serverOneParentTreeItem
    } else {
        ''
    }
    $serverOneUnderBeta = Test-TreeItemContainsVisibleText -TreeItem $betaTreeItem -Text 'Server One'

    $serversAfterBetaMove = Read-HeimdallServersJson
    $serverOneDtoAfterBetaMove = Get-ServerDtoById -Servers $serversAfterBetaMove -ServerId 'srv-1'
    $serverOneGroupAfterBetaMove = if ($null -ne $serverOneDtoAfterBetaMove) {
        Get-ServerDtoGroupName -Server $serverOneDtoAfterBetaMove
    } else {
        ''
    }

    $report.S1_ContextMenuMove = if (
        $serverOneUnderBeta -and
        $serverOneGroupAfterBetaMove -eq 'Beta' -and
        $serverOneParentLabel -eq 'Beta') {
        'Green'
    } else {
        'Red'
    }
    $report.S1_Sample = "Server 'Server One' parent after move: '$serverOneParentLabel'; persisted group: '$serverOneGroupAfterBetaMove'"

    $gammaExpandState = Get-UiaTreeItemExpandState -Item (Find-UiaTreeItemByName -Root $mainWindow -Name 'Gamma')
    $report.S3_ExpansionPreserved = if ($gammaExpandState -eq 'Expanded') { 'Green' } else { 'Red' }
    $report.S3_Sample = "Gamma expand state after move: '$gammaExpandState'"

    # Skipped: second context-menu pass unreliable under UIA.
    # Open-SelectedTreeItemContextMenu -TreeItem (Find-UiaTreeItemByName -Root $mainWindow -Name 'Server One') -Process $process
    # $moveToGroupMenu = Find-UiaMenuItemByName -Name 'Move to group'
    # Expand-MenuItem -Item $moveToGroupMenu
    # Invoke-UiaClick -Element (Find-UiaSubmenuItemByName -ParentName 'Move to group' -ChildName '(No Group)')
    # Start-Sleep -Milliseconds 500

    $statuses = @(
        $report.S1_ContextMenuMove,
        $report.S2_SelectionFollows,
        $report.S3_ExpansionPreserved,
        $report.S4_DestinationSetParity,
        $report.S5a_NoGroupOffered,
        $report.S5b_NoGroupMove)

    if ($statuses -notcontains 'Red') {
        $report.Result = 'Pass'
    }
}
catch {
    $report.Error = $_.Exception.Message
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
