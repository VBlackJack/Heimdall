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
using Heimdall.Core.Models;
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
/// hand-off (<c>EditFileAsync</c> creates an <c>EmbeddedEditorView</c> and overlays
/// it without transferring pane ownership); the bookmarks overflow <c>ContextMenu</c> built in
/// code; and session lifecycle — browser/editor creation, reconnect requests,
/// the health-check timer, and the browser/editor event relays into the ViewModel.
/// <para>
/// It is also the pane's <see cref="ICloseGuard"/>. The interface is implemented here rather than
/// by a wrapper because this instance is what every close path hands to the arbiter as
/// <c>SessionPaneModel.HostControl</c>, including while the inline editor overlay is up. The
/// policy itself stays in <see cref="EmbeddedSftpCloseGuard"/>; this class only reads its own
/// state into it and forwards the three members.
/// </para>
/// </remarks>
public partial class EmbeddedSftpView : UserControl, IDisposable, ICloseGuard
{
    private const long MaxInlineEditFileBytes = 16L * 1024 * 1024;

    private const double FileListWidthPadding = 10;
    private const double MinimumNameColumnWidth = 200;
    // Toolbar width (px) below which the labelled actions collapse into the
    // overflow menu. First estimate — tune against split-pane screenshots.
    private const double ToolbarCompactThresholdPx = 780;

    // Severity hint for the close confirmation: losing an in-flight transfer or unsaved edits is a
    // warning-grade question rather than an informational one.
    private const string CloseGuardConfirmSeverity = "warning";

    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly EmbeddedSftpViewModel _viewModel;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly EmbeddedSftpCloseGuard _closeGuard;

    private IRemoteBrowser? _browser;
    // The decorated browser (LoggingRemoteBrowser when wired) used for the inline editor's own
    // transfers so its open-download and non-sudo save are journaled like every other operation. The
    // raw _browser is kept only for events, is-checks, and lifecycle.
    private IRemoteBrowser? _operationsBrowser;
    private RemoteFileEditor? _editor;
    // Records the editor's privileged (sudo) saves, which bypass both the decorator and the ViewModel.
    private SessionOperationEmitter _operationEmitter = SessionOperationEmitter.Disabled;
    private SessionTabViewModel? _sessionTab;
    private SessionPaneModel? _ownerPane;
    private LocalizationManager? _localizer;
    private IDialogService? _dialogService;
    private SshConnectionParams? _sshParams;
    private Heimdall.Ssh.HostKeyStore _hostKeyStore = null!;
    private readonly HashSet<string> _activeEditTempDirs =
        new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _healthTimer;
    private string? _pendingBrowserSecurityStatus;
    private EmbeddedEditorView? _activeInlineEditor;
    private CancellationTokenSource? _inlineEditorCancellation;
    private string? _activeInlineEditorTempPath;

    private bool _disposed;
    private bool _inlineEditorSaveInProgress;
    private bool _toolbarCompact;

    /// <summary>
    /// Last unsaved-text flag folded into <see cref="_closeGuardEpoch"/>. Held so the stamp only
    /// moves on a real transition of that flag.
    /// </summary>
    private bool _closeGuardEditorDirty;

    /// <summary>
    /// Change stamp over the two protected states this view owns, the inline editor's save-in-flight
    /// flag and its unsaved text. Only ever increases, and is added to the ViewModel's transfer
    /// stamp so the close protocol sees a change whichever of the three moved. Written only on the
    /// UI thread, which is also the only thread the protocol samples from.
    /// </summary>
    private long _closeGuardEpoch;

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

    /// <summary>Per-profile session-logging override. Null means inherit the live global setting.</summary>
    public bool? SessionLoggingOverride { get; set; }

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

    public void SetOwningPane(SessionPaneModel pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        _ownerPane = pane;
        _ownerPane.SftpFollowSshDirectory = IsFollowSshDirectoryEnabled;
    }

    internal SessionPaneModel? OwningPane => _ownerPane;

    public bool IsFollowSshDirectoryEnabled => BtnFollowSshDirectory.IsChecked == true;

    public void SetFollowSshDirectoryEnabled(bool enabled)
    {
        BtnFollowSshDirectory.IsChecked = enabled;
        if (_ownerPane is not null)
        {
            _ownerPane.SftpFollowSshDirectory = enabled;
        }
    }

