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
using Heimdall.App.Services.CloseGuard;
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
/// code; and session lifecycle - browser/editor creation, reconnect requests,
/// the health-check timer, and the browser/editor event relays into the ViewModel.
/// <para>
/// It is also the pane's <see cref="ICloseGuard"/>. The interface is implemented here rather than
/// by a wrapper because this instance is what every close path hands to the arbiter as
/// <c>SessionPaneModel.HostControl</c>, including while the inline editor overlay is up. The
/// policy itself stays in <see cref="EmbeddedSftpCloseGuard"/>; this class only reads its own
/// state into it and forwards the three members.
/// </para>
/// </remarks>
public partial class EmbeddedSftpView : UserControl, IDisposable, ICloseGuard, ISessionPaneOwner
{
    private const long MaxInlineEditFileBytes = 16L * 1024 * 1024;

    private const double FileListWidthPadding = 10;
    private const double MinimumNameColumnWidth = 200;

    // The arrows appended to the header of the column the list is sorted by.
    private const string SortArrowAscending = " \u25B2";
    private const string SortArrowDescending = " \u25BC";

    // Distance in pixels from the top or bottom edge of the list inside which a drag scrolls it.
    private const double DragAutoScrollEdgePx = 24;

    // Vertical distance in pixels a drag scrolls the list per drag-over event inside that zone.
    private const double DragAutoScrollStepPx = 16;

    // Severity hint for the close confirmation: losing an in-flight transfer or unsaved edits is a
    // warning-grade question rather than an informational one.
    private const string CloseGuardConfirmSeverity = "warning";

    private static readonly TimeSpan HealthCheckInterval =
        TimeSpan.FromSeconds(SftpViewMetrics.HealthCheckIntervalSeconds);

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

    /// <summary>Set when the configured external editor was refused as a shell target.</summary>
    private string? _externalEditorRejectionKey;

    /// <summary>
    /// The external editor configured in settings. Read at <see cref="InitializeSession"/>: the
    /// setting was wired to the local browser only, and a remote file always opened in notepad.
    /// </summary>
    public string? ExternalEditorPath { get; set; }

    /// <summary>Whether the connection was already dropped to escape a stuck save.</summary>
    private bool _inlineEditorEscapeAttempted;

    /// <summary>How long to wait for the save flag to clear after dropping the connection.</summary>
    /// <remarks>
    /// Its entire product is which sentence the user reads. Generous and human-scale:
    /// too short only mislabels a drop that worked as one that did not.
    /// </remarks>
    private static readonly TimeSpan SaveEscapeSettleBound = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan SaveEscapeSettlePollInterval = TimeSpan.FromMilliseconds(100);
    private bool _toolbarCompact;

    // Drag out of the list: decides when a press becomes a drag, and keeps a multi-selection whole.
    private FileListDragSource? _dragSource;
    private System.Windows.Controls.ListViewItem? _highlightedDropRow;
    private string _dragOverlayText = string.Empty;
    private object? _dragOverlayDataKey;

    // The scroll position before a listing is replaced, restored after it.
    private double? _savedScrollOffset;

    // Progress events are coalesced: the transfer thread posts, a timer on the UI thread shows.
    private readonly SftpProgressCoalescer _progressCoalescer = new();
    private System.Windows.Threading.DispatcherTimer? _progressTimer;
    private int _progressTimerRequested;

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

    public event Action? EditProfileRequested;

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
        _dragSource = new FileListDragSource(
            FileListView,
            point => HitTestFileRow(point),
            () => !_disposed && _browser is { IsConnected: true },
            StartRemoteDrag);
        _viewModel.FilesReplacing += OnFilesReplacing;
        _viewModel.SelectionRestoreRequested += OnSelectionRestoreRequested;
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
        editorView.FocusEditor();
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

        if (!_disposed)
        {
            FileListView.Focus();
        }
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

        _viewModel.AttachStateStore(
            (Application.Current as App)?.Services?.GetService<ISftpBrowserStateStore>());
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

        string editorPath = EditorLaunchPolicy.ResolveExternalEditor(ExternalEditorPath, out string? editorRejectionKey);
        _externalEditorRejectionKey = editorRejectionKey;
        _editor = new RemoteFileEditor(operationsBrowser, hostKeyStore: hostKeyStore, hostKeyVerifier: _hostKeyVerifier, editorPath: editorPath);
        _editor.FileUploaded += OnEditorFileUploaded;
        _editor.FileUploadRefused += OnEditorFileUploadRefused;
        _editor.HostKeyRotatedDuringUpload += OnHostKeyRotatedDuringUpload;
        _editor.SudoSaveCompleted += OnEditorSudoSaveCompleted;

        // Hide sudo toggle for FTP sessions (no SSH channel for sudo)
        BtnSudoMode.Visibility = sshParams is not null
            ? Visibility.Visible : Visibility.Collapsed;
        BtnFollowSshDirectory.Visibility = browser is SftpBrowser
            ? Visibility.Visible : Visibility.Collapsed;

        SessionTitleText.Text = displayName;
        EndpointTextBlock.Text = endpoint;
        UpdateColumnHeaders();

        _browser.DirectoryChanged += OnDirectoryChanged;
        _browser.TransferProgress += OnTransferProgress;
        _browser.OperationWarningRaised += OnOperationWarningRaised;
        _browser.Disconnected += OnBrowserDisconnected;
        if (_browser is SftpBrowser sftpBrowser)
        {
            sftpBrowser.SecurityEventOccurred += OnBrowserSecurityEvent;
        }

