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
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Heimdall.App.Behaviors;
using Heimdall.App.Services.HostsFile;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Hosts file editor providing a friendly DataGrid-based UI for viewing and editing
/// the Windows hosts file with enable/disable toggling, inline comments, and IP validation.
/// </summary>
/// <remarks>
/// The file model, its validation and the save with backup live in
/// <see cref="HostsFileDocument" /> and <see cref="HostsFileStore" />; this view only asks
/// the user and shows the outcome.
/// </remarks>
public partial class HostsFileEditorView : UserControl, IToolView
{
    private readonly HostsFileStore _store = new(
        HostsFileStore.SystemHostsPath,
        ApplicationDataPathResolver.GetHostsBackupsDirectory(ApplicationDataPathResolver.Resolve()));
    private LocalizationManager? _localizer;
    private Action<string, string, ToolContext?>? _openToolAction;
    private HostsFileDocument? _document;

    public HostsFileEditorView()
    {
        InitializeComponent();
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
        TxtUnsaved.Text = L("ToolHostsEditorUnsaved");
        BtnReload.Content = L("ToolHostsEditorBtnReload");
        BtnSave.Content = L("ToolHostsEditorBtnSave");
        BtnAddEntry.Content = L("ToolHostsEditorBtnAdd");
        BtnDeleteSelected.Content = L("ToolHostsEditorBtnDelete");
        ColEnabled.Header = L("ToolHostsEditorColEnabled");
        ColIpAddress.Header = L("ToolHostsEditorColIp");
        ColHostname.Header = L("ToolHostsEditorColHostname");
        ColComment.Header = L("ToolHostsEditorColComment");
        ColProblem.Header = L("ToolHostsEditorColProblem");

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
        TxtFilePath.Text = _store.FilePath;

        HostsFileDocument? document = null;
        if (!_store.Exists)
        {
            // Saving creates the file; there is nothing on disk to lose.
            document = HostsFileDocument.CreateEmpty();
            TxtFilePath.Text = L("ToolHostsEditorFileNotFound");
            TxtLastModified.Text = string.Empty;
        }
        else
        {
            try
            {
                document = _store.Load();
                ShowLastModified();
            }
            catch (UnauthorizedAccessException)
            {
                TxtLastModified.Text = L("ToolHostsEditorReadDenied");
            }
            catch (IOException ex)
            {
                TxtLastModified.Text = ex.Message;
            }
        }

        // A file that could not be read leaves no document: saving an empty one over it
        // would erase every entry it holds.
        SetDocument(document);
    }

    private void SetDocument(HostsFileDocument? document)
    {
        if (_document is not null)
        {
            _document.IsDirtyChanged -= OnDocumentDirtyChanged;
        }

        _document = document;
        if (_document is not null)
        {
            _document.IsDirtyChanged += OnDocumentDirtyChanged;
        }

        HostsGrid.ItemsSource = _document?.Entries;
        BtnSave.IsEnabled = _document is not null;
        BtnAddEntry.IsEnabled = _document is not null;
        BtnDeleteSelected.IsEnabled = _document is not null;
        UpdateDirtyIndicator();
        UpdateEmptyState();
    }

    private void ShowLastModified()
    {
        var lastWrite = File.GetLastWriteTime(_store.FilePath);
        TxtLastModified.Text = string.Format(
            L("ToolHostsEditorLastModified"),
            lastWrite.ToString("g"));
    }

    private void OnDocumentDirtyChanged(object? sender, EventArgs e) => UpdateDirtyIndicator();

    private void UpdateDirtyIndicator()
    {
        TxtUnsaved.Visibility = _document?.IsDirty == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        if (ResolveUnsavedChanges() is not null)
        {
            return;
        }

        LoadHostsFile();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _ = PerformSave();
    }

