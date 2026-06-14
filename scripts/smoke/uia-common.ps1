<#
.SYNOPSIS
    Shared UIAutomation helpers for Heimdall smoke scripts.

.NOTES
    Copyright 2026 Julien Bombled
    Licensed under the Apache License, Version 2.0
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

if (-not ('Heimdall.Smoke.NativeMouse' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;

namespace Heimdall.Smoke
{
    public static class NativeMouse
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        public const uint LeftDown = 0x0002;
        public const uint LeftUp = 0x0004;
    }
}
"@
}

$script:RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Get-HeimdallRepoRoot {
    return $script:RepoRoot
}

function Get-HeimdallExePath {
    $matches = Get-ChildItem -Path (Join-Path $script:RepoRoot 'src\Heimdall.App\bin\Debug') -Recurse -Filter 'Heimdall.exe' |
        Sort-Object FullName -Descending

    if (-not $matches) {
        throw 'Heimdall.exe not found under src\Heimdall.App\bin\Debug. Run a Debug build first.'
    }

    return $matches[0].FullName
}

function Get-HeimdallSettingsPath {
    $matches = Get-ChildItem -Path (Join-Path $script:RepoRoot 'src\Heimdall.App\bin\Debug') -Recurse -Filter 'settings.json' |
        Where-Object { $_.FullName -match '\\config\\settings\.json$' } |
        Sort-Object FullName -Descending

    if (-not $matches) {
        throw 'settings.json not found under src\Heimdall.App\bin\Debug\*\config. Launch the app once after a Debug build.'
    }

    return $matches[0].FullName
}

function Backup-HeimdallSettings {
    $settingsPath = Get-HeimdallSettingsPath
    $backupPath = '{0}.codex-smoke-{1}.bak' -f $settingsPath, ([guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $settingsPath -Destination $backupPath -Force
    return @{
        SettingsPath = $settingsPath
        BackupPath   = $backupPath
    }
}

function Restore-HeimdallSettings {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Backup
    )

    if (Test-Path -LiteralPath $Backup.BackupPath) {
        Copy-Item -LiteralPath $Backup.BackupPath -Destination $Backup.SettingsPath -Force
        Remove-Item -LiteralPath $Backup.BackupPath -Force
    }
}

function Read-HeimdallSettingsJson {
    $settingsPath = Get-HeimdallSettingsPath
    return Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
}

function Write-HeimdallSettingsJson {
    param(
        [Parameter(Mandatory)]
        [psobject]$Settings
    )

    $settingsPath = Get-HeimdallSettingsPath
    $json = $Settings | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $settingsPath -Value $json -Encoding UTF8
}

function Stop-HeimdallProcesses {
    Get-Process -Name 'Heimdall' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

function Start-HeimdallApp {
    $exePath = Get-HeimdallExePath
    return Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
}

function Stop-HeimdallApp {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process
    )

    if (-not $Process.HasExited) {
        $Process.Kill($true)
        $Process.WaitForExit()
    }
}

function Wait-UiaUntil {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 20,
        [int]$PollMilliseconds = 200,
        [string]$Description = 'UIAutomation condition'
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($null -ne $value) {
            return $value
        }

        Start-Sleep -Milliseconds $PollMilliseconds
    } while ((Get-Date) -lt $deadline)

    throw "Timed out waiting for $Description."
}

function Get-UiaControlTypeCondition {
    param([System.Windows.Automation.ControlType]$ControlType)
    return New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        $ControlType)
}

function Get-UiaAutomationIdCondition {
    param([Parameter(Mandatory)][string]$AutomationId)
    return New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
}

function Find-UiaFirst {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [System.Windows.Automation.TreeScope]$Scope,
        [Parameter(Mandatory)]
        [System.Windows.Automation.Condition]$Condition
    )

    return $Root.FindFirst($Scope, $Condition)
}

function Find-UiaAll {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [System.Windows.Automation.TreeScope]$Scope,
        [Parameter(Mandatory)]
        [System.Windows.Automation.Condition]$Condition
    )

    return $Root.FindAll($Scope, $Condition)
}

function Get-UiaDescendantsByControlType {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [System.Windows.Automation.ControlType]$ControlType
    )

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $result = New-Object System.Collections.Generic.List[System.Windows.Automation.AutomationElement]
    $stack = New-Object System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]
    $stack.Push($Root)

    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        try {
            $child = $walker.GetFirstChild($current)
        } catch {
            continue
        }

        while ($null -ne $child) {
            try {
                if ($child.Current.ControlType -eq $ControlType) {
                    [void]$result.Add($child)
                }
            } catch {
            }

            $stack.Push($child)

            try {
                $child = $walker.GetNextSibling($child)
            } catch {
                $child = $null
            }
        }
    }

    return $result
}

