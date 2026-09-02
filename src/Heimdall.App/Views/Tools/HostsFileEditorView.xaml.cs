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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.App.Localization;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Hosts file editor providing a friendly DataGrid-based UI for viewing and editing
/// the Windows hosts file with enable/disable toggling, inline comments, and IP validation.
/// </summary>
public partial class HostsFileEditorView : UserControl, IToolView
{
    private static readonly string HostsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");

    private static readonly Regex IpAddressPattern = new(
        @"^(?:(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d\d?)$|^(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}$|^::1$|^::$",
        RegexOptions.Compiled);

    private LocalizationManager? _localizer;
    private Action<string, string, ToolContext?>? _openToolAction;
    private readonly ObservableCollection<HostEntry> _entries = [];
    private readonly List<string> _preambleLines = [];

    public HostsFileEditorView()
    {
        InitializeComponent();
        HostsGrid.ItemsSource = _entries;
        HostsGrid.PreviewMouseRightButtonDown += ToolContextMenuHelper.SelectRowOnRightClick;
        HostsGrid.ContextMenuOpening += OnHostsContextMenuOpening;
    }

    public void Initialize(ToolContext? context, LocalizationManager? localizer)
    {
        _localizer = localizer;
        _openToolAction = ToolContextMenuHelper.GetOpenToolAction(context);
        ApplyLocalization();
        ClearErrorState();
        LoadHostsFile();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (HostsGrid.Items.Count > 0 && HostsGrid.SelectedIndex < 0)
            {
                HostsGrid.SelectedIndex = 0;
            }

            HostsGrid.Focus();
        });
    }

    private void ApplyLocalization()
    {
        HeaderTitle.Text = L("ToolHostsEditorTitle");
        BtnReload.Content = L("ToolHostsEditorBtnReload");
        BtnSave.Content = L("ToolHostsEditorBtnSave");
        BtnAddEntry.Content = L("ToolHostsEditorBtnAdd");
        BtnDeleteSelected.Content = L("ToolHostsEditorBtnDelete");
        ColEnabled.Header = L("ToolHostsEditorColEnabled");
        ColIpAddress.Header = L("ToolHostsEditorColIp");
        ColHostname.Header = L("ToolHostsEditorColHostname");
        ColComment.Header = L("ToolHostsEditorColComment");

        AutomationProperties.SetName(BtnReload, L("ToolHostsEditorBtnReload"));
        AutomationProperties.SetName(BtnSave, L("ToolHostsEditorBtnSave"));
        AutomationProperties.SetName(BtnAddEntry, L("ToolHostsEditorBtnAdd"));
        AutomationProperties.SetName(BtnDeleteSelected, L("ToolHostsEditorBtnDelete"));
        AutomationProperties.SetName(HostsGrid, L("ToolHostsEditorTitle"));

        BtnHelp.ToolTip = L("ToolHelpTooltip");
        AutomationProperties.SetName(BtnHelp, L("ToolHelpTooltip"));
        AutomationProperties.SetName(BtnCloseHelp, L("BtnClose"));
        TxtEmptyState.Text = L("ToolHostsEmptyState");
    }

    private void LoadHostsFile()
    {
        ClearErrorState();
        _entries.Clear();
        _preambleLines.Clear();

        if (!File.Exists(HostsFilePath))
        {
            TxtFilePath.Text = L("ToolHostsEditorFileNotFound");
            TxtLastModified.Text = string.Empty;
            UpdateEmptyState();
            return;
        }

        try
        {
            var lines = File.ReadAllLines(HostsFilePath, Encoding.UTF8);
            bool inPreamble = true;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // Empty lines
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (inPreamble)
                    {
                        _preambleLines.Add(rawLine);
                    }
                    continue;
                }

                // Pure comment lines (no IP pattern after #)
                if (line.StartsWith('#'))
                {
                    var afterHash = line.TrimStart('#').Trim();
                    if (TryParseHostLine(afterHash, out var disabledEntry))
                    {
                        // Disabled entry: # <IP> <hostname> [# comment]
                        disabledEntry.Enabled = false;
                        _entries.Add(disabledEntry);
                        inPreamble = false;
                    }
                    else
                    {
                        // Regular comment - keep as preamble if still at top
                        if (inPreamble)
                        {
                            _preambleLines.Add(rawLine);
                        }
                    }
                    continue;
                }

                inPreamble = false;

                if (TryParseHostLine(line, out var entry))
                {
                    entry.Enabled = true;
                    _entries.Add(entry);
                }
            }

            TxtFilePath.Text = HostsFilePath;
            var lastWrite = File.GetLastWriteTime(HostsFilePath);
            TxtLastModified.Text = string.Format(
                L("ToolHostsEditorLastModified"),
                lastWrite.ToString("g"));
            UpdateEmptyState();
        }
        catch (UnauthorizedAccessException)
        {
            TxtFilePath.Text = HostsFilePath;
            TxtLastModified.Text = L("ToolHostsEditorReadDenied");
        }
        catch (IOException ex)
        {
            TxtFilePath.Text = HostsFilePath;
            TxtLastModified.Text = ex.Message;
        }
    }

    private static bool TryParseHostLine(string line, out HostEntry entry)
    {
        entry = new HostEntry();

        // Split on whitespace
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        // First part must look like an IP address
        if (!IPAddress.TryParse(parts[0], out _))
        {
            return false;
        }

        entry.IpAddress = parts[0];
        entry.Hostname = parts[1];

        // Look for inline comment: everything after # is comment
        var commentIndex = line.IndexOf('#');
        if (commentIndex >= 0)
        {
            entry.Comment = line[(commentIndex + 1)..].Trim();
        }

        return true;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        LoadHostsFile();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveHostsFile();
    }

    private void SaveHostsFile()
    {
        ClearErrorState();
        var sb = new StringBuilder();

        // Write preamble (original comment header)
        foreach (var line in _preambleLines)
        {
            sb.AppendLine(line);
        }

        if (_preambleLines.Count > 0)
        {
            sb.AppendLine();
        }

        // Write entries
        foreach (var entry in _entries)
        {
            if (string.IsNullOrWhiteSpace(entry.IpAddress) || string.IsNullOrWhiteSpace(entry.Hostname))
            {
                continue;
            }

            var line = new StringBuilder();

            if (!entry.Enabled)
            {
                line.Append("# ");
            }

            line.Append(entry.IpAddress);
            line.Append('\t');
            line.Append(entry.Hostname);

            if (!string.IsNullOrWhiteSpace(entry.Comment))
            {
                line.Append("\t# ");
                line.Append(entry.Comment);
            }

            sb.AppendLine(line.ToString());
        }

        try
        {
            File.WriteAllText(HostsFilePath, sb.ToString(), Encoding.UTF8);
            var lastWrite = File.GetLastWriteTime(HostsFilePath);
            TxtLastModified.Text = string.Format(
                L("ToolHostsEditorLastModified"),
                lastWrite.ToString("g"));
        }
        catch (UnauthorizedAccessException)
        {
            ShowErrorState(L("ToolHostsEditorSaveAdminRequired"));
        }
        catch (IOException ex)
        {
            ShowErrorState(string.Format(L("ToolHostsEditorSaveFailed"), ex.Message));
        }
    }

    private void ClearErrorState()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ErrorText.BringIntoView();
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HostsGrid.Visibility = _entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        var entry = new HostEntry
        {
            Enabled = true,
            IpAddress = "127.0.0.1",
            Hostname = "example.local",
            Comment = string.Empty
        };
        _entries.Add(entry);
        HostsGrid.ScrollIntoView(entry);
        HostsGrid.SelectedItem = entry;
        UpdateEmptyState();
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        var selected = HostsGrid.SelectedItems.Cast<HostEntry>().ToList();
        foreach (var item in selected)
        {
            _entries.Remove(item);
        }
        UpdateEmptyState();
    }

    private void OnCellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Column == ColIpAddress && e.EditingElement is System.Windows.Controls.TextBox tb)
        {
            var ip = tb.Text.Trim();
            if (!string.IsNullOrWhiteSpace(ip) && !IpAddressPattern.IsMatch(ip))
            {
                tb.BorderBrush = TryFindResource("ErrorBrush") as Brush ?? System.Windows.Media.Brushes.Red;
                tb.ToolTip = L("ToolHostsEditorInvalidIp");
            }
            else
            {
                tb.ClearValue(BorderBrushProperty);
                tb.ToolTip = null;
            }
        }
    }

    private void OnHostsContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (HostsGrid.SelectedItem is not HostEntry entry)
        {
            e.Handled = true;
            return;
        }

        var menu = new System.Windows.Controls.ContextMenu();

        // Toggle enable/disable
        var toggleHeader = entry.Enabled ? L("ToolHostsEditorCtxDisable") : L("ToolHostsEditorCtxEnable");
        var toggle = new System.Windows.Controls.MenuItem { Header = toggleHeader };
        toggle.Click += (_, _) => entry.Enabled = !entry.Enabled;
        menu.Items.Add(toggle);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // Copy IP
        var copyIp = new System.Windows.Controls.MenuItem { Header = L("ToolCtxCopyIp") };
        copyIp.Click += (_, _) => { try { System.Windows.Clipboard.SetText(entry.IpAddress); } catch (System.Runtime.InteropServices.ExternalException) { return; } };
        menu.Items.Add(copyIp);

        // Copy Hostname
        if (!string.IsNullOrWhiteSpace(entry.Hostname))
        {
            var copyHost = new System.Windows.Controls.MenuItem { Header = L("ToolCtxCopyHostname") };
            copyHost.Click += (_, _) => { try { System.Windows.Clipboard.SetText(entry.Hostname); } catch (System.Runtime.InteropServices.ExternalException) { return; } };
            menu.Items.Add(copyHost);
        }

        // Cross-tool navigation
        if (_openToolAction is not null && !string.IsNullOrWhiteSpace(entry.IpAddress))
        {
            menu.Items.Add(new System.Windows.Controls.Separator());

            var ping = new System.Windows.Controls.MenuItem { Header = L("ToolCtxOpenPing") };
            ping.Click += (_, _) => _openToolAction("PING", L("PaletteToolPing"),
                new ToolContext(TargetHost: entry.IpAddress));
            menu.Items.Add(ping);

            var portScan = new System.Windows.Controls.MenuItem { Header = L("ToolCtxOpenPortScan") };
            portScan.Click += (_, _) => _openToolAction("PORTSCAN", L("PaletteToolPortScan"),
                new ToolContext(TargetHost: entry.IpAddress));
            menu.Items.Add(portScan);

            var dns = new System.Windows.Controls.MenuItem { Header = L("ToolCtxOpenDns") };
            dns.Click += (_, _) => _openToolAction("DNS", L("PaletteToolDns"),
                new ToolContext(TargetHost: entry.IpAddress));
            menu.Items.Add(dns);

            var whois = new System.Windows.Controls.MenuItem { Header = L("ToolCtxOpenWhois") };
            whois.Click += (_, _) => _openToolAction("WHOIS", L("PaletteToolWhois"),
                new ToolContext(TargetHost: entry.IpAddress));
            menu.Items.Add(whois);
        }

        HostsGrid.ContextMenu = menu;
    }

    private void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            HelpPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TxtHelpContent.Text = L("ToolHelpHOSTS").Replace("\\n", "\n");
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