    public EmbeddedSftpView()
        : this(
            ResolveRequiredService<IUiDispatcher>(),
            ResolveRequiredService<IRemoteClipboardService>(),
            ResolveRequiredService<IHostKeyVerifier>())
    {
    }

    internal EmbeddedSftpView(
        IUiDispatcher uiDispatcher,
        IRemoteClipboardService remoteClipboard,
        IHostKeyVerifier hostKeyVerifier)
    {
        ArgumentNullException.ThrowIfNull(uiDispatcher);
        ArgumentNullException.ThrowIfNull(remoteClipboard);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        _hostKeyVerifier = hostKeyVerifier;
        _viewModel = new EmbeddedSftpViewModel(uiDispatcher, remoteClipboard);

        // Built here rather than at InitializeSession so a pane is guarded for its whole lifetime:
        // the delegates read the fields below when the arbiter calls them, not now.
        _closeGuard = new EmbeddedSftpCloseGuard(
            SampleCloseGuardSnapshot,
            ConfirmCloseAsync,
            DescribeClosePane);
        InitializeComponent();
        DataContext = _viewModel;
    }

    internal EmbeddedEditorView? ActiveInlineEditor => _activeInlineEditor;

    internal bool ShowInlineEditor(
        SessionPaneModel pane,
        EmbeddedEditorView editorView,
        string? tempPath = null)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(editorView);

        if (_disposed
            || _activeInlineEditor is not null
            || !ReferenceEquals(pane.HostControl, this))
        {
            return false;
        }

        _activeInlineEditor = editorView;
        _activeInlineEditorTempPath = tempPath;
        _inlineEditorCancellation = new CancellationTokenSource();
        if (editorView.DataContext is EmbeddedEditorViewModel editorViewModel)
        {
            editorViewModel.PropertyChanged += OnInlineEditorPropertyChanged;
        }

