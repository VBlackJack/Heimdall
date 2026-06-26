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
using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.Utilities;
using Heimdall.Sftp;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Heimdall.App.Views;

/// <summary>
/// View half of the embedded SFTP/FTP file browser. Its partner
/// <c>EmbeddedSftpViewModel</c> owns navigation state, the listing,
/// filtering/sorting, file operations, transfer orchestration and status; this
/// class is a thin host reached through bindings and commands.
/// </summary>
/// <remarks>
/// The code-behind is intentionally limited to wiring that cannot move to the
/// ViewModel without coupling it to WPF/Win32 internals or to other views:
/// Win32 file/folder pickers (<c>OpenFileDialog</c> / <c>FolderBrowserDialog</c>,
/// since <c>IDialogService</c> has no file-picker); drag-and-drop;
/// <c>GridView</c> column sizing and sort-header management; the embedded-editor
/// hand-off (<c>EditFileAsync</c> creates an <c>EmbeddedEditorView</c> and swaps
/// panes in the split tree); the bookmarks overflow <c>ContextMenu</c> built in
/// code; and session lifecycle — browser/editor creation, reconnect requests,
/// the health-check timer, and the browser/editor event relays into the ViewModel.
/// </remarks>
public partial class EmbeddedSftpView : UserControl, IDisposable
{
    private const double FileListWidthPadding = 10;
    private const double MinimumNameColumnWidth = 200;
    // Toolbar width (px) below which the labelled actions collapse into the
    // overflow menu. First estimate — tune against split-pane screenshots.
    private const double ToolbarCompactThresholdPx = 780;

    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly EmbeddedSftpViewModel _viewModel;
    private readonly IHostKeyVerifier _hostKeyVerifier;

    private IRemoteBrowser? _browser;
    // The decorated browser (LoggingRemoteBrowser when wired) used for the inline editor's own
    // transfers so its open-download and non-sudo save are journaled like every other operation. The
    // raw _browser is kept only for events, is-checks, and lifecycle.
    private IRemoteBrowser? _operationsBrowser;
    private RemoteFileEditor? _editor;
    // Records the editor's privileged (sudo) saves, which bypass both the decorator and the ViewModel.
    private SessionOperationEmitter _operationEmitter = SessionOperationEmitter.Disabled;
    private SessionTabViewModel? _sessionTab;
    private LocalizationManager? _localizer;
    private IDialogService? _dialogService;
    private SshConnectionParams? _sshParams;
    private Heimdall.Ssh.HostKeyStore _hostKeyStore = null!;
    private readonly HashSet<string> _activeEditTempDirs =
        new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _healthTimer;
    private string? _pendingBrowserSecurityStatus;

    private bool _disposed;
    private bool _toolbarCompact;

    /// <summary>
    /// Optional shared operations-log sink. When set, transfer operations are recorded through a
    /// <see cref="LoggingRemoteBrowser"/> decorator. Wired by <c>EmbeddedSessionManager</c>.
    /// </summary>
    public ISessionOperationLog? SessionOperationLog { get; set; }

    /// <summary>
    /// Reads the LIVE global session-logging toggle. Wired by <c>EmbeddedSessionManager</c> so the
    /// gate decision honours toggle changes without a restart.
    /// </summary>
    public Func<bool>? SessionLoggingEnabledProvider { get; set; }

    /// <summary>
    /// Raised when the user clicks the Split button in the header strip.
    /// The subscriber (EmbeddedSessionManager) shows the split picker context menu.
    /// </summary>
    public event Action? SplitRequested
    {
        add => _viewModel.SplitRequested += value;
        remove => _viewModel.SplitRequested -= value;
    }

    /// <summary>
    /// Raised when the user selects "Open in terminal" from the context menu.
    /// The parameter is the remote directory path to cd into.
    /// </summary>
    public event Action<string>? OpenInTerminalRequested
    {
        add => _viewModel.OpenInTerminalRequested += value;
        remove => _viewModel.OpenInTerminalRequested -= value;
    }