function Wait-HeimdallMainWindow {
    param(
        [Parameter(Mandatory)]
        [int]$ProcessId,
        [int]$TimeoutSeconds = 30
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "Heimdall main window for PID $ProcessId" -Condition {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $windows = $root.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (Get-UiaControlTypeCondition ([System.Windows.Automation.ControlType]::Window)))

        for ($i = 0; $i -lt $windows.Count; $i++) {
            $window = $windows.Item($i)
            $windowProcessId = $window.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ProcessIdProperty)
            if ($windowProcessId -ne $ProcessId) {
                continue
            }

            $name = [string]$window.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
            if ([string]::IsNullOrWhiteSpace($name)) {
                continue
            }

            $settingsTab = Try-GetUiaElementById -Root $window -AutomationId 'TabSettings'
            if ($null -ne $settingsTab) {
                return $window
            }
        }

        return $null
    }
}

function Try-GetUiaElementById {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $stack = New-Object System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]
    $stack.Push($Root)

    while ($stack.Count -gt 0) {
        $current = $stack.Pop()
        try {
            if ([string]$current.Current.AutomationId -eq $AutomationId) {
                return $current
            }
        } catch {
        }

        try {
            $child = $walker.GetFirstChild($current)
        } catch {
            $child = $null
        }

        while ($null -ne $child) {
            $stack.Push($child)
            try {
                $child = $walker.GetNextSibling($child)
            } catch {
                $child = $null
            }
        }
    }

    return $null
}

function Wait-UiaElementById {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root,
        [Parameter(Mandatory)]
        [string]$AutomationId,
        [int]$TimeoutSeconds = 20
    )

    return Wait-UiaUntil -TimeoutSeconds $TimeoutSeconds -Description "element '$AutomationId'" -Condition {
        Try-GetUiaElementById -Root $Root -AutomationId $AutomationId
    }
}

function Invoke-UiaClick {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $invokePattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        $invokePattern.Invoke()
        return
    }

    $selectionItemPattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionItemPattern)) {
        $selectionItemPattern.Select()
        return
    }

    $togglePattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$togglePattern)) {
        $togglePattern.Toggle()
        return
    }

    $point = New-Object System.Windows.Point
    if ($Element.TryGetClickablePoint([ref]$point)) {
        [Heimdall.Smoke.NativeMouse]::SetCursorPos([int]$point.X, [int]$point.Y) | Out-Null
        Start-Sleep -Milliseconds 50
        [Heimdall.Smoke.NativeMouse]::mouse_event([Heimdall.Smoke.NativeMouse]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 25
        [Heimdall.Smoke.NativeMouse]::mouse_event([Heimdall.Smoke.NativeMouse]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
        return
    }

    try {
        $bounds = $Element.Current.BoundingRectangle
        if ($bounds.Width -gt 0 -and $bounds.Height -gt 0) {
            $x = [int]($bounds.Left + ($bounds.Width / 2))
            $y = [int]($bounds.Top + ($bounds.Height / 2))
            [Heimdall.Smoke.NativeMouse]::SetCursorPos($x, $y) | Out-Null
            Start-Sleep -Milliseconds 50
            [Heimdall.Smoke.NativeMouse]::mouse_event([Heimdall.Smoke.NativeMouse]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 25
            [Heimdall.Smoke.NativeMouse]::mouse_event([Heimdall.Smoke.NativeMouse]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
            return
        }
    } catch {
    }

    throw "Element '$($Element.Current.AutomationId)' is not invokable and has no clickable point."
}

function Get-UiaValue {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element
    )

    $valuePattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        return $valuePattern.Current.Value
    }

    return [string]$Element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
}

function Set-UiaToggleState {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Element,
        [Parameter(Mandatory)]
        [bool]$Checked
    )

    $togglePattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$togglePattern)) {
        throw "Element '$($Element.Current.AutomationId)' does not support TogglePattern."
    }

    $desired = if ($Checked) {
        [System.Windows.Automation.ToggleState]::On
    } else {
        [System.Windows.Automation.ToggleState]::Off
    }

    if ($togglePattern.Current.ToggleState -ne $desired) {
        $togglePattern.Toggle()
    }
}

function Expand-UiaCombo {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Combo
    )

    $pattern = $null
    if (-not $Combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        throw "Combo '$($Combo.Current.AutomationId)' does not support ExpandCollapsePattern."
    }

    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $pattern.Expand()
    }
}

