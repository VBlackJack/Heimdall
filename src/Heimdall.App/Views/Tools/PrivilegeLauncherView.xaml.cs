/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Microsoft.Win32;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Tool view for launching processes under elevated security contexts
/// (Administrator, SYSTEM, TrustedInstaller).
/// </summary>
public partial class PrivilegeLauncherView : UserControl, IToolView
{
    private LocalizationManager? _localizer;

    /// <summary>Privilege level items bound to the ComboBox.</summary>
    private readonly record struct PrivilegeLevelItem(PrivilegeLevel Level, string DisplayName);
    private readonly List<PrivilegeLevelItem> _levels = [];

    /// <summary>Quick-launch shortcut definition.</summary>
    private readonly record struct QuickLaunchEntry(string Label, string ExePath);

    public PrivilegeLauncherView()
    {
        InitializeComponent();
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        ApplyLocalization();

        // Show warning if not elevated
        if (!PrivilegeLauncher.IsCurrentProcessElevated())
        {
            TxtWarning.Text = L("ToolPrivLaunchWarningNotElevated");
            WarningPanel.Visibility = Visibility.Visible;
        }

        // Pre-fill executable path from context argument
        if (!string.IsNullOrEmpty(context?.Argument))
            TxtExecutablePath.Text = context.Argument;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            TxtExecutablePath.Focus();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolPrivLaunchTitle");
        LblExecutable.Text = L("ToolPrivLaunchLblExecutable");
        BtnBrowse.Content = L("ToolPrivLaunchBtnBrowse");
        LblPrivilegeLevel.Text = L("ToolPrivLaunchLblPrivilegeLevel");
        LblArguments.Text = L("ToolPrivLaunchLblArguments");
        BtnLaunch.Content = L("ToolPrivLaunchBtnLaunch");

        // Accessibility
        System.Windows.Automation.AutomationProperties.SetName(TxtExecutablePath, L("ToolPrivLaunchLblExecutable"));
        System.Windows.Automation.AutomationProperties.SetName(BtnBrowse, L("ToolPrivLaunchBtnBrowse"));
        System.Windows.Automation.AutomationProperties.SetName(CmbPrivilegeLevel, L("ToolPrivLaunchLblPrivilegeLevel"));
        System.Windows.Automation.AutomationProperties.SetName(TxtArguments, L("ToolPrivLaunchLblArguments"));
        System.Windows.Automation.AutomationProperties.SetName(BtnLaunch, L("ToolPrivLaunchBtnLaunch"));
        System.Windows.Automation.AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");

        // Populate privilege levels with localized names
        _levels.Clear();
        _levels.Add(new(PrivilegeLevel.CurrentUserElevated, L("ToolPrivLaunchLevelElevated")));
        _levels.Add(new(PrivilegeLevel.System, L("ToolPrivLaunchLevelSystem")));
        _levels.Add(new(PrivilegeLevel.TrustedInstaller, L("ToolPrivLaunchLevelTrustedInstaller")));

        CmbPrivilegeLevel.ItemsSource = _levels;
        CmbPrivilegeLevel.DisplayMemberPath = "DisplayName";
        CmbPrivilegeLevel.SelectedIndex = 0;

        // Quick launch shortcuts
        LblQuickLaunch.Text = L("ToolPrivLaunchLblQuickLaunch");
        PopulateQuickLaunchButtons();
    }

    private void PopulateQuickLaunchButtons()
    {
        QuickLaunchPanel.Children.Clear();

        var sys32 = Environment.SystemDirectory; // C:\Windows\System32
        var shortcuts = new List<QuickLaunchEntry>
        {
            new("CMD", Path.Combine(sys32, "cmd.exe")),
            new("PowerShell", Path.Combine(sys32, "WindowsPowerShell", "v1.0", "powershell.exe")),
            new("Regedit", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe")),
            new("Task Manager", Path.Combine(sys32, "Taskmgr.exe")),
            new("Explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")),
        };

        // pwsh.exe (PowerShell 7+) - search PATH and common install locations
        var pwshPath = FindInPath("pwsh.exe")
            ?? FindInProgramFiles("PowerShell", "pwsh.exe");
        if (pwshPath is not null)
            shortcuts.Insert(2, new("pwsh", pwshPath));

        var tabIndex = 10;
        foreach (var shortcut in shortcuts)
        {
            if (!File.Exists(shortcut.ExePath)) continue;

            var btn = new Button
            {
                Content = shortcut.Label,
                Tag = shortcut.ExePath,
                Style = (Style)FindResource("SecondaryButtonStyle"),
                Padding = (Thickness)FindResource("PaddingButtonPrimary"),
                Margin = (Thickness)FindResource("MarginButtonGroup"),
                TabIndex = tabIndex++
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, shortcut.Label);
            btn.Click += OnQuickLaunchClick;
            QuickLaunchPanel.Children.Add(btn);
        }
    }

    private void OnQuickLaunchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            TxtExecutablePath.Text = path;
    }

    private static string? FindInPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindInProgramFiles(string subDir, string fileName)
    {
        string?[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        ];

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = Path.Combine(root, subDir);
            if (!Directory.Exists(dir)) continue;

            // Search up to one level deep (e.g. PowerShell/7/pwsh.exe)
            try
            {
                foreach (var match in Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories))
                    return match;
            }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L("ToolPrivLaunchBrowseTitle"),
            Filter = L("ToolPrivLaunchBrowseFilter"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            TxtExecutablePath.Text = dialog.FileName;
    }

    private void OnLaunchClick(object sender, RoutedEventArgs e)
    {
        var exePath = TxtExecutablePath.Text.Trim();
        if (string.IsNullOrEmpty(exePath))
        {
            ShowStatus(isError: true, L("ToolPrivLaunchErrorNoPath"));
            return;
        }

        if (CmbPrivilegeLevel.SelectedItem is not PrivilegeLevelItem selected)
            return;

        var args = TxtArguments.Text.Trim();
        var result = PrivilegeLauncher.Launch(
            exePath,
            string.IsNullOrEmpty(args) ? null : args,
            selected.Level);

        if (result.Success)
        {
            ShowStatus(
                isError: false,
                string.Format(L("ToolPrivLaunchStatusSuccess"), result.ProcessId));
        }
        else
        {
            ShowStatus(isError: true, result.ErrorMessage ?? L("ToolPrivLaunchStatusFailed"));
        }
    }

    private void ShowStatus(bool isError, string message)
    {
        StatusPanel.Visibility = Visibility.Visible;

        var surface = TryFindResource("SurfaceBrush") as Brush ?? Brushes.Transparent;
        var border = TryFindResource(isError ? "ErrorBrush" : "SuccessBrush") as Brush ?? Brushes.Gray;
        var text = TryFindResource(isError ? "ErrorTextBrush" : "SuccessTextBrush") as Brush ?? Brushes.White;

        StatusPanel.Background = surface;
        StatusPanel.BorderBrush = border;
        TxtStatusIcon.Text = isError ? "\uEA39" : "\uE73E"; // ErrorBadge : CheckMark
        TxtStatusIcon.Foreground = text;
        TxtStatus.Text = message;
        TxtStatus.Foreground = text;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpPRIVLAUNCH").Replace("\\n", "\n");
        HelpPanel.Visibility = Visibility.Visible;
    }

    private void OnCloseHelpClick(object sender, RoutedEventArgs e)
    {
        HelpPanel.Visibility = Visibility.Collapsed;
    }

    private string L(string key) => _localizer?[key] ?? key;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