    /// <summary>
    /// Raised when the user asks the shared pane lifecycle to reconnect this session.
    /// </summary>
    public event Action? ReconnectRequested;

    /// <summary>
    /// Raised when the user asks the shared pane lifecycle to close this session tab.
    /// </summary>
    public event Action? CloseRequested;

    public EmbeddedSftpView()
    {
        InitializeComponent();
        var services = (Application.Current as App)?.Services;
        var uiDispatcher = services?.GetRequiredService<IUiDispatcher>()
            ?? throw new InvalidOperationException("IUiDispatcher is not registered.");
        _hostKeyVerifier = services?.GetRequiredService<IHostKeyVerifier>()
            ?? throw new InvalidOperationException("IHostKeyVerifier is not registered.");
        _viewModel = new EmbeddedSftpViewModel(uiDispatcher);
        DataContext = _viewModel;
    }

    /// <summary>
    /// Wires the view to a connected SFTP browser session.
    /// Must be called exactly once, immediately after construction.
    /// </summary>
    public void InitializeSession(
        IRemoteBrowser browser,
        SessionTabViewModel sessionTab,
        string displayName,
        string endpoint,
        LocalizationManager localizer,
        IDialogService dialogService,
        Heimdall.Ssh.HostKeyStore hostKeyStore,
        SshConnectionParams? sshParams = null,
        string? initialRemotePath = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(sessionTab);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(hostKeyStore);

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EmbeddedSftpView));
        }

        // Keep the RAW browser for the view's own event subscriptions and the
        // is SftpBrowser / is FtpBrowser checks below. The ViewModel and the editor receive a
        // LoggingRemoteBrowser decorator (when the operations log is wired) so the NON-sudo transfer
        // operations are recorded; the raw browser's lifecycle stays owned by this view's teardown.
        _browser = browser;
        _sessionTab = sessionTab;
        _localizer = localizer;
        _dialogService = dialogService;
        _sshParams = sshParams;
        _hostKeyStore = hostKeyStore;

        string operationProtocol = browser is SftpBrowser ? "SFTP" : "FTP";
        string? rawOperationHost = browser is FtpBrowser ftpOperationHost
            ? ftpOperationHost.Host
            : sshParams?.Host;
        string operationHost = GraphicalSessionEventHelpers.ResolveHost(rawOperationHost, displayName);

        IRemoteBrowser operationsBrowser = CreateOperationsBrowser(browser, operationProtocol, operationHost);
        _operationsBrowser = operationsBrowser;

        // View-owned emitter for the editor sudo-save signal (same sink/provider/protocol/host as the
        // decorator and the ViewModel sudo emitter, so all operation records stay consistent).
        _operationEmitter = SessionOperationLog is not null && !string.IsNullOrWhiteSpace(operationHost)
            ? new SessionOperationEmitter(
                SessionOperationLog,
                SessionLoggingEnabledProvider ?? (static () => false),
                operationProtocol,
                operationHost)
            : SessionOperationEmitter.Disabled;

        _viewModel.Initialize(
            operationsBrowser,
            sessionTab,
            displayName,
            endpoint,
            localizer,
            dialogService,
            hostKeyStore,
            _hostKeyVerifier,
            sshParams,
            rawBrowser: browser,
            operationLog: SessionOperationLog,
            sessionLoggingEnabledProvider: SessionLoggingEnabledProvider,
            operationProtocol: operationProtocol,
            operationHost: operationHost);

        _editor = new RemoteFileEditor(
            operationsBrowser,
            hostKeyStore: hostKeyStore,
            hostKeyVerifier: _hostKeyVerifier);
        _editor.FileUploaded += OnEditorFileUploaded;
        _editor.HostKeyRotatedDuringUpload += OnHostKeyRotatedDuringUpload;
        _editor.SudoSaveCompleted += OnEditorSudoSaveCompleted;

        // Hide sudo toggle for FTP sessions (no SSH channel for sudo)
        BtnSudoMode.Visibility = sshParams is not null
            ? Visibility.Visible : Visibility.Collapsed;

        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = endpoint;

        _browser.DirectoryChanged += OnDirectoryChanged;
        _browser.TransferProgress += OnTransferProgress;
        _browser.Disconnected += OnBrowserDisconnected;
        if (_browser is SftpBrowser sftpBrowser)
        {
            sftpBrowser.SecurityEventOccurred += OnBrowserSecurityEvent;
        }
        else if (_browser is FtpBrowser ftpBrowser && !ftpBrowser.IsTlsEnabled)
        {
            string cleartextWarning = localizer["WarnFtpCleartextBadge"];
            _viewModel.ShowCleartextWarning(cleartextWarning);
            System.Windows.Automation.AutomationProperties.SetName(
                CleartextWarningBadge,
                cleartextWarning);
        }

        UpdateStatus(_localizer["SftpStatusConnected"]);
        StartHealthTimer();

        _ = NavigateInitialAsync(initialRemotePath);
    }

    /// <summary>
    /// Wraps the raw browser in a <see cref="LoggingRemoteBrowser"/> when the operations log is wired,
    /// so the NON-sudo transfer operations are recorded. Returns the raw browser unchanged when no sink
    /// is set or no usable host could be resolved (defensive fallback). The <paramref name="protocol"/>
    /// and <paramref name="host"/> are the same values handed to the ViewModel's sudo emitter, so the
    /// decorator and the sudo records stay consistent.
    /// </summary>
    private IRemoteBrowser CreateOperationsBrowser(
        IRemoteBrowser browser,
        string protocol,
        string host)
    {
        if (SessionOperationLog is null || string.IsNullOrWhiteSpace(host))
        {
            return browser;
        }

        return new LoggingRemoteBrowser(
            browser,
            SessionOperationLog,
            SessionLoggingEnabledProvider ?? (static () => false),
            protocol,
            host);
    }

    public string CurrentPath => _viewModel.CurrentPath;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.MarkDisposed();

        StopHealthTimer();

        if (_editor is not null)
        {
            _editor.FileUploaded -= OnEditorFileUploaded;
            _editor.HostKeyRotatedDuringUpload -= OnHostKeyRotatedDuringUpload;
            _editor.SudoSaveCompleted -= OnEditorSudoSaveCompleted;
            _editor.Dispose();
            _editor = null;
        }

        if (_browser is not null)
        {
            _browser.DirectoryChanged -= OnDirectoryChanged;
            _browser.TransferProgress -= OnTransferProgress;
            _browser.Disconnected -= OnBrowserDisconnected;
            if (_browser is SftpBrowser sftpBrowser)
            {
                sftpBrowser.SecurityEventOccurred -= OnBrowserSecurityEvent;
            }

            try { _browser.Disconnect(); }
            catch (ObjectDisposedException) { /* Expected when disposing already-closed connection */ }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"EmbeddedSFTP Disconnect during dispose failed: {ex.Message}");
            }

            try { _browser.Dispose(); }
            catch (ObjectDisposedException) { /* Expected when disposing already-closed browser */ }

            _browser = null;
        }

        // The decorator wraps the raw browser and owns no resources of its own (its Dispose is a no-op);
        // drop the reference so a late edit cannot run against a torn-down session.
        _operationsBrowser = null;

        List<string> activeEditTempDirs = _activeEditTempDirs.ToList();
        foreach (string tempPath in activeEditTempDirs)
        {
            CleanupEditTempDir(tempPath);
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP Dispose completed");
    }

    // ------------------------------------------------------------------
    // Keyboard shortcuts
    // ------------------------------------------------------------------

    private void OnViewKeyDown(object sender, KeyEventArgs e)
    {
        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.F5:
                _ = _viewModel.Refresh();
                e.Handled = true;
                break;

            case Key.F2:
                _viewModel.RenameSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete:
                _viewModel.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                if (FileListView.SelectedItem is SftpFileInfo enterFile)
                {
                    if (!_viewModel.HandleFileDoubleClick(enterFile))
                    {
                        _ = EditFileAsync(enterFile);
                    }
                }
                e.Handled = true;
                break;

            case Key.Back:
                _ = _viewModel.NavigateBack();
                e.Handled = true;
                break;

            case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                OnCtxCopyPathClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FilterTextBox.Focus();
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------
    // Directory navigation
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // Column sorting
    // ------------------------------------------------------------------

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null)
        {
            return;
        }

        if (FileListView.View is not GridView gridView)
        {
            return;
        }

        int colIndex = gridView.Columns.IndexOf(header.Column);
        string? columnName = colIndex switch
        {
            0 => "Name",
            1 => "Size",
            2 => "Modified",
            3 => "Permissions",
            4 => "Owner",
            _ => null
        };
        if (columnName is null)
        {
            return;
        }

        _viewModel.ToggleSortColumn(columnName);
        UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        if (FileListView?.View is not GridView gridView)
        {
            return;
        }

        string[] names = ["Name", "Size", "Modified", "Permissions", "Owner"];
        string arrow = _viewModel.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? " \u25B2"
            : " \u25BC";

        for (int i = 0; i < gridView.Columns.Count && i < names.Length; i++)
        {
            string baseName = _localizer?[$"SftpCol{names[i]}"] ?? names[i];
            gridView.Columns[i].Header = string.Equals(names[i], _viewModel.SortColumn, StringComparison.Ordinal)
                ? baseName + arrow
                : baseName;
        }
    }

    // ------------------------------------------------------------------
    // Selection info
    // ------------------------------------------------------------------

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SetSelection(GetSelectedFiles(), FileListView.SelectedItem as SftpFileInfo);
    }

    // ------------------------------------------------------------------
    // Navigation events
    // ------------------------------------------------------------------

    private void OnDirectoryChanged(string newPath)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                _viewModel.CurrentPath = newPath;
            }
        });
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is not SftpFileInfo file)
        {
            return;
        }

        if (!_viewModel.HandleFileDoubleClick(file))
        {
            _ = EditFileAsync(file);
        }
    }

    // ------------------------------------------------------------------
    // Bookmarks
    // ------------------------------------------------------------------

    private void OnFileListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FileListView?.View is not GridView gv || gv.Columns.Count < 5)
        {
            return;
        }

        double fixedWidth = 0;
        for (int i = 1; i < gv.Columns.Count; i++)
        {
            fixedWidth += gv.Columns[i].ActualWidth;
        }

        double available = FileListView.ActualWidth
            - fixedWidth
            - SystemParameters.VerticalScrollBarWidth
            - FileListWidthPadding;

        if (available > MinimumNameColumnWidth)
        {
            gv.Columns[0].Width = available;
        }
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < ToolbarCompactThresholdPx;
        if (compact == _toolbarCompact)
        {
            return;
        }

        _toolbarCompact = compact;
        ApplyToolbarLayout(compact);
    }

    private void ApplyToolbarLayout(bool compact)
    {
        Visibility inlineActions = compact ? Visibility.Collapsed : Visibility.Visible;
        Visibility overflow = compact ? Visibility.Visible : Visibility.Collapsed;

        BtnUpload.Visibility = inlineActions;
        BtnNewFolder.Visibility = inlineActions;
        ActionsPrivilegeSeparator.Visibility = inlineActions;
        PrivilegeBookmarksSeparator.Visibility = inlineActions;
        BtnBookmarkMenu.Visibility = inlineActions;
        BtnSudoModeText.Visibility = inlineActions;
        BtnOverflowMenu.Visibility = overflow;
    }

    private void OnToolbarMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private void OnBookmarksClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Bookmarks.Count == 0)
        {
            UpdateStatus(_localizer?["SftpBookmarkEmpty"] ?? "No bookmarks");
            return;
        }

        var menu = new ContextMenu
        {
            PlacementTarget = _toolbarCompact ? BtnOverflowMenu : BtnBookmarkMenu,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (string path in _viewModel.Bookmarks)
        {
            var item = new MenuItem
            {
                Header = path,
                Icon = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                    Text = "\uE8B7",
                    FontSize = 14
                }
            };
            string capturedPath = path;
            item.Click += (_, _) => _ = NavigateRemoteAsync(capturedPath);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------
    // File operations
    // ------------------------------------------------------------------

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            return;
        }

        OpenFileDialog dialog = new()
        {
            Multiselect = true,
            Title = _localizer?["SftpBtnUpload"] ?? "Upload"
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        await _viewModel.UploadFilesAsync(dialog.FileNames);
    }

    private async void OnCtxDownloadClick(object sender, RoutedEventArgs e)
    {
        List<SftpFileInfo> selected = GetSelectedFiles();
        if (selected.Count == 0 || _browser is null)
        {
            return;
        }

        if (_viewModel.IsTransferInProgress)
        {
            UpdateStatus(_localizer?["SftpTransferInProgress"] ?? "A file transfer is already in progress.");
            return;
        }

        System.Windows.Forms.FolderBrowserDialog dialog = new()
        {
            Description = _localizer?["SftpBtnDownload"] ?? "Download"
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        await _viewModel.DownloadFilesAsync(selected, dialog.SelectedPath);
    }

    // ------------------------------------------------------------------
    // Context menu actions
    // ------------------------------------------------------------------

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = FileListView.SelectedItem is not null;
        bool isFile = FileListView.SelectedItem is SftpFileInfo f && !f.IsDirectory;
        bool isDir = FileListView.SelectedItem is SftpFileInfo d && d.IsDirectory;

        CtxOpen.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxEdit.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        CtxDownload.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxRename.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxDelete.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxChmod.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxCopyPath.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxProperties.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // Always visible
        CtxUploadHere.Visibility = Visibility.Visible;
        CtxOpenInTerminal.Visibility = Visibility.Visible;
    }

    private void OnCtxOpenClick(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is SftpFileInfo file)
        {
            if (file.IsDirectory)
            {
                _ = NavigateRemoteAsync(file.FullPath);
            }
            else
            {
                _ = EditFileAsync(file);
            }
        }
    }

    private void OnCtxEditClick(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is SftpFileInfo file && !file.IsDirectory)
        {
            _ = EditFileAsync(file);
        }
    }

    private void OnCtxEditExternalClick(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is SftpFileInfo file && !file.IsDirectory && _editor is not null)
        {
            _ = EditFileExternalAsync(file);
        }
    }

    private async Task EditFileExternalAsync(SftpFileInfo file)
    {
        if (_disposed || _editor is null)
        {
            return;
        }

        try
        {
            UpdateStatus(_localizer?.Format("SftpStatusEditing", file.Name)
                ?? $"Editing: {file.Name}");

            try
            {
                await _editor.EditFileAsync(file.FullPath);
            }
            catch (Exception ex) when (_sshParams is not null && EmbeddedSftpViewModel.IsPermissionDenied(ex))
            {
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSFTP external edit permission denied, falling back to sudo for {file.Name}");
                await _editor.EditFileSudoAsync(file.FullPath, _sshParams);
            }
        }
        catch (Exception ex)
        {
            ShowError(_viewModel.DescribeTransferError(ex));
        }
    }

    private void OnCtxCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is SftpFileInfo file)
        {
            Clipboard.SetText(file.FullPath);
            UpdateStatus(_localizer?.Format("SftpStatusPathCopied", file.FullPath)
                ?? $"Copied: {file.FullPath}");
        }
    }

    private async Task EditFileAsync(SftpFileInfo file)
    {
        if (_disposed || _operationsBrowser is null)
        {
            return;
        }

        string? tempPath = null;

        try
        {
            UpdateStatus(_localizer?.Format("SftpStatusEditing", file.Name)
                ?? $"Editing: {file.Name}");

            // Download file content for embedded editing
            tempPath = Path.Combine(Path.GetTempPath(), "Heimdall", "edit", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            _activeEditTempDirs.Add(tempPath);
            string localPath = Path.Combine(tempPath, Path.GetFileName(file.Name));

            bool useSudo = false;
            try
            {
                await _operationsBrowser.DownloadFileAsync(file.FullPath, localPath);
            }
            catch (Exception ex) when (_sshParams is not null && EmbeddedSftpViewModel.IsPermissionDenied(ex))
            {
                // Sudo download fallback
                Core.Logging.FileLogger.Info(
                    $"EmbeddedSFTP edit permission denied, falling back to sudo for {file.Name}");
                useSudo = true;

                if (_editor is not null)
                {
                    await _editor.EditFileSudoAsync(file.FullPath, _sshParams);
                    CleanupEditTempDir(tempPath);
                    return;
                }
            }

            if (!useSudo)
            {
                string content = await File.ReadAllTextAsync(localPath);
                var remotePath = file.FullPath;

                // Open in embedded AvalonEdit editor
                var editorView = new EmbeddedEditorView(_localizer);
                editorView.OpenContent(file.Name, content);

                // Track whether an upload is in progress to prevent close during save
                bool isSaving = false;

                // Save → upload back to server
                editorView.FileSaved += async (_, savedContent) =>
                {
                    isSaving = true;
                    try
                    {
                        try
                        {
                            await File.WriteAllTextAsync(localPath, savedContent);
                        }
                        catch (Exception localWriteEx)
                        {
                            ShowError(_viewModel.DescribeTransferError(localWriteEx));
                            return;
                        }

                        try
                        {
                            await _operationsBrowser.UploadFileAsync(localPath, remotePath);
                            UpdateStatus(_localizer?.Format("SftpStatusAutoUploaded", file.Name)
                                ?? $"Uploaded: {file.Name}");
                            editorView.ConfirmRemoteSaved();
                        }
                        catch (Exception uploadEx)
                            when (_sshParams is not null && EmbeddedSftpViewModel.IsPermissionDenied(uploadEx))
                        {
                            Core.Logging.FileLogger.Info(
                                $"EmbeddedSFTP inline save permission denied, falling back to sudo for {file.Name}");

                            try
                            {
                                await _viewModel.UploadViaSudoAsync(localPath, remotePath, CancellationToken.None);
                                UpdateStatus(_localizer?.Format("SftpStatusUploadedViaSudo", file.Name)
                                    ?? $"Saved via sudo: {file.Name}");
                                editorView.ConfirmRemoteSaved();
                            }
                            catch (Exception sudoEx)
                            {
                                ShowError(_viewModel.DescribeTransferError(sudoEx));
                            }
                        }
                        catch (Exception uploadEx)
                        {
                            ShowError(_viewModel.DescribeTransferError(uploadEx));
                        }
                    }
                    finally
                    {
                        isSaving = false;
                    }
                };

                // Close → restore SFTP panel, refresh listing
                var sftpPanel = this;
                var parentTab = _sessionTab;
                editorView.CloseRequested += () =>
                {
                    if (isSaving)
                    {
                        // Upload still in progress; ignore close to avoid deleting temp file
                        return;
                    }

                    if (parentTab is not null)
                    {
                        // Find the pane containing the editor and swap back to SFTP
                        var editorPane = Heimdall.Core.Models.SplitTreeHelper.FindPaneByHostControl(
                            parentTab.RootContent, editorView);
                        if (editorPane is not null)
                        {
                            editorPane.HostControl = sftpPanel;
                        }
                    }
                    _ = RefreshRemoteAsync();
                };

                // Replace this SFTP view with the editor in the pane that contains it
                if (_sessionTab is not null)
                {
                    var sftpPane = Heimdall.Core.Models.SplitTreeHelper.FindPaneByHostControl(
                        _sessionTab.RootContent, this);
                    if (sftpPane is not null)
                    {
                        sftpPane.HostControl = editorView;
                    }
                }

                // Cleanup temp on close
                editorView.CloseRequested += () =>
                {
                    if (isSaving) return;
                    CleanupEditTempDir(tempPath);
                };
            }
            else
            {
                CleanupEditTempDir(tempPath);
            }
        }
        catch (Exception ex)
        {
            ShowError(_localizer?.Format("SftpStatusEditOpenFailed", ex.Message) ?? ex.Message);
            if (tempPath is not null)
            {
                CleanupEditTempDir(tempPath);
            }
        }
    }

    private void CleanupEditTempDir(string tempPath)
    {
        if (!_activeEditTempDirs.Contains(tempPath))
        {
            return;
        }

        try
        {
            Directory.Delete(tempPath, recursive: true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] edit temp directory cleanup failed: {ex.Message}");
        }
        finally
        {
            _activeEditTempDirs.Remove(tempPath);
        }
    }

    // ------------------------------------------------------------------
    // Drag and drop
    // ------------------------------------------------------------------

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        bool hasFiles = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
        e.Effects = hasFiles
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;

        if (hasFiles)
        {
            DragOverlay.Visibility = Visibility.Visible;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;

        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            return;
        }

        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        var files = paths.Where(p => File.Exists(p)).ToArray();
        int folderCount = paths.Length - files.Length;

        if (folderCount > 0 && _dialogService is not null)
        {
            string msg = _localizer?.Format("SftpDropFoldersSkipped", folderCount.ToString())
                ?? $"{folderCount} folder(s) will be skipped. Continue?";

            if (files.Length == 0)
            {
                UpdateStatus(_localizer?["SftpStatusFoldersNotSupported"]
                    ?? "Folder upload not supported.");
                return;
            }

            bool proceed = await _dialogService.ShowConfirmAsync(
                _localizer?["SftpDropFoldersTitle"] ?? "Folders detected",
                msg);

            if (!proceed)
            {
                return;
            }
        }

        if (files.Length > 0)
        {
            await _viewModel.UploadFilesAsync(files);
        }
    }

    // ------------------------------------------------------------------
    // Transfer progress
    // ------------------------------------------------------------------

    private void OnTransferProgress(SftpTransferProgress progress)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _viewModel.UpdateTransferProgress(progress);
        });
    }

    // ------------------------------------------------------------------
    // Connection lifecycle
    // ------------------------------------------------------------------

    private void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP Disconnect requested by user");
        try
        {
            _browser?.Disconnect();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedSFTP manual disconnect failed: {ex.Message}");
        }

        UpdateStatus(_localizer?["SftpStatusDisconnected"] ?? "Disconnected");
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP reconnect requested by user");
        UpdateStatus(_localizer?["SftpStatusReconnecting"] ?? "Reconnecting...");
        ReconnectRequested?.Invoke();
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP close requested by user");
        CloseRequested?.Invoke();
    }

    private void OnBrowserDisconnected(string? errorMessage)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            string status = string.IsNullOrWhiteSpace(errorMessage)
                ? (_localizer?["SftpStatusDisconnected"] ?? "Disconnected")
                : (_localizer?.Format("SftpErrorSessionDied", errorMessage)
                    ?? $"Session lost: {errorMessage}");

            if (_pendingBrowserSecurityStatus is { } securityStatus)
            {
                _pendingBrowserSecurityStatus = null;
                ShowError(securityStatus);
            }
            else
            {
                UpdateStatus(status);
            }
        });
    }

    // ------------------------------------------------------------------
    // Health check
    // ------------------------------------------------------------------

    private void StartHealthTimer()
    {
        _healthTimer = new System.Threading.Timer(
            _ => CheckHealth(), null,
            SftpOperationTimeout,
            SftpOperationTimeout);
    }

    private void StopHealthTimer()
    {
        _healthTimer?.Dispose();
        _healthTimer = null;
    }

    private void CheckHealth()
    {
        if (_disposed || _browser is null)
        {
            return;
        }

        if (!_browser.IsConnected)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    UpdateStatus(_localizer?["SftpStatusHealthCheckFailed"] ?? "Connection lost");
                }
            });

            StopHealthTimer();
        }
    }

    // ------------------------------------------------------------------
    // Editor auto-upload callback
    // ------------------------------------------------------------------

    private void OnEditorFileUploaded(string remotePath, bool success)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            string fileName = Path.GetFileName(remotePath);

            if (success)
            {
                UpdateStatus(_localizer?.Format("SftpStatusAutoUploaded", fileName)
                    ?? $"Auto-uploaded: {fileName}");
            }
            else
            {
                if (_editor?.GetActiveEdits().Contains(remotePath, StringComparer.Ordinal) != true)
                {
                    return;
                }

                ShowError(_localizer?.Format("SftpStatusAutoUploadFailed", fileName, "upload error")
                    ?? $"Auto-upload failed: {fileName}");
            }
        });
    }

    private void OnHostKeyRotatedDuringUpload(HostKeyRotationEvent evt)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            ShowError(_localizer?.Format(
                    "SftpHostKeyRotatedDuringUpload",
                    evt.RemotePath,
                    evt.Host,
                    evt.Port,
                    evt.PresentedFingerprint)
                ?? $"Host key for {evt.Host}:{evt.Port} changed while saving {evt.RemotePath}. Save aborted.");
            _editor?.CloseEdit(evt.RemotePath);
        });
    }

    // Records the editor's privileged (sudo) save as a single operations entry. This is the ONLY log
    // for a sudo editor save: that path uses its own SSH/SFTP clients and bypasses both the browser
    // decorator and the ViewModel sudo emitter. Non-sudo editor saves go through the decorated browser
    // and are NOT signalled here, so they are never double-logged. Logging only; no UI marshalling.
    private void OnEditorSudoSaveCompleted(RemoteEditorSudoSaveCompleted evt)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _operationEmitter.EmitUploadCompleted(
                evt.LocalPath,
                evt.RemotePath,
                evt.Success,
                () => new FileInfo(evt.LocalPath).Length,
                privileged: true);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"EmbeddedSFTP: failed to record sudo editor save for {evt.RemotePath}: {ex.Message}");
        }
    }

    private void OnBrowserSecurityEvent(SshSessionSecurityEvent evt)
    {
        if (evt.Code != SshFailureCode.HostKeyMismatch)
        {
            return;
        }

        var message = FormatHostKeyMismatchMidSession(evt);
        _pendingBrowserSecurityStatus = message;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            ShowError(message);
        });
    }

    private string FormatHostKeyMismatchMidSession(SshSessionSecurityEvent evt)
    {
        return _localizer?.Format(
                "SftpHostKeyMismatchMidSession",
                evt.Host,
                evt.Port,
                evt.PresentedFingerprint ?? "?",
                evt.StoredFingerprint ?? "?")
            ?? $"Security warning: host key for {evt.Host}:{evt.Port} changed during the session. Presented fingerprint: {evt.PresentedFingerprint ?? "?"}. Trusted fingerprint: {evt.StoredFingerprint ?? "?"}.";
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private void UpdateStatus(string text)
    {
        _viewModel.UpdateStatus(text);
    }

    private void ShowError(string message)
    {
        _viewModel.SetErrorStatus(message);
    }

    private List<SftpFileInfo> GetSelectedFiles()
    {
        return FileListView.SelectedItems.Cast<SftpFileInfo>().ToList();
    }

    private Task NavigateRemoteAsync(string path)
    {
        return _viewModel.NavigateToPath(path);
    }

    private Task NavigateInitialAsync(string? initialRemotePath)
    {
        return _viewModel.NavigateInitialAsync(initialRemotePath);
    }

    private Task RefreshRemoteAsync()
    {
        return _viewModel.Refresh();
    }
}