    /// <summary>
    /// Validates, asks for confirmation, then saves with a backup.
    /// </summary>
    /// <returns>Null when saved, otherwise why it was not.</returns>
    private ToolCloseRefusal? PerformSave()
    {
        ClearErrorState();
        if (_document is null)
        {
            return ToolCloseRefusal.SaveFailed;
        }

        // Commit a cell still in edit mode, so the value on screen is the one checked and written.
        HostsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        var problems = _document.FindProblems();
        if (problems.Count > 0)
        {
            ShowProblems(problems);
            return ToolCloseRefusal.SaveFailed;
        }

        var message = _store.Exists
            ? string.Format(L("ToolHostsEditorConfirmSave"), _store.FilePath, _store.BackupDirectory, HostsFileStore.MaxTimestampedBackups)
            : string.Format(L("ToolHostsEditorConfirmCreate"), _store.FilePath);
        bool confirmed = MessageDialog.ShowConfirm(
            Window.GetWindow(this),
            L("ToolHostsEditorTitle"),
            message,
            "warning",
            L("BtnSave"),
            L("BtnCancel"));
        if (!confirmed)
        {
            return ToolCloseRefusal.UserDeclined;
        }

        var result = _store.Save(_document);
        switch (result.Status)
        {
            case HostsFileSaveStatus.Saved:
                TxtFilePath.Text = _store.FilePath;
                ShowLastModified();
                return null;
            case HostsFileSaveStatus.InvalidEntries:
                ShowProblems(result.Problems);
                break;
            case HostsFileSaveStatus.BackupFailed:
                ShowErrorState(string.Format(L("ToolHostsEditorBackupFailed"), _store.BackupDirectory, result.ErrorMessage));
                break;
            case HostsFileSaveStatus.ExternallyModified:
                ShowErrorState(L("ToolHostsEditorExternallyModified"));
                OfferReloadAfterExternalChange();
                break;
            case HostsFileSaveStatus.AccessDenied:
                ShowErrorState(L("ToolHostsEditorSaveAdminRequired"));
                break;
            default:
                ShowErrorState(string.Format(L("ToolHostsEditorSaveFailed"), result.ErrorMessage));
                break;
        }

        return ToolCloseRefusal.SaveFailed;
    }

    /// <summary>
    /// Another program rewrote the file since it was loaded. Its change is kept; the user
    /// chooses whether to reload, which discards the edits made here.
    /// </summary>
    private void OfferReloadAfterExternalChange()
    {
        bool reload = MessageDialog.ShowConfirm(
            Window.GetWindow(this),
            L("ToolHostsEditorTitle"),
            L("ToolHostsEditorExternallyModifiedReloadPrompt"),
            "warning",
            L("ToolHostsEditorBtnReload"),
            L("BtnCancel"));
        if (reload)
        {
            LoadHostsFile();
        }
    }

    private void ShowProblems(IReadOnlyList<HostsFileRowProblem> problems)
    {
        var text = new StringBuilder(L("ToolHostsEditorInvalidRows"));
        foreach (var problem in problems)
        {
            var key = HostEntry.ProblemLocaleKey(problem.Problem) ?? string.Empty;
            text.AppendLine();
            text.Append(string.Format(L("ToolHostsEditorInvalidRowItem"), problem.LineNumber, L(key)));
        }

        ShowErrorState(text.ToString());

        var first = problems[0].Entry;
        HostsGrid.SelectedItem = first;
        HostsGrid.ScrollIntoView(first);
    }

    /// <summary>
    /// Asks what to do with unsaved edits before they are replaced or closed.
    /// </summary>
    /// <returns>Null when the caller may proceed, otherwise why it may not.</returns>
    private ToolCloseRefusal? ResolveUnsavedChanges()
    {
        if (_document is null || !_document.IsDirty)
        {
            return null;
        }

        var decision = MessageDialog.ShowThreeWay(
            Window.GetWindow(this),
            L("ToolHostsEditorTitle"),
            L("ToolHostsEditorUnsavedMessage"),
            "warning",
            L("BtnSave"),
            L("BtnDiscard"),
            L("BtnCancel"));

        return decision switch
        {
            true => PerformSave(),
            false => null,
            _ => ToolCloseRefusal.UserDeclined,
        };
    }

    /// <inheritdoc />
    public ToolCloseRefusal CloseRefusal { get; private set; } = ToolCloseRefusal.Busy;

    /// <inheritdoc />
    public bool CanClose()
    {
        var refusal = ResolveUnsavedChanges();

        // Set on every refusing call, never left over from an earlier one.
        CloseRefusal = refusal ?? ToolCloseRefusal.Busy;
        return refusal is null;
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
        LiveRegionBehavior.Announce(ErrorText);
    }

    private void UpdateEmptyState()
    {
        int count = _document?.Entries.Count ?? 0;
        EmptyStatePanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HostsGrid.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddEntryClick(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var entry = _document.AddEntry();
        UpdateEmptyState();
        HostsGrid.SelectedItem = entry;
        HostsGrid.ScrollIntoView(entry, ColIpAddress);
        HostsGrid.UpdateLayout();
        HostsGrid.CurrentCell = new DataGridCellInfo(entry, ColIpAddress);
        HostsGrid.Focus();
        HostsGrid.BeginEdit();
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        _document.RemoveEntries(HostsGrid.SelectedItems.Cast<HostEntry>());
        UpdateEmptyState();
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
        if (_document is not null)
        {
            _document.IsDirtyChanged -= OnDocumentDirtyChanged;
        }

        GC.SuppressFinalize(this);
    }
}