        RefreshCloseGuardEditorDirty();
        BrowserSurface.Visibility = Visibility.Collapsed;
        InlineEditorHost.Content = editorView;
        InlineEditorHost.Visibility = Visibility.Visible;
        AllowDrop = false;
        return true;
    }

    internal bool RestoreInlineEditor(SessionPaneModel pane, EmbeddedEditorView editorView)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(editorView);

        if (!ReferenceEquals(pane.HostControl, this)
            || !ReferenceEquals(_activeInlineEditor, editorView))
        {
            return false;
        }

        DismissInlineEditor();
        return true;
    }

    private void DismissInlineEditor()
    {
        CancellationTokenSource? cancellation = _inlineEditorCancellation;
        _inlineEditorCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        if (_activeInlineEditor?.DataContext is EmbeddedEditorViewModel editorViewModel)
        {
            editorViewModel.PropertyChanged -= OnInlineEditorPropertyChanged;
        }

        InlineEditorHost.Content = null;
        InlineEditorHost.Visibility = Visibility.Collapsed;
        BrowserSurface.Visibility = _disposed ? Visibility.Collapsed : Visibility.Visible;
        AllowDrop = !_disposed;
        _activeInlineEditor = null;
        _activeInlineEditorTempPath = null;
        RefreshCloseGuardEditorDirty();
    }

    private static T ResolveRequiredService<T>()
        where T : notnull
    {
        IServiceProvider? services = (Application.Current as App)?.Services;
        if (services is null)
        {
            throw new InvalidOperationException("Application services are not available.");
        }

        return services.GetRequiredService<T>();
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
        _viewModel.RenameFollowsSymlinkTarget = browser is SftpBrowser;
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
                operationHost,
                SessionLoggingOverride)
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
            operationLog: SessionOperationLog,
            sessionLoggingEnabledProvider: SessionLoggingEnabledProvider,
            operationProtocol: operationProtocol,
            operationHost: operationHost,
            sessionLoggingOverride: SessionLoggingOverride);

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
        BtnFollowSshDirectory.Visibility = browser is SftpBrowser
            ? Visibility.Visible : Visibility.Collapsed;

        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = endpoint;

        _browser.DirectoryChanged += OnDirectoryChanged;
        _browser.TransferProgress += OnTransferProgress;
        _browser.OperationWarningRaised += OnOperationWarningRaised;
        _browser.Disconnected += OnBrowserDisconnected;
        if (_browser is SftpBrowser sftpBrowser)
        {
            sftpBrowser.SecurityEventOccurred += OnBrowserSecurityEvent;
        }
        else if (_browser is FtpBrowser ftpBrowser)
        {
            string securityNotice = localizer[
                GetFtpSecurityNoticeLocalizationKey(ftpBrowser.IsTlsEnabled)];
            _viewModel.ShowSecurityNotice(securityNotice);
            System.Windows.Automation.AutomationProperties.SetName(
                SecurityNoticeBadge,
                securityNotice);
        }

        UpdateStatus(_localizer["SftpStatusConnected"]);
        StartHealthTimer();

        _ = ObserveFaultedTask(NavigateInitialAsync(initialRemotePath), "initial navigation");
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
            host,
            SessionLoggingOverride);
    }

    public string CurrentPath => _viewModel.CurrentPath;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        bool inlineEditorSaveInProgress = _inlineEditorSaveInProgress;
        string? activeInlineEditorTempPath = _activeInlineEditorTempPath;
        DismissInlineEditor();
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
            IRemoteBrowser browser = _browser;
            _browser = null;

            browser.DirectoryChanged -= OnDirectoryChanged;
            browser.TransferProgress -= OnTransferProgress;
            browser.OperationWarningRaised -= OnOperationWarningRaised;
            browser.Disconnected -= OnBrowserDisconnected;
            if (browser is SftpBrowser sftpBrowser)
            {
                sftpBrowser.SecurityEventOccurred -= OnBrowserSecurityEvent;
            }

            _ = ObserveFaultedTask(
                DisposeBrowserAsync(browser),
                "browser teardown");
        }

        // The decorator wraps the raw browser and owns no resources of its own (its Dispose is a no-op);
        // drop the reference so a late edit cannot run against a torn-down session.
        _operationsBrowser = null;

        List<string> activeEditTempDirs = _activeEditTempDirs.ToList();
        foreach (string tempPath in activeEditTempDirs)
        {
            if (inlineEditorSaveInProgress
                && string.Equals(
                    tempPath,
                    activeInlineEditorTempPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CleanupEditTempDir(tempPath);
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP Dispose completed");
    }

    // ------------------------------------------------------------------
    // Close guard
    // ------------------------------------------------------------------

    /// <summary>
    /// Non-blocking sample of what this pane protects, as the close protocol's first phase needs it.
    /// </summary>
    public CloseGuardState SampleCloseGuardState()
    {
        return _closeGuard.SampleCloseGuardState();
    }

    /// <summary>
    /// Synchronous close poll. The verdict is the guard's, not this view's: what an SFTP pane
    /// refuses, asks about, or lets through is decided in <see cref="EmbeddedSftpCloseGuard"/>.
    /// </summary>
    public CloseDecision PollClose(CloseRequest request)
    {
        return _closeGuard.PollClose(request);
    }

    /// <summary>
    /// Asynchronous continuation for a poll that deferred. Never cancels a transfer: the teardown
    /// that follows a granted close cancels anyway, and the user may still abandon this close.
    /// </summary>
    public Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
    {
        return _closeGuard.ResolveCloseAsync(request, cancellationToken);
    }

    /// <summary>
    /// One read of the three states this pane protects, stamped so the protocol can tell "the work
    /// finished" from "different work started".
    /// </summary>
    /// <remarks>
    /// The stamp adds two counters that only ever increase: the ViewModel's transfer stamp, taken
    /// under the same gate as its writers, and this view's own stamp, bumped on every
    /// save-in-flight and unsaved-text transition. Any change to either therefore strictly
    /// increases the sum, so it never returns to a value a consent was already granted against.
    /// </remarks>
    private SftpCloseGuardSnapshot SampleCloseGuardSnapshot()
    {
        (bool isTransferInProgress, long transferEpoch) = _viewModel.SampleTransferState();

        return new SftpCloseGuardSnapshot(
            isTransferInProgress,
            _inlineEditorSaveInProgress,
            HasUnsavedInlineEditorChanges(),
            transferEpoch + _closeGuardEpoch);
    }

    /// <summary>
    /// Raises the guard's confirmation, filling the message template with the pane label so the
    /// user can tell which pane is being asked about when several are closing at once.
    /// </summary>
    /// <remarks>
    /// Consents when the session was never initialized: there is then neither a localizer nor a
    /// dialog service, so no question could be put to anyone, and nothing has been started that a
    /// refusal would protect.
    /// </remarks>
    private Task<bool> ConfirmCloseAsync(string titleKey, string messageKey)
    {
        LocalizationManager? localizer = _localizer;
        IDialogService? dialogService = _dialogService;
        if (localizer is null || dialogService is null)
        {
            return Task.FromResult(true);
        }

        return dialogService.ShowConfirmAsync(
            localizer[titleKey],
            localizer.Format(messageKey, DescribeClosePane()),
            CloseGuardConfirmSeverity);
    }

    /// <summary>
    /// Answers the inline editor's own Close button with the same decision the pane guard makes,
    /// and says the refusal out loud. Returns <see langword="true"/> when the close is refused.
    /// </summary>
    /// <remarks>
    /// The editor overlay is the fourth surface that can close this pane, and it was the only one
    /// that stayed silent: the tab, the split pane and the floating window all already show this
    /// same sentence under this same title. Routing through
    /// <see cref="EmbeddedSftpCloseGuard.DescribeEditorSaveRefusal"/> rather than re-testing the
    /// field is what keeps the four from ever disagreeing about one save.
    /// </remarks>
    private bool RefuseInlineEditorCloseWhileSaving()
    {
        if (EmbeddedSftpCloseGuard.DescribeEditorSaveRefusal(SampleCloseGuardSnapshot())
            is not { } reasonKey)
        {
            return false;
        }

        ReportCloseGuardRefusal(reasonKey);
        return true;
    }

    /// <summary>Shows a close refusal, under the shared close-guard title.</summary>
    /// <remarks>
    /// Refuses in silence when the session was never initialized, which is the OPPOSITE of what
    /// <see cref="ConfirmCloseAsync"/> does with the same missing services. There, nothing had been
    /// started that a refusal would protect, so consent is the safe answer. Here a save is
    /// genuinely in flight, so the rule outranks our ability to explain it.
    /// </remarks>
    private void ReportCloseGuardRefusal(string reasonKey)
    {
        LocalizationManager? localizer = _localizer;
        IDialogService? dialogService = _dialogService;
        if (localizer is null || dialogService is null)
        {
            return;
        }

        dialogService.ShowInfo(
            localizer[CloseGuardLocaleKeys.BlockedTitle],
            localizer.Format(reasonKey, DescribeClosePane()));
    }

    /// <summary>The pane's own header label, interpolated into the guard's messages.</summary>
    private string DescribeClosePane()
    {
        string headerTitle = SessionTitleText.Text;
        if (!string.IsNullOrWhiteSpace(headerTitle))
        {
            return headerTitle;
        }

        return _ownerPane?.Title ?? string.Empty;
    }

    private bool HasUnsavedInlineEditorChanges()
    {
        return _activeInlineEditor?.DataContext is EmbeddedEditorViewModel editorViewModel
            && editorViewModel.IsModified;
    }

    /// <summary>
    /// Records whether the inline editor is writing back to the server, moving the close-guard
    /// stamp on each transition so a consent given before a save started cannot outlive it.
    /// </summary>
    private void SetInlineEditorSaveInProgress(bool inProgress)
    {
        if (_inlineEditorSaveInProgress == inProgress)
        {
            return;
        }

        _inlineEditorSaveInProgress = inProgress;
        _closeGuardEpoch++;
    }

    /// <summary>
    /// Folds the editor's unsaved-text flag into the close-guard stamp. Called when the overlay is
    /// attached or detached, and whenever the editor reports the flag changed.
    /// </summary>
    /// <remarks>
    /// Only a real transition moves the stamp. Bumping on every notification would invalidate a
    /// consent the user gave about a state that never changed, which the arbiter reads as new work
    /// and terminally refuses.
    /// </remarks>
    private void RefreshCloseGuardEditorDirty()
    {
        bool hasUnsavedChanges = HasUnsavedInlineEditorChanges();
        if (hasUnsavedChanges == _closeGuardEditorDirty)
        {
            return;
        }

        _closeGuardEditorDirty = hasUnsavedChanges;
        _closeGuardEpoch++;
    }

    private void OnInlineEditorPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        // An empty or null name means every property changed, so it has to refresh too.
        if (string.IsNullOrEmpty(e.PropertyName)
            || string.Equals(
                e.PropertyName,
                nameof(EmbeddedEditorViewModel.IsModified),
                StringComparison.Ordinal))
        {
            RefreshCloseGuardEditorDirty();
        }
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
        BtnFollowSshDirectoryText.Visibility = inlineActions;
        BtnOverflowMenu.Visibility = overflow;
    }

    private void OnFollowSshDirectoryToggled(object sender, RoutedEventArgs e)
    {
        if (_ownerPane is not null)
        {
            _ownerPane.SftpFollowSshDirectory = IsFollowSshDirectoryEnabled;
        }
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

        string[] fileNames;
        try
        {
            OpenFileDialog dialog = new()
            {
                Multiselect = true,
                Title = _localizer?["SftpBtnUpload"] ?? "Upload"
            };

            if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
            {
                return;
            }

            fileNames = dialog.FileNames;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] upload dialog failed: {ex.Message}");
            return;
        }

        await _viewModel.UploadFilesAsync(fileNames);
    }

    private async void OnCtxDownloadClick(object sender, RoutedEventArgs e)
    {
        List<SftpFileInfo> selected;
        try
        {
            selected = GetSelectedFiles();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] download selection read failed: {ex.Message}");
            return;
        }

        if (selected.Count == 0 || _browser is null)
        {
            return;
        }

        if (_viewModel.IsTransferInProgress)
        {
            UpdateStatus(_localizer?["SftpTransferInProgress"] ?? "A file transfer is already in progress.");
            return;
        }

        string targetDirectory;
        try
        {
            System.Windows.Forms.FolderBrowserDialog dialog = new()
            {
                Description = _localizer?["SftpBtnDownload"] ?? "Download"
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }

            targetDirectory = dialog.SelectedPath;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] download folder dialog failed: {ex.Message}");
            return;
        }

        await _viewModel.DownloadFilesAsync(selected, targetDirectory);
    }

    // ------------------------------------------------------------------
    // Context menu actions
    // ------------------------------------------------------------------

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        bool hasSelection = FileListView.SelectedItem is not null;
        bool isRegularFile = FileListView.SelectedItem is SftpFileInfo f && f.IsRegularFile;
        bool isDir = FileListView.SelectedItem is SftpFileInfo d && d.IsDirectory;
        bool singleSelection = FileListView.SelectedItems.Count == 1;

        CtxOpen.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        // Editing (integrated or external) is a single-file action: require a single regular file selection.
        // Links, fifos, sockets, and devices are hidden because Heimdall cannot write them back. Editing is
        // also hidden on directories, an empty selection, and a multi-selection.
        CtxEdit.Visibility = isRegularFile && singleSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxEditExternal.Visibility = isRegularFile && singleSelection ? Visibility.Visible : Visibility.Collapsed;
        // Offered only when the selection holds something the transfer will actually move, using
        // the same predicate the planner uses. A directory-only selection used to open a folder
        // picker and then download nothing. The handler keeps no silent guard of its own: if the
        // action is somehow reached anyway, the existing end-of-transfer message explains what was
        // skipped, and a silent return would say less than that.
        CtxDownload.Visibility = EmbeddedSftpViewModel.CanDownloadSelection(
            FileListView.SelectedItems.OfType<SftpFileInfo>())
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Rename targets exactly one entry. Native SFTP rename follows a symbolic link to its target,
        // so hide that unsafe action while preserving name-based FTP rename.
        CtxRename.Visibility = singleSelection
            && !(_browser is SftpBrowser
                && FileListView.SelectedItem is SftpFileInfo { Kind: RemoteEntryKind.SymbolicLink })
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxDelete.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        // Chmod applies to the whole selection, but only SFTP can mutate POSIX permissions.
        CtxChmod.Visibility = hasSelection && _browser is SftpBrowser
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxCut.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxCopy.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxPaste.Visibility = _viewModel.HasClipboard && _viewModel.IsConnected
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxPasteFromExplorer.Visibility = EmbeddedSftpViewModel.CanPasteFromExternalClipboard(
            ClipboardHasFileDrop(),
            _viewModel.IsConnected)
            ? Visibility.Visible
            : Visibility.Collapsed;
        CtxDuplicate.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxCopyPath.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxProperties.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        // Always visible
        CtxUploadHere.Visibility = Visibility.Visible;
        CtxOpenInTerminal.Visibility = Visibility.Visible;
    }

    private async void OnCtxPasteFromExplorerClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            return;
        }

        string[]? paths;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetDataPresent(System.Windows.DataFormats.FileDrop) != true)
            {
                return;
            }

            paths = (string[]?)data.GetData(System.Windows.DataFormats.FileDrop);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] paste from Explorer clipboard read failed: {ex.Message}");
            return;
        }

        if (paths is null || paths.Length == 0)
        {
            return;
        }

        await _viewModel.UploadFilesAsync(paths);
    }

    private static bool ClipboardHasFileDrop()
    {
        try
        {
            return Clipboard.GetDataObject()?.GetDataPresent(System.Windows.DataFormats.FileDrop) == true;
        }
        catch (Exception)
        {
            return false;
        }
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
        if (!file.IsRegularFile)
        {
            UpdateStatus(_localizer?.Format("SftpStatusEditUnsupportedEntry", file.Name)
                ?? $"Cannot edit {file.Name}: this entry is not a regular file.");
            return;
        }

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
        if (!file.IsRegularFile)
        {
            UpdateStatus(_localizer?.Format("SftpStatusEditUnsupportedEntry", file.Name)
                ?? $"Cannot edit {file.Name}: this entry is not a regular file.");
            return;
        }

        IRemoteBrowser? operationsBrowser = _operationsBrowser;
        if (_disposed || operationsBrowser is null)
        {
            return;
        }

        if (file.Size > MaxInlineEditFileBytes)
        {
            ShowInlineEditFileTooLarge(file.Name);
            return;
        }

        string? tempPath = null;

        try
        {
            UpdateStatus(_localizer?.Format("SftpStatusEditing", file.Name)
                ?? $"Editing: {file.Name}");

            // Download file content for embedded editing
            tempPath = Path.Combine(
                EditorTempPaths.Root,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempPath);
            _activeEditTempDirs.Add(tempPath);
            string localPath = Path.Combine(tempPath, Path.GetFileName(file.Name));

            bool useSudo = false;
            int downloadExceededSizeLimit = 0;
            using CancellationTokenSource downloadCancellation = new();

            void EnforceInlineEditDownloadLimit(SftpTransferProgress progress)
            {
                if (!progress.IsUpload
                    && string.Equals(progress.FileName, file.Name, StringComparison.Ordinal)
                    && (progress.BytesTransferred > MaxInlineEditFileBytes
                        || progress.TotalBytes > MaxInlineEditFileBytes))
                {
                    Interlocked.Exchange(ref downloadExceededSizeLimit, 1);
                    downloadCancellation.Cancel();
                }
            }

            try
            {
                operationsBrowser.TransferProgress += EnforceInlineEditDownloadLimit;
                try
                {
                    await operationsBrowser.DownloadFileAsync(
                        file.FullPath,
                        localPath,
                        downloadCancellation.Token);
                }
                finally
                {
                    operationsBrowser.TransferProgress -= EnforceInlineEditDownloadLimit;
                }
            }
            catch (OperationCanceledException)
                when (Volatile.Read(ref downloadExceededSizeLimit) != 0)
            {
                ShowInlineEditFileTooLarge(file.Name);
                CleanupEditTempDir(tempPath);
                return;
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
                if (new FileInfo(localPath).Length > MaxInlineEditFileBytes)
                {
                    ShowInlineEditFileTooLarge(file.Name);
                    CleanupEditTempDir(tempPath);
                    return;
                }

                RemoteTextDocument document = await RemoteTextFileCodec.ReadAsync(localPath);
                string content = document.Text;
                string remotePath = file.FullPath;

                // Open in embedded AvalonEdit editor
                EmbeddedEditorView editorView = new(_localizer);

                // Assigned before the overlay is mounted, and before anything can be typed into it.
                // A guard attached to no host is a guard that never runs - that is exactly how the
                // pane's own close guard sat inert for weeks after it shipped.
                editorView.CloseRefusedByHost = RefuseInlineEditorCloseWhileSaving;
                editorView.OpenContent(file.Name, content);

                SessionPaneModel? inlineEditorPane = _ownerPane;
                if (inlineEditorPane is null && _sessionTab is not null)
                {
                    inlineEditorPane = Heimdall.Core.Models.SplitTreeHelper.FindPaneByHostControl(
                        _sessionTab.RootContent, this);
                }

                if (inlineEditorPane is null)
                {
                    CleanupEditTempDir(tempPath);
                    return;
                }

                if (!ShowInlineEditor(inlineEditorPane, editorView, tempPath))
                {
                    CleanupEditTempDir(tempPath);
                    return;
                }

                CancellationToken inlineEditorCancellation =
                    _inlineEditorCancellation?.Token ?? new CancellationToken(canceled: true);

                // Save → upload back to server
                editorView.SaveRequested += async (_, savedContent) =>
                {
                    if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                    {
                        return false;
                    }

                    SetInlineEditorSaveInProgress(true);
                    try
                    {
                        try
                        {
                            await RemoteTextFileCodec.WriteAsync(
                                localPath,
                                savedContent,
                                document,
                                inlineEditorCancellation);
                        }
                        catch (OperationCanceledException)
                            when (inlineEditorCancellation.IsCancellationRequested)
                        {
                            return false;
                        }
                        catch (Exception localWriteEx)
                        {
                            if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                            {
                                return false;
                            }

                            ShowError(_viewModel.DescribeTransferError(localWriteEx));
                            return false;
                        }

                        try
                        {
                            await operationsBrowser.UploadFileAsync(
                                localPath,
                                remotePath,
                                inlineEditorCancellation);
                            if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                            {
                                return false;
                            }

                            UpdateStatus(_localizer?.Format("SftpStatusAutoUploaded", file.Name)
                                ?? $"Uploaded: {file.Name}");
                            return true;
                        }
                        catch (OperationCanceledException)
                            when (inlineEditorCancellation.IsCancellationRequested)
                        {
                            return false;
                        }
                        catch (Exception uploadEx)
                            when (_sshParams is not null && EmbeddedSftpViewModel.IsPermissionDenied(uploadEx))
                        {
                            if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                            {
                                return false;
                            }

                            Core.Logging.FileLogger.Info(
                                $"EmbeddedSFTP inline save permission denied, falling back to sudo for {file.Name}");

                            try
                            {
                                await _viewModel.UploadViaSudoAsync(
                                    localPath,
                                    remotePath,
                                    inlineEditorCancellation);
                                if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                                {
                                    return false;
                                }

                                UpdateStatus(_localizer?.Format("SftpStatusUploadedViaSudo", file.Name)
                                    ?? $"Saved via sudo: {file.Name}");
                                return true;
                            }
                            catch (OperationCanceledException)
                                when (inlineEditorCancellation.IsCancellationRequested)
                            {
                                return false;
                            }
                            catch (Exception sudoEx)
                            {
                                if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                                {
                                    return false;
                                }

                                ShowError(_viewModel.DescribeTransferError(sudoEx));
                                return false;
                            }
                        }
                        catch (Exception uploadEx)
                        {
                            if (_disposed || inlineEditorCancellation.IsCancellationRequested)
                            {
                                return false;
                            }

                            ShowError(_viewModel.DescribeTransferError(uploadEx));
                            return false;
                        }
                    }
                    finally
                    {
                        SetInlineEditorSaveInProgress(false);
                        if (_disposed)
                        {
                            CleanupEditTempDir(tempPath);
                        }
                    }
                };

                // Close → restore SFTP panel, refresh listing, drop the temp copy.
                // One handler rather than two: both used to test the same field independently,
                // which is one rule kept in two places. The order below is load-bearing, and the
                // cleanup stays OUTSIDE the restore branch because it has to run even when the
                // pane identity check declines - otherwise a temp directory is leaked.
                editorView.CloseRequested += () =>
                {
                    if (RefuseInlineEditorCloseWhileSaving())
                    {
                        return;
                    }

                    if (RestoreInlineEditor(inlineEditorPane, editorView))
                    {
                        _ = RefreshRemoteAsync();
                    }

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
            ShowError(ex is SudoEditFileTooLargeException
                ? _viewModel.DescribeTransferError(ex)
                : _localizer?.Format("SftpStatusEditOpenFailed", ex.Message) ?? ex.Message);
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

        string[] paths;
        string targetDir;

        try
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                return;
            }

            paths = (string[]?)e.Data.GetData(System.Windows.DataFormats.FileDrop) ?? [];
            if (paths.Length == 0)
            {
                return;
            }

            // Impure hit-test: find the row under the cursor (if any); the pure helper turns it into the
            // target directory. Dropping onto a folder row uploads into it; anywhere else uses CurrentPath.
            SftpFileInfo? hoveredEntry = HitTestFileRow(e.GetPosition(FileListView));
            targetDir = EmbeddedSftpViewModel.ResolveDropTargetDirectory(
                hoveredEntry, _viewModel.CurrentPath);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] drop payload preparation failed: {ex.Message}");
            return;
        }

        await _viewModel.UploadEntriesAsync(paths, targetDir);
    }

    // Maps a point in FileListView coordinates to the SftpFileInfo of the row beneath it, or null when
    // the drop lands on empty space (or the header). Walks the visual tree up to the row container.
    private SftpFileInfo? HitTestFileRow(System.Windows.Point point)
    {
        if (FileListView.InputHitTest(point) is not DependencyObject hit)
        {
            return null;
        }

        DependencyObject? container = ItemsControl.ContainerFromElement(FileListView, hit);
        return (container as System.Windows.Controls.ListViewItem)?.DataContext as SftpFileInfo;
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

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP Disconnect requested by user");
        IRemoteBrowser? browser = _browser;
        try
        {
            if (browser is not null)
            {
                await DisconnectBrowserAsync(browser, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"EmbeddedSFTP manual disconnect failed: {ex.Message}");
        }

        if (!_disposed && ReferenceEquals(_browser, browser))
        {
            UpdateStatus(_localizer?["SftpStatusDisconnected"] ?? "Disconnected");
        }
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

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            var message = FormatHostKeyMismatchMidSession(evt);
            _pendingBrowserSecurityStatus = message;
            ShowError(message);
        });
    }

    private void OnOperationWarningRaised(RemoteOperationWarning warning)
    {
        LocalizationManager? localizer = _localizer;
        if (localizer is null)
        {
            return;
        }

        string message = localizer.Format(warning.WarningKey, warning.RemotePath);
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _viewModel.ShowOperationWarning(message);
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

    private void ShowInlineEditFileTooLarge(string fileName)
    {
        ShowError(_localizer?.Format("SftpStatusEditFileTooLarge", fileName)
            ?? "SftpStatusEditFileTooLarge");
    }

    private List<SftpFileInfo> GetSelectedFiles()
    {
        return FileListView.SelectedItems.Cast<SftpFileInfo>().ToList();
    }

    private Task NavigateRemoteAsync(string path)
    {
        return _viewModel.NavigateToPath(path);
    }

    public Task NavigateToPath(string path)
    {
        return _viewModel.NavigateToUntrustedPath(path);
    }

    private Task NavigateInitialAsync(string? initialRemotePath)
    {
        return _viewModel.NavigateInitialAsync(initialRemotePath);
    }

    internal static Task ObserveFaultedTask(
        Task task,
        string operationName,
        Action<string>? logWarning = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        Action<string> logger = logWarning ?? Core.Logging.FileLogger.Warn;

        return task.ContinueWith(
            static (faultedTask, state) =>
            {
                var (operation, logger) = ((string Operation, Action<string> Logger))state!;
                Exception exception = faultedTask.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Task faulted without an exception.");
                logger($"[EmbeddedSftpView] {operation} failed: {exception.Message}");
            },
            (operationName, logger),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal static string GetFtpSecurityNoticeLocalizationKey(bool isTlsEnabled)
        => isTlsEnabled
            ? "WarnFtpsDataChannelIdentityBadge"
            : "WarnFtpCleartextBadge";

    internal static Task DisposeBrowserAsync(IRemoteBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        return Task.Run(() =>
        {
            try
            {
                browser.Disconnect();
            }
            catch (ObjectDisposedException)
            {
                // Expected when the connection has already been closed.
            }
            finally
            {
                try
                {
                    browser.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Expected when the browser has already been disposed.
                }
            }
        });
    }

    internal static Task DisconnectBrowserAsync(
        IRemoteBrowser browser,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return browser.DisconnectAsync(ct);
    }

    private Task RefreshRemoteAsync()
    {
        return _viewModel.Refresh();
    }
}