        ApplyTransportSecurityNotice(_viewModel, _browser);

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

        bool inlineEditorSaveInProgress = _inlineEditorSaveInProgress;
        string? activeInlineEditorTempPath = _activeInlineEditorTempPath;

        // Retained before _disposed is set, and that order is the whole protection.
        // The flag is what opens the delete: the save's own finally drops this directory
        // as soon as it observes a disposed view. Nothing orders that observation
        // against the rest of this method, so leaving the retention until after the
        // teardown work leaves a stretch in which the only copy of the user's typed
        // text can be deleted by a cleanup that runs inside it. Removing the path first
        // makes CleanupEditTempDir refuse it for the whole of that stretch.
        //
        // The loop below then needs no special case: this path is no longer in the set
        // it walks.
        if (inlineEditorSaveInProgress && activeInlineEditorTempPath is not null)
        {
            _activeEditTempDirs.Remove(activeInlineEditorTempPath);
        }

        _disposed = true;
        DismissInlineEditor();
        _viewModel.FilesReplacing -= OnFilesReplacing;
        _viewModel.SelectionRestoreRequested -= OnSelectionRestoreRequested;
        _viewModel.MarkDisposed();

        StopHealthTimer();
        StopProgressTimer();

        if (_editor is not null)
        {
            _editor.FileUploaded -= OnEditorFileUploaded;
            _editor.FileUploadRefused -= OnEditorFileUploadRefused;
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
            HasExternalEdits(),
            transferEpoch + _closeGuardEpoch + (_editor?.EditSessionTransitions ?? 0));
    }

    /// <summary>
    /// Whether a file of this pane is open in an external editor. The guard never consulted the
    /// editor, so the pane closed without a question and disposed the editor, which deleted the
    /// staged file under the editor still open on it.
    /// </summary>
    private bool HasExternalEdits() => _editor is not null && _editor.GetActiveEdits().Count > 0;

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
            is null)
        {
            return false;
        }

        switch (EditorSaveEscapeOutcome.Decide(
            _inlineEditorSaveInProgress,
            _inlineEditorEscapeAttempted))
        {
            case EditorSaveEscapeOutcome.Response.OfferEscape:
                // Observed, not abandoned. This is the one path a user takes when
                // everything else has already stopped responding; a throw inside it
                // vanishing without trace is the exact shape of defect it exists to end.
                _ = ObserveFaultedTask(OfferSaveEscapeAsync(), "editor save escape");
                break;

            case EditorSaveEscapeOutcome.Response.ReportOutcome:
                ShowEditorNotice(
                    EditorSaveEscapeOutcome.ReportKey(_inlineEditorSaveInProgress));
                break;
        }

        return true;
    }

    /// <summary>
    /// Offers the one act that can reach a save which will not end: dropping the SFTP
    /// connection under it.
    /// </summary>
    /// <remarks>
    /// This is not a close and not a cancellation. The write cannot be stopped - it is a
    /// synchronous call inside the SSH library that takes no cancellation token - so the
    /// only lever is the transport beneath it. Consenting therefore issues exactly the
    /// disconnect the pane's own toolbar button already issues, and nothing else: the
    /// view is not disposed, the overlay stays up with the text still in it, and the
    /// close guard's terminal refusal is untouched.
    /// <para>
    /// A user whose file LISTING is wedged has had this button in one click all along.
    /// The overlay simply hides it, because it lives inside the browser surface that
    /// showing the editor collapses. So this reveals an escape that every other stuck
    /// state already has, rather than inventing one.
    /// </para>
    /// </remarks>
    private async Task OfferSaveEscapeAsync()
    {
        LocalizationManager? localizer = _localizer;
        IDialogService? dialogService = _dialogService;
        IRemoteBrowser? browser = _browser;
        if (localizer is null || dialogService is null || browser is null)
        {
            return;
        }

        bool drop = await dialogService.ShowConfirmAsync(
            localizer[SftpCloseGuardLocaleKeys.EditorSaveEscapeTitle],
            localizer.Format(
                SftpCloseGuardLocaleKeys.EditorSaveEscapeMessage,
                DescribeClosePane(),
                _activeInlineEditorTempPath ?? string.Empty),
            CloseGuardConfirmSeverity,
            localizer[SftpCloseGuardLocaleKeys.EditorSaveEscapeConfirm],
            localizer[SftpCloseGuardLocaleKeys.EditorSaveEscapeKeepWaiting]);

        if (!drop || _disposed)
        {
            return;
        }

        _inlineEditorEscapeAttempted = true;
        await DisconnectBrowserAsync(browser, CancellationToken.None);

        // The FLAG is the observable, never the disconnect task. On a stalled send the
        // drop can block on the same lock the write holds, so its task may never
        // complete - awaiting it would leave the escape silently unreported, which is
        // the very shape this whole item exists to end.
        await WaitForSaveToSettleAsync();

        ShowEditorNotice(EditorSaveEscapeOutcome.ReportKey(_inlineEditorSaveInProgress));
    }

    /// <summary>
    /// Waits, within a bound, for the save flag to clear after the connection was dropped.
    /// </summary>
    /// <remarks>
    /// The bound produces a sentence and nothing else. It shortens no transfer and
    /// introduces no operation timeout - it decides only which of two true things the
    /// user is told.
    /// </remarks>
    private async Task WaitForSaveToSettleAsync()
    {
        DateTime deadline = DateTime.UtcNow + SaveEscapeSettleBound;
        while (_inlineEditorSaveInProgress && DateTime.UtcNow < deadline && !_disposed)
        {
            await Task.Delay(SaveEscapeSettlePollInterval);
        }
    }

    /// <summary>Writes a notice into the editor overlay's own status bar.</summary>
    private void ShowEditorNotice(string key)
    {
        LocalizationManager? localizer = _localizer;
        if (localizer is null
            || _activeInlineEditor?.DataContext is not EmbeddedEditorViewModel editorViewModel)
        {
            return;
        }

        editorViewModel.NoticeText = localizer.Format(key, DescribeClosePane());
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

        // The escape is offered once per save. The flag was never reset, so the second save
        // that stalled in a pane's life read as "already escaped" and got the stuck report
        // instead of the offer.
        if (inProgress)
        {
            _inlineEditorEscapeAttempted = false;
        }
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

        // Alt+key arrives as Key.System with the real key in SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        FileBrowserShortcut shortcut = FileBrowserShortcutPolicy.Resolve(
            key,
            Keyboard.Modifiers,
            _activeInlineEditor is not null,
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase,
            FileListView.IsKeyboardFocusWithin);

        if (shortcut != FileBrowserShortcut.None && ExecuteShortcut(shortcut))
        {
            e.Handled = true;
        }
    }

    /// <returns>Whether the shortcut was acted on, so a key is consumed only when it did something.</returns>
    private bool ExecuteShortcut(FileBrowserShortcut shortcut)
    {
        switch (shortcut)
        {
            case FileBrowserShortcut.Refresh:
                _ = _viewModel.Refresh();
                return true;

            case FileBrowserShortcut.FocusFilter:
                FilterTextBox.Focus();
                FilterTextBox.SelectAll();
                return true;

            case FileBrowserShortcut.FocusPath:
                PathTextBox.Focus();
                PathTextBox.SelectAll();
                return true;

            case FileBrowserShortcut.NewFolder:
                _viewModel.CreateFolderCommand.Execute(null);
                return true;

            case FileBrowserShortcut.Download:
                _ = StartDownloadAsync();
                return true;

            case FileBrowserShortcut.Upload:
                OnUploadClick(this, new RoutedEventArgs());
                return true;

            case FileBrowserShortcut.NavigateBack:
                _ = _viewModel.NavigateBack();
                return true;

            case FileBrowserShortcut.CancelLoad:
                if (!_viewModel.IsLoading)
                {
                    return false;
                }

                _viewModel.CancelLoad();
                return true;

            case FileBrowserShortcut.Rename:
                _viewModel.RenameSelectedCommand.Execute(null);
                return true;

            case FileBrowserShortcut.Delete:
                _viewModel.DeleteSelectedCommand.Execute(null);
                return true;

            case FileBrowserShortcut.Open:
                if (FileListView.SelectedItem is SftpFileInfo enterFile)
                {
                    OpenEntry(enterFile);
                }

                return true;

            case FileBrowserShortcut.ParentFolder:
                _ = _viewModel.NavigateUp();
                return true;

            case FileBrowserShortcut.Cut:
                _viewModel.CutSelectedCommand.Execute(null);
                return true;

            case FileBrowserShortcut.Copy:
                _viewModel.CopySelectedCommand.Execute(null);
                return true;

            case FileBrowserShortcut.Paste:
                PasteFromAnyClipboard();
                return true;

            case FileBrowserShortcut.CopyPath:
                OnCtxCopyPathClick(this, new RoutedEventArgs());
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Pastes what the user last cut or copied: the remote clipboard when it holds entries, otherwise
    /// the files Explorer put on the Windows clipboard.
    /// </summary>
    private void PasteFromAnyClipboard()
    {
        if (_viewModel.HasClipboard && _viewModel.IsConnected)
        {
            _viewModel.PasteCommand.Execute(null);
            return;
        }

        if (ClipboardHasFileDrop())
        {
            OnCtxPasteFromExplorerClick(this, new RoutedEventArgs());
        }
    }

    /// <summary>Opens an entry as a double-click does: a folder is entered, a file is opened in the editor.</summary>
    private void OpenEntry(SftpFileInfo entry)
    {
        if (!_viewModel.HandleFileDoubleClick(entry))
        {
            _ = EditFileCoreAsync(entry, openedImplicitly: true);
        }
    }

    /// <summary>A click on a folder of the breadcrumb goes there.</summary>
    private void OnPathSegmentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SftpPathSegment segment)
        {
            _ = NavigateRemoteAsync(segment.FullPath);
        }
    }

    /// <summary>A click on the empty part of the breadcrumb turns it into the editable path.</summary>
    private void OnPathBreadcrumbMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || !PathTextBox.IsEnabled)
        {
            return;
        }

        PathTextBox.Focus();
        PathTextBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>Keeps the deepest folder of the breadcrumb in view when the path is longer than the bar.</summary>
    private void OnPathBreadcrumbSizeChanged(object sender, SizeChangedEventArgs e)
        => PathBreadcrumbScroll.ScrollToRightEnd();

    /// <summary>Escape in the filter box clears it; a second Escape hands the keyboard back to the list.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (FilterTextBox.Text.Length > 0)
        {
            _viewModel.ClearFilter();
        }
        else
        {
            FileListView.Focus();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Enter in the path bar navigates and hands the keyboard to the list; Escape gives the bar back
    /// the folder actually shown, which a failed navigation also does.
    /// </summary>
    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.GoToPathCommand.Execute(null);
                FileListView.Focus();
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.RevertPathBar();
                FileListView.Focus();
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

        // The column says what it is: with the columns free to be reordered, its position said
        // nothing, and a click sorted the wrong column.
        string key = SftpColumn.GetKey(header.Column);
        if (key.Length == 0)
        {
            return;
        }

        _viewModel.ToggleSortColumn(key);
        UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        if (FileListView?.View is not GridView gridView)
        {
            return;
        }

        string arrow = _viewModel.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? SortArrowAscending
            : SortArrowDescending;

        foreach (GridViewColumn column in gridView.Columns)
        {
            string key = SftpColumn.GetKey(column);
            if (key.Length == 0)
            {
                continue;
            }

            string baseName = L($"SftpCol{key}");
            column.Header = string.Equals(key, _viewModel.SortColumn, StringComparison.Ordinal)
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
        // Only a row opens. The handler sits on the whole list, and a double-click on a column header,
        // on its resize grip or on a scrollbar used to open whatever was still selected.
        if (HitTestFileRow(e.GetPosition(FileListView)) is not { } file)
        {
            return;
        }

        OpenEntry(file);
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

        GridViewColumn? nameColumn = gv.Columns.FirstOrDefault(
            column => string.Equals(SftpColumn.GetKey(column), EmbeddedSftpViewModel.SortColumnName, StringComparison.Ordinal));
        if (nameColumn is null)
        {
            return;
        }

        double fixedWidth = gv.Columns
            .Where(column => !ReferenceEquals(column, nameColumn))
            .Sum(column => column.ActualWidth);

        double available = FileListView.ActualWidth
            - fixedWidth
            - SystemParameters.VerticalScrollBarWidth
            - FileListWidthPadding;

        if (available > MinimumNameColumnWidth)
        {
            nameColumn.Width = available;
        }
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < SftpViewMetrics.ToolbarCompactThresholdPx;
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
        BtnDownload.Visibility = inlineActions;
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
            UpdateStatus(L("SftpBookmarkEmpty"));
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
                Icon = CreateBookmarkIcon()
            };
            string capturedPath = path;
            item.Click += (_, _) => _ = NavigateRemoteAsync(capturedPath);
            menu.Items.Add(item);
        }

        // A bookmark can be removed: the menu used to promise a management it did not offer.
        menu.Items.Add(new Separator());
        var removeMenu = new MenuItem { Header = L("SftpBookmarkRemoveMenu") };
        foreach (string path in _viewModel.Bookmarks)
        {
            var removeItem = new MenuItem
            {
                Header = path,
                Icon = CreateBookmarkIcon()
            };
            string capturedPath = path;
            removeItem.Click += (_, _) => _viewModel.RemoveBookmark(capturedPath);
            removeMenu.Items.Add(removeItem);
        }

        menu.Items.Add(removeMenu);
        menu.IsOpen = true;
    }

    private static TextBlock CreateBookmarkIcon() => new()
    {
        FontFamily = new System.Windows.Media.FontFamily(SftpViewMetrics.IconFontFamilyName),
        Text = SftpViewMetrics.FolderGlyph,
        FontSize = SftpViewMetrics.MenuIconFontSize
    };

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
                Title = L("SftpBtnUpload")
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

    private void OnDownloadClick(object sender, RoutedEventArgs e) => _ = StartDownloadAsync();

    private void OnCtxDownloadClick(object sender, RoutedEventArgs e) => _ = StartDownloadAsync();

    /// <summary>
    /// Asks for a destination folder and downloads the selection into it. A transfer already running
    /// is no obstacle: the batch is queued behind it.
    /// </summary>
    private async Task StartDownloadAsync()
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

        if (selected.Count == 0
            || _browser is null
            || !EmbeddedSftpViewModel.CanDownloadSelection(selected))
        {
            return;
        }

        string? targetDirectory = PickDownloadFolder();
        if (targetDirectory is null)
        {
            return;
        }

        _viewModel.RememberDownloadFolder(targetDirectory);
        await _viewModel.DownloadFilesAsync(selected, targetDirectory);
    }

    /// <summary>
    /// Shows the system folder picker, opened on the folder the last download went to. It replaced a
    /// legacy picker that was drawn in the old style and forgot its place every time.
    /// </summary>
    private string? PickDownloadFolder()
    {
        try
        {
            OpenFolderDialog dialog = new()
            {
                Title = L("SftpBtnDownload"),
                Multiselect = false
            };
            if (_viewModel.RememberedDownloadFolder is { } lastFolder)
            {
                dialog.InitialDirectory = lastFolder;
            }

            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] download folder dialog failed: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Context menu actions
    // ------------------------------------------------------------------

    private void OnFileListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => ListViewContextMenuHelper.SelectRowOnRightClick(sender, e);

    /// <summary>
    /// No menu for a session that is gone: every entry of it used to answer "Transfer failed".
    /// </summary>
    private void OnFileContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_disposed || _browser is null || !_browser.IsConnected)
        {
            e.Handled = true;
        }
    }

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
        // Enabled only when the selection holds something the download will actually move, using the
        // same predicate the planner walks with: a selection of links and pipes alone would open a
        // folder picker and then download nothing. Shown but grey rather than hidden, so the action
        // stays discoverable.
        bool canDownload = EmbeddedSftpViewModel.CanDownloadSelection(FileListView.SelectedItems.OfType<SftpFileInfo>());
        CtxDownload.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        CtxDownload.IsEnabled = canDownload;
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

        // Always visible: what applies to the folder rather than to a selection.
        CtxNewFolder.Visibility = Visibility.Visible;
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
                _ = EditFileCoreAsync(file, openedImplicitly: true);
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
            UpdateStatus(LF("SftpStatusEditUnsupportedEntry", file.Name));
            return;
        }

        if (_disposed || _editor is null)
        {
            return;
        }

        // The same refusal the local browser applies: a shell is not an editor, and a file path
        // handed to one as an argument is a command line.
        if (_externalEditorRejectionKey is { } rejectionKey)
        {
            ShowError(L(rejectionKey));
            return;
        }

        try
        {
            UpdateStatus(LF("SftpStatusEditing", file.Name));

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
        catch (ExternalEditorLaunchException ex)
        {
            ShowError(LF("SftpErrorExternalEditorLaunchFailed", ex.EditorPath));
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
            UpdateStatus(LF("SftpStatusPathCopied", file.FullPath));
        }
    }

    /// <summary>The explicit "Edit" command: opens the file in the integrated editor, whatever it holds.</summary>
    private Task EditFileAsync(SftpFileInfo file) => EditFileCoreAsync(file, openedImplicitly: false);

    /// <param name="file">The file to open in the integrated editor.</param>
    /// <param name="openedImplicitly">
    /// Whether the file is opened by a double-click or Enter rather than by the explicit "Edit"
    /// command. Only the implicit open checks for binary content and offers a download instead.
    /// </param>
    private async Task EditFileCoreAsync(SftpFileInfo file, bool openedImplicitly)
    {
        if (!file.IsRegularFile)
        {
            UpdateStatus(LF("SftpStatusEditUnsupportedEntry", file.Name));
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
            UpdateStatus(LF("SftpStatusEditing", file.Name));

            // Download file content for embedded editing
            tempPath = EditorTempPaths.CreateWorkingDirectory();
            _activeEditTempDirs.Add(tempPath);
            string localPath = Path.Combine(tempPath, Path.GetFileName(file.Name));

            bool useSudo = false;
            int downloadExceededSizeLimit = 0;
            using CancellationTokenSource downloadCancellation = new();

            // The browser reports the file name it derives from the remote path; compare against
            // the same derivation rather than the entry's listed name.
            string progressFileName = Path.GetFileName(file.FullPath);

            void EnforceInlineEditDownloadLimit(SftpTransferProgress progress)
            {
                if (!progress.IsUpload
                    && string.Equals(progress.FileName, progressFileName, StringComparison.Ordinal)
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

                if (openedImplicitly && BinaryFileSniffer.LooksBinary(localPath))
                {
                    CleanupEditTempDir(tempPath);
                    await OfferDownloadForBinaryFileAsync(file);
                    return;
                }

                RemoteTextDocument document = await RemoteTextFileCodec.ReadAsync(localPath);
                string content = document.Text;
                string remotePath = file.FullPath;
                string? encodingNotice = document.DecodedWithFallback
                    ? L("EditorEncodingFallbackNotice")
                    : null;

                // Open in embedded AvalonEdit editor
                EmbeddedEditorView editorView = new(_localizer);

                // Assigned before the overlay is mounted, and before anything can be typed into it.
                // A guard attached to no host is a guard that never runs - that is exactly how the
                // pane's own close guard sat inert for weeks after it shipped.
                editorView.CloseRefusedByHost = RefuseInlineEditorCloseWhileSaving;
                editorView.OpenContent(file.Name, content);
                if (encodingNotice is not null)
                {
                    editorView.ShowNotice(encodingNotice);
                }

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

                            UpdateStatus(LF("SftpStatusAutoUploaded", file.Name));
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

                                UpdateStatus(LF("SftpStatusUploadedViaSudo", file.Name));
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
            ShowError(ex is SudoEditFileTooLargeException or EditorWorkingDirectoryUnprotectedException
                ? _viewModel.DescribeTransferError(ex)
                : LF("SftpStatusEditOpenFailed", ex.Message));
            if (tempPath is not null)
            {
                CleanupEditTempDir(tempPath);
            }
        }
    }

    /// <summary>
    /// Offers to download a file that looks binary instead of opening it in a text editor, where it
    /// would show as garbage and a save would corrupt it.
    /// </summary>
    private async Task OfferDownloadForBinaryFileAsync(SftpFileInfo file)
    {
        IDialogService? dialogService = _dialogService;
        if (dialogService is null || _disposed)
        {
            return;
        }

        bool download = await dialogService.ShowConfirmAsync(
            L("SftpBinaryFileTitle"),
            LF("SftpBinaryFileMessage", file.Name),
            CloseGuardConfirmSeverity,
            L("SftpBtnDownload"),
            L("BtnCancel"));

        if (!download || _disposed)
        {
            return;
        }

        string? targetDirectory = PickDownloadFolder();
        if (targetDirectory is null)
        {
            return;
        }

        _viewModel.RememberDownloadFolder(targetDirectory);
        await _viewModel.DownloadFilesAsync([file], targetDirectory);
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
        if (_disposed || _browser is null || !_browser.IsConnected || _activeInlineEditor is not null)
        {
            RefuseDrag(e);
            return;
        }

        AutoScrollDuringDrag(e);
        SftpFileInfo? hovered = HitTestFileRow(e.GetPosition(FileListView));

        // A drag started in this list: a folder row of the same list receives a move.
        if (SftpRemoteDragPayload.From(e.Data) is { } remote)
        {
            bool canMove = ReferenceEquals(remote.Source, _viewModel)
                && hovered is { IsDirectory: true } folder
                && remote.Entries.Any(entry => EmbeddedSftpViewModel.CanMoveInto(entry, folder.FullPath));
            SetDropHighlight(canMove ? hovered : null);
            DragOverlay.Visibility = Visibility.Collapsed;
            e.Effects = canMove ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            RefuseDrag(e);
            return;
        }

        // Files from outside: the row under the pointer, when it is a folder, is the destination.
        // Say so before the button is released, with how many items go where.
        string targetDirectory = EmbeddedSftpViewModel.ResolveDropTargetDirectory(hovered, _viewModel.CurrentPath);
        SetDropHighlight(hovered is { IsDirectory: true } ? hovered : null);
        DragDropOverlayText.Text = DescribeExternalDrop(e.Data, targetDirectory);
        DragOverlay.Visibility = Visibility.Visible;
        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void RefuseDrag(System.Windows.DragEventArgs e)
    {
        SetDropHighlight(null);
        DragOverlay.Visibility = Visibility.Collapsed;
        e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e)
    {
        SetDropHighlight(null);
        DragOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The text of the drop overlay: how many items, and into which folder. The text is rebuilt only
    /// when the dragged data or the destination changes, since drag-over fires continuously.
    /// </summary>
    private string DescribeExternalDrop(System.Windows.IDataObject data, string targetDirectory)
    {
        string composedKey = string.Concat(targetDirectory, "|", _viewModel.IsTransferInProgress);
        if (ReferenceEquals(_dragOverlayDataKey, data) && string.Equals(_dragOverlayText, composedKey, StringComparison.Ordinal))
        {
            return DragDropOverlayText.Text;
        }

        int count = (data.GetData(System.Windows.DataFormats.FileDrop) as string[])?.Length ?? 0;
        string items = LFC(count, "SftpTransferJobItemsOne", "SftpTransferJobItems");
        string text = LF(
            _viewModel.IsTransferInProgress ? "SftpDropOverlayQueued" : "SftpDropOverlayTarget",
            items,
            targetDirectory);
        _dragOverlayDataKey = data;
        _dragOverlayText = composedKey;
        return text;
    }

    /// <summary>Scrolls the list while a drag hovers near its top or bottom edge, so a far row can be reached.</summary>
    private void AutoScrollDuringDrag(System.Windows.DragEventArgs e)
    {
        ScrollViewer? scroller = FindListScrollViewer();
        if (scroller is null)
        {
            return;
        }

        double y = e.GetPosition(FileListView).Y;
        if (y < DragAutoScrollEdgePx)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - DragAutoScrollStepPx);
        }
        else if (y > FileListView.ActualHeight - DragAutoScrollEdgePx)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + DragAutoScrollStepPx);
        }
    }

    private void SetDropHighlight(SftpFileInfo? folder)
    {
        System.Windows.Controls.ListViewItem? container = folder is null
            ? null
            : FileListView.ItemContainerGenerator.ContainerFromItem(folder) as System.Windows.Controls.ListViewItem;
        if (ReferenceEquals(container, _highlightedDropRow))
        {
            return;
        }

        if (_highlightedDropRow is not null)
        {
            SftpDropTarget.SetIsDropTarget(_highlightedDropRow, false);
        }

        _highlightedDropRow = container;
        if (container is not null)
        {
            SftpDropTarget.SetIsDropTarget(container, true);
        }
    }

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        DragOverlay.Visibility = Visibility.Collapsed;
        SetDropHighlight(null);

        if (_disposed || _browser is null || !_browser.IsConnected || _activeInlineEditor is not null)
        {
            return;
        }

        string[] paths;
        string targetDir;
        SftpRemoteDragPayload? remote;

        try
        {
            // Impure hit-test: find the row under the cursor (if any); the pure helper turns it into the
            // target directory. Dropping onto a folder row sends into it; anywhere else uses CurrentPath.
            SftpFileInfo? hoveredEntry = HitTestFileRow(e.GetPosition(FileListView));
            targetDir = EmbeddedSftpViewModel.ResolveDropTargetDirectory(
                hoveredEntry, _viewModel.CurrentPath);

            remote = SftpRemoteDragPayload.From(e.Data);
            if (remote is not null)
            {
                paths = [];
            }
            else
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
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[EmbeddedSftpView] drop payload preparation failed: {ex.Message}");
            return;
        }

        if (remote is not null)
        {
            // A move inside this list, onto a folder row. Anything else is not a drop target here.
            if (ReferenceEquals(remote.Source, _viewModel)
                && HitTestFileRow(e.GetPosition(FileListView)) is { IsDirectory: true })
            {
                await _viewModel.MoveEntriesAsync(remote.Entries, targetDir);
            }

            return;
        }

        await _viewModel.UploadEntriesAsync(paths, targetDir);
    }

    // Maps a point in FileListView coordinates to the SftpFileInfo of the row beneath it, or null when
    // the point lands on empty space, a header or a scrollbar. Walks the visual tree up to the row container.
    private SftpFileInfo? HitTestFileRow(System.Windows.Point point)
    {
        if (FileListView.InputHitTest(point) is not DependencyObject hit)
        {
            return null;
        }

        DependencyObject? container = ItemsControl.ContainerFromElement(FileListView, hit);
        return (container as System.Windows.Controls.ListViewItem)?.DataContext as SftpFileInfo;
    }

    private ScrollViewer? FindListScrollViewer()
    {
        if (_listScrollViewer is not null)
        {
            return _listScrollViewer;
        }

        _listScrollViewer = FindDescendant<ScrollViewer>(FileListView);
        return _listScrollViewer;
    }

    private ScrollViewer? _listScrollViewer;

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Drag out of the list
    // ------------------------------------------------------------------

    private void OnFileListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragSource?.OnPreviewMouseLeftButtonDown(e);

    private void OnFileListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragSource?.OnPreviewMouseLeftButtonUp(e);

    private void OnFileListPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => _dragSource?.OnPreviewMouseMove(e);

    /// <summary>Runs the drag of the selected rows; what it carries stays inside the application.</summary>
    private void StartRemoteDrag(IReadOnlyList<object> rows)
    {
        List<SftpFileInfo> entries = rows.OfType<SftpFileInfo>().ToList();
        if (entries.Count == 0)
        {
            return;
        }

        System.Windows.DataObject data = new();
        data.SetData(SftpRemoteDragPayload.FormatName, new SftpRemoteDragPayload(_viewModel, entries));

        try
        {
            System.Windows.DragDrop.DoDragDrop(
                FileListView,
                data,
                System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Copy);
        }
        finally
        {
            SetDropHighlight(null);
            DragOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // Listing replacement: keep the scroll position and the selection
    // ------------------------------------------------------------------

    private void OnFilesReplacing()
    {
        _savedScrollOffset = FindListScrollViewer()?.VerticalOffset;
    }

    private void OnSelectionRestoreRequested(IReadOnlyList<SftpFileInfo> restored)
    {
        // Selected again by path, and brought into view: the entry just created or renamed, or the
        // selection a refresh would otherwise have emptied.
        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (_disposed)
                {
                    return;
                }

                FileListView.SelectedItems.Clear();
                foreach (SftpFileInfo entry in restored)
                {
                    FileListView.SelectedItems.Add(entry);
                }

                FileListView.ScrollIntoView(restored[0]);
                _savedScrollOffset = null;
            }));
    }

    // ------------------------------------------------------------------
    // Transfer progress
    // ------------------------------------------------------------------

    private void OnTransferProgress(SftpTransferProgress progress)
    {
        // Called from the transfer thread for every buffer moved. Only the newest event is kept; a
        // timer on the UI thread shows it a few times a second instead of queueing one dispatcher
        // call per event, which could fall behind a fast link and freeze the window.
        _progressCoalescer.Post(progress);
        RequestProgressTimer();
    }

    private void RequestProgressTimer()
    {
        if (Interlocked.Exchange(ref _progressTimerRequested, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(StartProgressTimer));
    }

    private void StartProgressTimer()
    {
        if (_disposed)
        {
            return;
        }

        _progressTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(SftpViewMetrics.ProgressRefreshMilliseconds),
            System.Windows.Threading.DispatcherPriority.Background,
            OnProgressTimerTick,
            Dispatcher);
        _progressTimer.Start();
        OnProgressTimerTick(this, EventArgs.Empty);
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        SftpTransferProgress? progress = _progressCoalescer.Take();
        if (progress is not null)
        {
            if (!_disposed)
            {
                _viewModel.UpdateTransferProgress(progress);
            }

            return;
        }

        // Nothing arrived since the last tick: sleep until the next event wakes the timer. An event
        // posted between the take and the reset is caught by the check that follows it.
        _progressTimer?.Stop();
        Interlocked.Exchange(ref _progressTimerRequested, 0);
        if (_progressCoalescer.HasPending)
        {
            RequestProgressTimer();
        }
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
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

        // Cutting the connection under a running transfer loses the files not yet moved: ask first,
        // as the close guard does for the pane.
        if (!await _viewModel.ConfirmDisconnectAsync())
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
            UpdateStatus(L("SftpStatusDisconnected"));
        }
    }

    private void OnReconnectClick(object sender, RoutedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        Core.Logging.FileLogger.Info("EmbeddedSFTP reconnect requested by user");
        UpdateStatus(L("SftpStatusReconnecting"));
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

    private void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (!_disposed)
        {
            EditProfileRequested?.Invoke();
        }
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
                ? (L("SftpStatusDisconnected"))
                : (LF("SftpErrorSessionDied", errorMessage));

            if (_pendingBrowserSecurityStatus is { } securityStatus)
            {
                _pendingBrowserSecurityStatus = null;
                ShowError(securityStatus);
            }
            else if (string.IsNullOrWhiteSpace(errorMessage))
            {
                UpdateStatus(status);
            }
            else
            {
                // A session that died on an error is an error: shown with the icon and the colour
                // of one, not as a neutral status that reads like "Ready".
                ShowError(status);
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
            HealthCheckInterval,
            HealthCheckInterval);
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
                    ShowError(L("SftpStatusHealthCheckFailed"));
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
                UpdateStatus(LF("SftpStatusAutoUploaded", fileName));
            }
            else
            {
                if (_editor?.GetActiveEdits().Contains(remotePath, StringComparer.Ordinal) != true)
                {
                    return;
                }

                // One argument, not two. The second used to be the literal "upload error",
                // which reached a French user untranslated for the one reason no reason is
                // available here: this callback is told that the upload failed, not why.
                ShowError(LF("SftpStatusAutoUploadFailed", fileName));
            }
        });
    }

    /// <summary>
    /// Shows, once, why the server refused an auto-upload that retrying cannot change. The editor
    /// no longer re-attempts it on a timer, so this is the only report until the next save.
    /// </summary>
    private void OnEditorFileUploadRefused(string remotePath, Exception refusal)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed
                || _editor?.GetActiveEdits().Contains(remotePath, StringComparer.Ordinal) != true)
            {
                return;
            }

            ShowError(LF(
                "SftpStatusAutoUploadRefused",
                Path.GetFileName(remotePath),
                _viewModel.DescribeTransferError(refusal)));
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

            ShowError(LF(
                "SftpHostKeyRotatedDuringUpload",
                evt.RemotePath,
                evt.Host,
                evt.Port,
                evt.PresentedFingerprint));
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
        // The two fingerprints keep their own fallbacks: those are data, not prose, and a
        // security warning that says "changed to nothing" would be worse than one that says
        // "changed to ?".
        return LF(
            "SftpHostKeyMismatchMidSession",
            evt.Host,
            evt.Port,
            evt.PresentedFingerprint ?? "?",
            evt.StoredFingerprint ?? "?");
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    /// <summary>Resolves a locale key, degrading to the key itself when no localizer is set.</summary>
    /// <remarks>
    /// <para>The same helper the SSH view already uses. It replaces a dozen
    /// <c>_localizer?[key] ?? "English"</c> arms whose English half was never shown -
    /// production always has a localizer - but which were, literally, user-facing text
    /// written in the code, and which drifted from the catalogue by construction: nothing
    /// updated them when a message was reworded.</para>
    /// <para><b>It was written as a call to itself when it was introduced on 2026-09-07</b>, which
    /// is recursion with no way out. Every status message this view produced overflowed the stack
    /// and killed the process where it stood: no exception, no log line, and no dump on a machine
    /// whose error reporting is disabled by policy. It reached users in every release from
    /// v2026.090701 to v2026.091402, and a burst of dropped transports - which makes this view
    /// report a disconnection - is what triggered it. Named by the stack of a crash dump, from
    /// three values repeated 32,040 times.</para>
    /// <para>The rewrite that introduced it carried its own guard, and that guard passed: it
    /// asserted the English halves had been removed, and nothing asserted that what replaced them
    /// worked. <c>UnconditionalSelfRecursionGuardTests</c> now sweeps the tree for the shape.</para>
    /// </remarks>
    private string L(string key) => _localizer?[key] ?? key;

    /// <summary>As <see cref="L"/>, with the localizer applying the arguments.</summary>
    /// <remarks>
    /// Through the localizer's own <c>Format</c>, never <c>string.Format</c>: that one
    /// swallows a malformed template and returns it, where <c>string.Format</c> throws in
    /// front of the user, on the line meant to tell them what went wrong.
    /// </remarks>
    private string LF(string key, params object[] args) => _localizer?.Format(key, args) ?? key;

    /// <summary>As <see cref="LF"/> for a counted message: the localizer picks the form for the number.</summary>
    private string LFC(long count, string oneKey, string otherKey, params object[] args)
        => _localizer?.FormatCount(count, oneKey, otherKey, args) ?? otherKey;

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
        ShowError(LF("SftpStatusEditFileTooLarge", fileName));
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

    /// <summary>
    /// The persistent transport-security notice this browser's transport requires, or null when
    /// the transport carries no disclosed limitation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SFTP-001. The FTPS data channel is encrypted but its certificate is not authenticated,
    /// and that cannot be changed from here: the library accepts any certificate on a data
    /// connection from inside itself, and the only TLS callback this application installs is
    /// never consulted for one. The audit record carries the measurement and the reasoning.
    /// </para>
    /// <para>
    /// Since the limitation cannot be fixed, the disclosure IS the remedy, which makes its
    /// wiring load bearing rather than cosmetic. It used to live inside an else-if arm two
    /// braces deep, where the statement predicate could not reach it and deleting it turned
    /// nothing red. Written as body-level statements on purpose: a ternary return would put
    /// the argument back out of reach of the exact-argument anchor that guards it.
    /// </para>
    /// </remarks>
    internal static string? TransportSecurityNoticeKey(IRemoteBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        if (browser is not FtpBrowser ftpBrowser)
        {
            return null;
        }

        return GetFtpSecurityNoticeLocalizationKey(ftpBrowser.IsTlsEnabled);
    }

    /// <summary>
    /// Raises the transport-security notice for a session, when its transport has one.
    /// </summary>
    /// <remarks>
    /// Called for every session rather than for FTP sessions only, so that the decision about
    /// which transports disclose something lives in one named place instead of in the shape of
    /// a branch at the call site. SFTP and SCP answer null and nothing is raised.
    /// </remarks>
    internal static void ApplyTransportSecurityNotice(
        EmbeddedSftpViewModel viewModel,
        IRemoteBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(browser);

        string? key = TransportSecurityNoticeKey(browser);
        if (key is null)
        {
            return;
        }

        viewModel.ShowSecurityNoticeKey(key);
    }

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