/// <summary>
/// Represents a single entry in the hosts file with enable/disable state.
/// </summary>
public sealed class HostEntry : INotifyPropertyChanged
{
    private bool _enabled;
    private string _ipAddress = string.Empty;
    private string _hostname = string.Empty;
    private string _comment = string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(EnabledDisplay));
        }
    }

    /// <summary>
    /// <see cref="Enabled" /> as a word, for the row's accessible name.
    /// </summary>
    /// <remarks>
    /// Binding the boolean directly would put an English literal into every locale:
    /// <see cref="bool" /> does not implement <see cref="IFormattable" />, so
    /// <see cref="string.Format(IFormatProvider, string, object?[])" /> falls through to
    /// <c>Boolean.ToString()</c>, which ignores the format provider and always returns the
    /// invariant "True" or "False". The visible column is a checkbox, so this word exists
    /// only for a screen reader and has to carry its own translation.
    /// </remarks>
    public string EnabledDisplay => LocalizationSource.Instance[
        _enabled ? "A11yStatusDotScheduledEnabled" : "A11yStatusDotScheduledDisabled"];

    public string IpAddress
    {
        get => _ipAddress;
        set { _ipAddress = value; OnPropertyChanged(nameof(IpAddress)); }
    }

    public string Hostname
    {
        get => _hostname;
        set { _hostname = value; OnPropertyChanged(nameof(Hostname)); }
    }

    public string Comment
    {
        get => _comment;
        set { _comment = value; OnPropertyChanged(nameof(Comment)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