function Collapse-UiaCombo {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Combo
    )

    $pattern = $null
    if ($Combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) {
        if ($pattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $pattern.Collapse()
        }
    }
}

function Get-UiaVisibleListItems {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    return @(Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::ListItem))
}

function Get-UiaComboItemNames {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Combo,
        [System.Windows.Automation.AutomationElement]$SearchRoot = ([System.Windows.Automation.AutomationElement]::RootElement)
    )

    Expand-UiaCombo -Combo $Combo
    Start-Sleep -Milliseconds 250
    $names = @(Get-UiaVisibleListItems -Root $SearchRoot | ForEach-Object {
        [string]$_.Current.Name
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    Collapse-UiaCombo -Combo $Combo
    return $names
}

function Select-UiaComboItemByName {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Combo,
        [Parameter(Mandatory)]
        [string]$ItemName,
        [System.Windows.Automation.AutomationElement]$SearchRoot = ([System.Windows.Automation.AutomationElement]::RootElement)
    )

    try {
        Expand-UiaCombo -Combo $Combo
        $item = Wait-UiaUntil -TimeoutSeconds 4 -Description "combo item '$ItemName'" -Condition {
            $items = Get-UiaVisibleListItems -Root $SearchRoot
            foreach ($candidate in $items) {
                if ([string]$candidate.Current.Name -eq $ItemName) {
                    return $candidate
                }
            }
            return $null
        }

        Invoke-UiaClick -Element $item
        Start-Sleep -Milliseconds 300
        Collapse-UiaCombo -Combo $Combo
        return
    } catch {
        Collapse-UiaCombo -Combo $Combo
    }

    $names = @(Get-UiaComboItemNames -Combo $Combo -SearchRoot $SearchRoot)
    $targetIndex = [Array]::IndexOf($names, $ItemName)
    if ($targetIndex -lt 0) {
        throw "Combo item '$ItemName' was not found."
    }

    Invoke-UiaClick -Element $Combo
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{F4}')
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{HOME}')
    Start-Sleep -Milliseconds 100
    for ($i = 0; $i -lt $targetIndex; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
        Start-Sleep -Milliseconds 75
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 300
}

function Select-UiaComboByOffset {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Combo,
        [int]$Offset = 1
    )

    Invoke-UiaClick -Element $Combo
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{F4}')
    Start-Sleep -Milliseconds 150
    for ($i = 0; $i -lt $Offset; $i++) {
        [System.Windows.Forms.SendKeys]::SendWait('{DOWN}')
        Start-Sleep -Milliseconds 75
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 300
}

function Get-UiaTextChildren {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    $result = @()
    $items = @(Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::Text))
    foreach ($item in $items) {
        $name = [string]$item.Current.Name
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $result += $name
        }
    }

    return $result
}

function Open-HeimdallSettings {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow
    )

    $tab = Wait-UiaElementById -Root $MainWindow -AutomationId 'TabSettings'
    Invoke-UiaClick -Element $tab
    Start-Sleep -Milliseconds 400
    return Wait-UiaElementById -Root $MainWindow -AutomationId 'Mw_SettingsSubTabControl'
}

function Select-HeimdallSettingsTab {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow,
        [Parameter(Mandatory)]
        [string]$AutomationId
    )

    $tab = Wait-UiaElementById -Root $MainWindow -AutomationId $AutomationId
    Invoke-UiaClick -Element $tab
    Start-Sleep -Milliseconds 300
    return $tab
}

function Get-HeimdallLocaleCombo {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$MainWindow
    )

    $combos = @(Get-UiaDescendantsByControlType -Root $MainWindow -ControlType ([System.Windows.Automation.ControlType]::ComboBox))
    foreach ($combo in $combos) {
        $names = Get-UiaComboItemNames -Combo $combo -SearchRoot ([System.Windows.Automation.AutomationElement]::RootElement)
        if ($names -contains 'en' -and $names -contains 'fr') {
            return $combo
        }
    }

    throw 'Could not find the locale combo box.'
}

function Get-HeimdallFirstListWithItems {
    param(
        [Parameter(Mandatory)]
        [System.Windows.Automation.AutomationElement]$Root
    )

    $lists = @(Get-UiaDescendantsByControlType -Root $Root -ControlType ([System.Windows.Automation.ControlType]::List))
    foreach ($list in $lists) {
        $items = Find-UiaAll -Root $list -Scope ([System.Windows.Automation.TreeScope]::Children) -Condition (Get-UiaControlTypeCondition ([System.Windows.Automation.ControlType]::ListItem))
        if ($items.Count -gt 0) {
            return $list
        }
    }

    throw 'Could not find a populated list.'
}
