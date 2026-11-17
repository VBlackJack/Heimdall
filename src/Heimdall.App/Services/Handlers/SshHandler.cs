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
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Pageant;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles SSH connection logic.
/// </summary>
internal sealed class SshHandler : IProtocolHandler, IDisposable
{
    /// <summary>
    /// Bound on each authentication wait of the embedded client, long enough for a person to
    /// answer a keyboard-interactive question. Reaching the server keeps the normal connect
    /// timeout (see <see cref="SshConnectionParams.AuthenticationTimeout"/>).
    /// </summary>
    private static readonly TimeSpan InteractiveAuthenticationTimeout = TimeSpan.FromMinutes(2);
    /// <summary>
    /// Opens the embedded shell session. Replaced in tests so the refusal
    /// branches below can be reached without a live SSH server: everything the
    /// handler does with the exception - classification, localization, the
    /// agent decision and the composed message - stays production code.
    /// </summary>
    internal delegate Task ConnectShellSession(
        SshShellSession session,
        SshConnectionParams connectionParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        int terminalColumns,
        int terminalRows,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts the Plink pipe-mode process. Replaced in tests so the launch, its arguments and
    /// the terminal size it carries can be observed without starting a process.
    /// </summary>
    internal delegate Task StartPipeModeSession(
        Heimdall.Terminal.PipeModeSession session,
        string executable,
        string arguments,
        int columns,
        int rows,
        CancellationToken cancellationToken);

    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyTrustService _hostKeyTrustService;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IX11ServerManager _x11ServerManager;
    private readonly IDialogService _dialogService;
    private readonly IPlinkHostKeyProbe _plinkHostKeyProbe;
    private readonly PlinkPasswordFileJanitor _plinkPasswordFileJanitor;
    private readonly SensitiveFileJanitorScheduler _plinkPasswordFileJanitorScheduler;
    private readonly Action<string?> _deletePlinkPasswordFile;
    private readonly Func<string?, PlinkAttestationLease> _plinkAttestation;
    private readonly Func<SshAgentPreference, SshAgentRegistry> _agentRegistryFactory;
    private readonly ConnectShellSession _connectShellSession;
    private readonly StartPipeModeSession _startPipeModeSession;
    private readonly IPuttySessionRegistry _puttySessionRegistry;
    private readonly Action<string, ISshAgentKey> _writeAgentKeyFile;

    internal Action<string>? SetStatusText { get; set; }

    /// <summary>
    /// Resolves the terminal size the view reported for a session id, waiting at most the given
    /// time for its first report, or says why there is none. Wired by the shell. Consulted just
    /// before the PTY is created, because the page's <c>ready:</c> size usually arrives while the
    /// connection is still being negotiated.
    /// </summary>
    internal Func<string, TimeSpan, CancellationToken, Task<TerminalSizeLookup>>? ResolveInitialTerminalSize { get; set; }

    /// <summary>
    /// Called with the server id the moment the embedded client's authentication failure is about
    /// to be retried through Plink, before the terminal size is asked for. Wired by the shell so
    /// it can drop the pending file panel, which could never connect in that case. Invoked
    /// synchronously: the subscriber applies its layout change on the UI thread through a
    /// synchronous dispatcher call, so the connect is held until the UI has applied it. It may
    /// block briefly on the UI thread and must not be given long work. An exception from it is
    /// logged and ignored.
    /// </summary>
    internal Action<string>? PlinkRetryStarting { get; set; }

    /// <summary>
    /// Receives the Info lines of the initial size resolution: the size used and, when it is the
    /// default, why. Replaced in tests so the fallback line can be observed.
    /// </summary>
    internal Action<string> TerminalSizeLog { get; set; } = Core.Logging.FileLogger.Info;

    public SshHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        HostKeyStore hostKeyStore,
        IHostKeyTrustService hostKeyTrustService,
        IHostKeyVerifier hostKeyVerifier,
        IX11ServerManager x11ServerManager,
        IDialogService dialogService,
        IPlinkHostKeyProbe? plinkHostKeyProbe = null,
        PlinkPasswordFileJanitor? plinkPasswordFileJanitor = null,
        Action<string?>? deletePlinkPasswordFile = null,
        Func<string?, PlinkAttestationLease>? plinkAttestation = null,
        Func<SshAgentPreference, SshAgentRegistry>? agentRegistryFactory = null,
        ConnectShellSession? connectShellSession = null,
        StartPipeModeSession? startPipeModeSession = null,
        IPuttySessionRegistry? puttySessionRegistry = null,
        Action<string, ISshAgentKey>? writeAgentKeyFile = null)
    {
        _writeAgentKeyFile = writeAgentKeyFile ?? PlinkAgentKeyFile.Write;
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _hostKeyStore = hostKeyStore;
        _hostKeyTrustService = hostKeyTrustService;
        _hostKeyVerifier = hostKeyVerifier;
        _x11ServerManager = x11ServerManager;
        _dialogService = dialogService;
        _plinkHostKeyProbe = plinkHostKeyProbe ?? new DefaultPlinkHostKeyProbe();
        _plinkPasswordFileJanitor = plinkPasswordFileJanitor ?? new PlinkPasswordFileJanitor();
        _deletePlinkPasswordFile = deletePlinkPasswordFile ?? DeletePlinkPasswordFile;
        _plinkAttestation = plinkAttestation ?? PlinkCompatibilityAttestation.Acquire;
        _agentRegistryFactory = agentRegistryFactory ?? SshAgentRegistry.CreateDefault;
        _connectShellSession = connectShellSession ?? ConnectShellSessionAsync;
        _startPipeModeSession = startPipeModeSession ?? StartPipeModeSessionAsync;
        _plinkPasswordFileJanitorScheduler = new SensitiveFileJanitorScheduler(
            nameof(PlinkPasswordFileJanitor),
            _plinkPasswordFileJanitor.SweepStale);
        _plinkPasswordFileJanitorScheduler.Start();
        _puttySessionRegistry = puttySessionRegistry ?? new WindowsPuttySessionRegistry();

        // Synchronous and before any launch, so the sweep can never delete a size session this
        // process has just created for a Plink that has not read it yet.
        new PlinkSizeSessionJanitor(_puttySessionRegistry).SweepLeftovers();
    }

    public string Protocol => "SSH";

    public void Dispose()
    {
        _plinkPasswordFileJanitorScheduler.Dispose();
    }

    /// <summary>
    /// Establishes an SSH shell connection, optionally through a tunnel.
    /// Returns a connected <see cref="SshShellSession"/> on success.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        Core.Logging.FileLogger.Info(
            $"ConnectSshAsync: {server.DisplayName} ({server.RemoteServer}:{server.SshPort}) Gateway={server.SshGatewayId ?? "none"}");
        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        // Before the tunnel, the host key resolution and the dial: a session that names an agent
        // key nobody holds must be refused without asking the user to trust a host first.
        (ConnectionResult? agentKeyRefusal, SshAgentIdentityMatch? chosenAgentKey) =
            await PreflightChosenAgentKeyAsync(server, settings, ct).ConfigureAwait(false);
        if (agentKeyRefusal is not null)
        {
            return agentKeyRefusal;
        }

        int sshPort = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        TunnelSetupOutcome tunnelOutcome = await _tunnelService
            .SetupTunnelIfNeededAsync(server, sshPort, settings, ct)
            .ConfigureAwait(false);
        (bool tunnelOk, bool usesTunnel, string targetHost, int targetPort, string? tunnelError) = tunnelOutcome;
        long tunnelLeaseId = tunnelOutcome.TunnelLeaseId;

        if (!tunnelOk)
        {
            return new ConnectionResult(
                false,
                tunnelError,
                null,
                SshSessionDiagnosticFactory.CreateGatewayFailure(
                    tunnelError,
                    code: tunnelOutcome.FailureCode));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingSsh);

        var sshMode = server.SshMode ?? "Embedded";
        Core.Logging.FileLogger.Info($"SSH mode: {sshMode}");

        if (string.Equals(sshMode, "External", StringComparison.OrdinalIgnoreCase))
        {
            return await ConnectSshExternal(server, settings, targetHost, targetPort, usesTunnel, tunnelLeaseId, ct)
                .ConfigureAwait(false);
        }

        // Same refusal as the plink path: a relative or missing key path is a profile
        // error, not a dial failure. Without this the SSH.NET client factory resolved a
        // relative path against the working directory and the user saw an unrelated error.
        if (!TryValidateKeyPath(server.SshKeyPath, out SshKeyPathValidationError sshNetKeyPathError))
        {
            string keyPathMessage = LocalizeKeyPathError(sshNetKeyPathError, server.SshKeyPath);
            _connectionSm.SetError(server.Id, keyPathMessage);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
            return new ConnectionResult(
                false,
                keyPathMessage,
                null,
                SshSessionDiagnosticFactory.CreatePreflightFailure(SshLocalizationKeys.ErrorConnectionFailed, keyPathMessage));
        }

        using CancellationTokenSource promptLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SshConnectionParams sshParams = new()
        {
            Host = targetHost,
            Port = targetPort,
            LogicalHost = usesTunnel ? server.RemoteServer : null,
            LogicalPort = usesTunnel ? sshPort : null,
            Username = server.SshUsername ?? string.Empty,
            Password = ConnectionHelpers.DecryptPassword(server.SshPasswordEncrypted),
            KeyPassphrase = ConnectionHelpers.DecryptPassword(server.SshKeyPassphraseEncrypted),
            KeyPath = string.IsNullOrWhiteSpace(server.SshKeyPath) ? null : server.SshKeyPath,
            SshAgentPreference = settings.SshAgentPreference,
            AgentKeyFingerprint = server.SshAgentKeyFingerprint,
            UseLegacyPasswordAsKeyPassphrase = server.UsesLegacySshCredentialMapping,
            LegacyCredentialName = server.DisplayName,
            AgentForwarding = server.SshAgentForwarding,
            Compression = server.SshCompression,
            X11Forwarding = server.SshX11Forwarding,
            AuthenticationTimeout = InteractiveAuthenticationTimeout,
            KeyboardInteractiveResponder = request =>
            {
                // SSH.NET invokes this synchronous event on its own authentication worker.
                // WPF marshals the dialog to the dispatcher; the UI thread never waits here.
                promptLifetime.Token.ThrowIfCancellationRequested();

                // The request is server text inside a Heimdall dialog: sanitised so it cannot
                // lay itself out as Heimdall's own words, and framed by the message as sent by
                // the named host.
                return _dialogService.ShowPasswordInputAsync(
                    _localizer[SshLocalizationKeys.InteractivePromptTitle],
                    string.Format(
                        _localizer[SshLocalizationKeys.InteractivePromptMessage],
                        server.RemoteServer,
                        server.SshUsername,
                        ServerPromptText.Sanitize(request)),
                    promptLifetime.Token).GetAwaiter().GetResult();
            }
        };

        var agentRegistry = _agentRegistryFactory(settings.SshAgentPreference);
        if (sshParams.AgentForwarding
            && !agentRegistry.HasPlinkCompatibleAgent()
            && agentRegistry.HasAnyNonPlinkAgent())
        {
            var message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
            _connectionSm.SetError(server.Id, message);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
            return new ConnectionResult(
                false,
                message,
                null,
                SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                    SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported,
                    message));
        }

        if (SshConnectionFactory.RequiresPlinkFallback(sshParams))
        {
            Core.Logging.FileLogger.Info(
                $"SSH using Plink fallback for {server.DisplayName}");

            return await ConnectSshViaPlinkAsync(
                    server,
                    settings,
                    targetHost,
                    targetPort,
                    usesTunnel,
                    originalFailure: null,
                    ct,
                    tunnelLeaseId: tunnelLeaseId)
                .ConfigureAwait(false);
        }

        // Past this point the connection goes through SSH.NET, which rejects an empty
        // user name from every authentication method: without this the attempt reached
        // the transport and came back as a raw ArgumentException, after the host-key
        // prompt had already asked the user to trust the server. Deliberately below the
        // Plink branch, which asks for a login name itself and whose own guard tolerates
        // a key-only profile without one.
        if (ConnectionHelpers.RequiresUsernameToConnect(server))
        {
            string usernameMsg = _localizer[SshLocalizationKeys.ErrorSshUsernameRequired];
            _connectionSm.SetError(server.Id, usernameMsg);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
            return new ConnectionResult(
                false,
                usernameMsg,
                null,
                SshSessionDiagnosticFactory.CreatePreflightFailure(
                    SshLocalizationKeys.ErrorSshUsernameRequired,
                    usernameMsg));
        }

        SshCapabilityNotice? capabilityNotice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            server.SshX11Forwarding,
            server.SshCompression);
        if (capabilityNotice is not null)
        {
            Core.Logging.FileLogger.Warn(
                $"SSH direct path cannot honor requested capabilities: {capabilityNotice.LogDescription}. Connection will continue without them.");
            SetStatusText?.Invoke(_localizer[capabilityNotice.StatusLocalizationKey]);
        }

        // Read before the dial: the too-many-failures message states what was offered, and
        // agents read after the refusal could hold keys loaded while it was in flight.
        int? agentKeysAtDial = null;
        try
        {
            // A chosen key found by the preflight is the only one offered, whatever else the
            // agents hold: the count the failure message states is that one key.
            agentKeysAtDial = chosenAgentKey is not null
                ? 1
                : agentRegistry.Observe().OfferableIdentityCount;
        }
        catch (Exception ex)
        {
            // The count only refines a failure message: the connection proceeds without it.
            Core.Logging.FileLogger.Debug("SSH agent key count unavailable at dial", ex);
        }

        var session = new SshShellSession();
        try
        {
            // Resolved here, not at entry: the page's ready: size usually lands while the
            // tunnel and the transport were being negotiated above. Never waited for on this
            // transport: the attach replays the page's size, and SSH.NET resizes after start.
            TerminalSize initialSize = await ResolveInitialTerminalSizeForAsync(server, TimeSpan.Zero, ct)
                .ConfigureAwait(false);
            await _connectShellSession(
                    session,
                    sshParams,
                    _hostKeyStore,
                    _hostKeyVerifier,
                    initialSize.Columns,
                    initialSize.Rows,
                    ct)
                .ConfigureAwait(false);
            if (session.Client is { } connectedClient)
            {
                connectedClient.KeepAliveInterval =
                    TimeSpan.FromSeconds(settings.SshKeepAliveIntervalSeconds);
            }
        }
        catch (HostKeyRejectedException ex)
        {
            session.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);

            if (ex.IsMismatch && !string.IsNullOrWhiteSpace(ex.StoredFingerprint))
            {
                string message = SshFailureMessageBuilder.HostKeyMismatch(
                    _localizer,
                    ex.StoredFingerprint,
                    ex.PresentedFingerprint);
                _connectionSm.SetError(server.Id, message);
                return new ConnectionResult(
                    false,
                    message,
                    null,
                    SshSessionDiagnosticFactory.CreateHostKeyMismatchFailure(
                        ex.StoredFingerprint,
                        ex.PresentedFingerprint,
                        ex.Host,
                        ex.Port));
            }

            string cancelledMessage = SshFailureMessageBuilder.HostKeyRejected(_localizer, ex.Host, ex.Port);
            _connectionSm.SetError(server.Id, cancelledMessage);
            return new ConnectionResult(
                false,
                cancelledMessage,
                null,
                SshSessionDiagnosticFactory.FromClassifiedFailure(
                    new SshFailureInfo(SshFailureCode.Cancelled, cancelledMessage, false, ex)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
            throw;
        }
        catch (Exception ex)
        {
            promptLifetime.Cancel();
            session.Dispose();
            var failure = FailureClassifier.Classify(ex, sshParams);
            SshFailureInfo localizedFailure = LocalizeFailure(failure, targetHost, agentKeysAtDial);

            // With a chosen agent key the retry goes through Plink only when Plink can honour that
            // key; otherwise the embedded client's own failure is the answer, unchanged. A code in
            // no retry set (the key is not loaded) never gets here.
            bool plinkMayRetry = !sshParams.KeyboardInteractive.HasInteractiveAnswer
                && SshPlinkRetryPolicy.AllowsPlinkRetry(failure.Code);
            if (plinkMayRetry && chosenAgentKey is not null)
            {
                try
                {
                    plinkMayRetry = await CanPlinkHonourChosenKeyAsync(server, settings, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Same ending as a cancelled dial above: the tunnel reference is given back
                    // and the cancellation travels on.
                    ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
                    throw;
                }
            }

            if (plinkMayRetry)
            {
                var fallbackAgentRegistry = _agentRegistryFactory(settings.SshAgentPreference);
                if (!fallbackAgentRegistry.HasPlinkCompatibleAgent() && fallbackAgentRegistry.HasAnyNonPlinkAgent())
                {
                    // Why no Plink retry was attempted. It is an observation
                    // about Heimdall and it names no cause for the refusal, so
                    // it goes after the server's own sentence and never over it.
                    // This branch used to return the notice alone: with the
                    // Windows OpenSSH Agent reachable and Pageant absent, a
                    // wrong password produced a screen that said nothing about
                    // the password.
                    var message = SshAuthFailureMessageComposer.AppendSentence(
                        localizedFailure.Message,
                        _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported],
                        SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported)
                        ?? localizedFailure.Message;
                    _connectionSm.SetError(server.Id, message);
                    ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
                    return new ConnectionResult(
                        false,
                        message,
                        null,
                        SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                            SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported,
                            message,
                            failure.Code));
                }

                Core.Logging.FileLogger.Info(
                    $"SSH.NET auth failed ({failure.Code}), falling back to Plink: {failure.Message}");
                SetStatusText?.Invoke(_localizer[SshLocalizationKeys.StatusSshRetryingViaPlink]);

                try
                {
                    PlinkRetryStarting?.Invoke(server.Id);
                }
                catch (Exception relayFailure)
                {
                    Core.Logging.FileLogger.Warn($"Plink retry notification failed: {relayFailure.GetType().Name}");
                }

                // Whatever the retry returns, the refusal it retried travels with it: a remembered
                // password the server just refused must be dropped even when Plink then starts.
                ConnectionResult fallback = await ConnectSshViaPlinkAsync(
                        server,
                        settings,
                        targetHost,
                        targetPort,
                        usesTunnel,
                        failure.Code,
                        ct,
                        localizedFailure.Message,
                        tunnelLeaseId: tunnelLeaseId)
                    .ConfigureAwait(false);
                return fallback with { FallbackFrom = failure.Code };
            }

            _connectionSm.SetError(server.Id, localizedFailure.Message);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
            return new ConnectionResult(
                false,
                localizedFailure.Message,
                null,
                SshSessionDiagnosticFactory.FromClassifiedFailure(localizedFailure, usesTunnel));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
        return new ConnectionResult(true, null, new SshSessionResult(session, server.SessionLoggingOverride));
    }

    /// <remarks>
    /// The body moved to <see cref="SshFailureLocalizer"/> so the SFTP handler can call
    /// the same thing. Kept here as a delegating wrapper: its two call sites and every
    /// existing test are then untouched by the move.
    /// </remarks>
    private SshFailureInfo LocalizeFailure(
        SshFailureInfo failure,
        string targetHost,
        int? agentKeyCountAtDial = null) =>
        SshFailureLocalizer.Localize(failure, _localizer, targetHost, agentKeyCountAtDial);

    private static Task ConnectShellSessionAsync(
        SshShellSession session,
        SshConnectionParams connectionParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        int terminalColumns,
        int terminalRows,
        CancellationToken cancellationToken) =>
        session.ConnectAsync(
            connectionParams,
            hostKeyStore: hostKeyStore,
            hostKeyVerifier: hostKeyVerifier,
            terminalColumns: terminalColumns,
            terminalRows: terminalRows,
            cancellationToken: cancellationToken);

    private static Task StartPipeModeSessionAsync(
        Heimdall.Terminal.PipeModeSession session,
        string executable,
        string arguments,
        int columns,
        int rows,
        CancellationToken cancellationToken) =>
        session.StartAsync(
            executable,
            arguments,
            columns,
            rows,
            cancellationToken: cancellationToken);

    /// <summary>
    /// The size to create the PTY at: the one the terminal page reported for this session, waited
    /// for at most <paramref name="wait"/>, or the default with the reason logged.
    /// </summary>
    /// <remarks>
    /// The page's <c>ready:</c> size used to arrive before the session was attached and was
    /// dropped, so the remote PTY stayed at 80x24 until the first window resize. On the Plink
    /// pipe path, Plink ignores the size it is launched with: the size reaches the remote PTY only
    /// through the temporary PuTTY saved session passed with <c>-load</c> (see
    /// <see cref="PlinkSizeSession"/>). Resizing after start stays impossible on that transport.
    /// <para>On a fresh tab the page loads in parallel with the connect, and the Plink launch
    /// reached this lookup before the page had spoken, every time. That path therefore waits for
    /// the first report, bounded by <see cref="AppSettings.PlinkInitialSizeWaitMs"/>; SSH.NET passes
    /// a zero wait, which completes synchronously and costs it nothing. Every fallback to the
    /// default is logged with its reason, so the silent 80x24 cannot come back unseen.</para>
    /// </remarks>
    private async Task<TerminalSize> ResolveInitialTerminalSizeForAsync(
        ServerProfileDto server,
        TimeSpan wait,
        CancellationToken ct)
    {
        Func<string, TimeSpan, CancellationToken, Task<TerminalSizeLookup>>? resolve = ResolveInitialTerminalSize;
        if (resolve is null)
        {
            LogDefaultTerminalSize(server, "no size resolver is wired");
            return TerminalSize.Default;
        }

        TerminalSizeLookup lookup;
        try
        {
            lookup = await resolve(server.Id, wait, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TerminalSizeLog(
                $"SSH initial terminal size wait for {server.DisplayName} cancelled; nothing was launched");
            throw;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"SSH initial terminal size lookup failed for {server.DisplayName}, using the default: {ex.Message}");
            return TerminalSize.Default;
        }

        if (lookup.Size is { } size)
        {
            TerminalSizeLog(
                $"SSH opening the PTY for {server.DisplayName} at the {DescribeSizeSource(lookup.Source)} "
                + $"{size.Columns}x{size.Rows}");
            return size;
        }

        LogDefaultTerminalSize(server, DescribeMissingSize(lookup));
        return TerminalSize.Default;
    }

    /// <summary>The name a support log gives the origin of a size that was used.</summary>
    internal static string DescribeSizeSource(TerminalSizeSource source) => source switch
    {
        TerminalSizeSource.PreviousView => "previous view",
        TerminalSizeSource.Window => "window",
        _ => "reported",
    };

    internal static string DescribeMissingSize(TerminalSizeLookup lookup) => lookup.Source switch
    {
        TerminalSizeSource.NoView => "no terminal view is connecting for this session",
        TerminalSizeSource.TimedOut =>
            $"the terminal page did not report its size within {(int)lookup.Waited.TotalMilliseconds} ms",
        _ => "the terminal page has not reported its size yet and this transport does not wait",
    };

    private void LogDefaultTerminalSize(ServerProfileDto server, string reason)
    {
        TerminalSizeLog(
            $"SSH opening the PTY for {server.DisplayName} at the default "
            + $"{TerminalSize.DefaultColumns}x{TerminalSize.DefaultRows}: {reason}");
    }

    /// <summary>
    /// Whether this session gets X11 forwarding: the profile asked for it and an X server is
    /// available. When the profile asks and no server can be found or started, the localized
    /// notice goes to the status text, the same path the direct-transport capability notice
    /// uses, and the answer is <see langword="false"/> so the launcher is not handed <c>-X</c>
    /// for a display that does not exist.
    /// </summary>
    /// <remarks>
    /// The three launch paths used to ignore the manager's answer: Plink and PuTTY were still
    /// given <c>-X</c>, every X client on the remote side failed with "cannot open display", and
    /// the user saw nothing here explaining why. The notice existed and went to the log only.
    /// </remarks>
    private async Task<bool> ResolveX11ForwardingAsync(ServerProfileDto server)
    {
        if (!server.SshX11Forwarding)
        {
            return false;
        }

        bool available;
        try
        {
            available = await _x11ServerManager.EnsureRunningAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"X11 server check failed for {server.DisplayName}: {ex.Message}");
            available = false;
        }

        if (available)
        {
            return true;
        }

        Core.Logging.FileLogger.Warn(
            $"X11 forwarding requested for {server.DisplayName} but no X11 server is available; launching without it.");
        SetStatusText?.Invoke(_localizer[SshLocalizationKeys.X11ServerNotFound]);
        return false;
    }

    // The lease names the one reference this connect took, so giving it back here and again
    // from the pane close that follows a failed connect releases it only once.
    private void ReleaseTunnelIfNeeded(bool usesTunnel, int tunnelLocalPort, long tunnelLeaseId)
    {
        if (!usesTunnel || tunnelLocalPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(tunnelLocalPort, tunnelLeaseId);
    }

    /// <summary>
    /// Launches putty.exe as an external (non-embedded) SSH session.
    /// Used when <see cref="ServerProfileDto.SshMode"/> is "External".
    /// </summary>
    private async Task<ConnectionResult> ConnectSshExternal(
        ServerProfileDto server,
        AppSettings settings,
        string targetHost,
        int targetPort,
        bool usesTunnel,
        long tunnelLeaseId,
        CancellationToken ct)
    {
        bool releaseTunnel = usesTunnel;
        try
        {
            string? puttyPath = ConnectionHelpers.ResolvePuttyPath(settings.PuttyPath, settings.PlinkPath);
            if (string.IsNullOrWhiteSpace(puttyPath) || !File.Exists(puttyPath))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorPuttyNotConfigured];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreateGenericFailure(SshLocalizationKeys.ErrorPuttyNotConfigured, msg));
            }

            if (!string.IsNullOrEmpty(server.SshUsername) &&
                !InputValidator.Validate(server.SshUsername, "SshUser"))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidSshUsername];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePreflightFailure(SshLocalizationKeys.ErrorInvalidSshUsername, msg));
            }

            if (!InputValidator.IsValidSshHost(targetHost))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidTargetHost];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePreflightFailure(SshLocalizationKeys.ErrorInvalidTargetHost, msg));
            }

            if (!InputValidator.ValidatePortRange(targetPort))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidTargetPort];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(false, msg, null, SshSessionDiagnosticFactory.CreatePreflightFailure(SshLocalizationKeys.ErrorInvalidTargetPort, msg));
            }

            if (!TryValidateKeyPath(server.SshKeyPath, out SshKeyPathValidationError keyPathError))
            {
                string keyPathMessage = LocalizeKeyPathError(keyPathError, server.SshKeyPath);
                _connectionSm.SetError(server.Id, keyPathMessage);
                return new ConnectionResult(false, keyPathMessage, null, SshSessionDiagnosticFactory.CreatePreflightFailure(SshLocalizationKeys.ErrorConnectionFailed, keyPathMessage));
            }

            string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
            var hostKeyIdentity = ResolvePlinkHostKeyIdentity(server, targetHost, targetPort, usesTunnel);
            string? storedFingerprint = _hostKeyTrustService
                .GetEffectiveEntry(hostKeyIdentity.Host, hostKeyIdentity.Port)
                ?.Fingerprint;
            PlinkHostKeyDecision hostKeyDecision = await PlinkHostKeyDecider.DecideAsync(
                    transportHost: targetHost,
                    transportPort: targetPort,
                    verificationHost: hostKeyIdentity.Host,
                    verificationPort: hostKeyIdentity.Port,
                    username: server.SshUsername,
                    plinkPath: plinkPath,
                    probeTimeoutMs: settings.HostKeyProbeTimeoutMs,
                    storedFingerprint: storedFingerprint,
                    probe: _plinkHostKeyProbe,
                    verifier: _hostKeyVerifier,
                    trustService: _hostKeyTrustService,
                    ct: ct)
                .ConfigureAwait(false);
            if (!hostKeyDecision.ShouldProceed)
            {
                return BuildPlinkHostKeyRejectionResult(
                    server.Id,
                    hostKeyIdentity.Host,
                    hostKeyIdentity.Port,
                    hostKeyDecision);
            }

            string? hostKeyArg = hostKeyDecision.Fingerprint;
            string target = !string.IsNullOrEmpty(server.SshUsername)
                ? $"{server.SshUsername}@{targetHost}"
                : targetHost;

            bool x11Forwarding = await ResolveX11ForwardingAsync(server).ConfigureAwait(false);
            System.Diagnostics.ProcessStartInfo psi = BuildPuttyStartInfo(
                puttyPath,
                server.SshKeyPath,
                server.SshCompression,
                server.SshAgentForwarding,
                x11Forwarding,
                targetPort,
                target,
                hostKeyArg);
            Core.Logging.FileLogger.Info($"Launching PuTTY: {puttyPath} for {targetHost}:{targetPort}");

            System.Diagnostics.Process? process = System.Diagnostics.Process.Start(psi);

            Core.Logging.FileLogger.Info(
                $"Launched putty.exe PID={process?.Id ?? 0} for {server.DisplayName} ({targetHost}:{targetPort})");
            _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
            if (process is not null)
            {
                System.Diagnostics.Process startedProcess = process;
                startedProcess.Exited += (_, _) =>
                {
                    try
                    {
                        ReleaseTunnelIfNeeded(usesTunnel, targetPort, tunnelLeaseId);
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.FileLogger.Warn(
                            $"Tunnel release on putty.exe exit failed: {ex.Message}");
                    }

                    try
                    {
                        startedProcess.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.FileLogger.Warn(
                            $"putty.exe Process.Dispose failed: {ex.Message}");
                    }
                };
                releaseTunnel = false;
                startedProcess.EnableRaisingEvents = true;
            }

            return new ConnectionResult(true, null, null);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("PuTTY launch failed", ex);
            _connectionSm.SetError(server.Id, ex.Message);
            return new ConnectionResult(
                false,
                ex.Message,
                null,
                SshSessionDiagnosticFactory.CreateGenericFailure(SshLocalizationKeys.ErrorConnectionFailed, ex.Message));
        }
        finally
        {
            ReleaseTunnelIfNeeded(releaseTunnel, targetPort, tunnelLeaseId);
        }
    }

    /// <summary>
    /// Launches plink.exe as an interactive SSH session via ConPTY.
    /// Used when SSH.NET cannot authenticate.
    /// </summary>
    internal async Task<ConnectionResult> ConnectSshViaPlinkAsync(
        ServerProfileDto server,
        AppSettings settings,
        string targetHost,
        int targetPort,
        bool usesTunnel,
        SshFailureCode? originalFailure,
        CancellationToken ct,
        string? originalFailureMessage = null,
        long tunnelLeaseId = 0)
    {
        bool releaseTunnel = usesTunnel;
        try
        {
            string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
            if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath))
            {
                // Report the cause, not the retry that could not be attempted. This used to say
                // "SSH.NET failed (PasswordRejected), but Plink is not found. Set the path in
                // Settings." - an unlocalized enum plus an instruction that fixes nothing,
                // because the password was simply wrong. Plink is an internal retry strategy;
                // its absence belongs in the log, not in front of the user.
                //
                // How often this fires: rarely, and the claim that it was common is refuted.
                // plink.exe is vendored at Assets/Tools and innosetup ships the tree recursively,
                // so ResolvePlinkPath normally finds the embedded copy and this branch never
                // runs. It is reachable when that copy is gone - a portable extraction, an
                // antivirus quarantine, a manual delete. Worth fixing because the message is
                // wrong whenever it does appear, not because it appears often.
                string msg = !string.IsNullOrWhiteSpace(originalFailureMessage)
                    ? originalFailureMessage
                    : originalFailure is not null
                        ? _localizer.Format(SshLocalizationKeys.ErrorPlinkNotConfiguredWithReason, originalFailure)
                        : _localizer[SshLocalizationKeys.ErrorPlinkNotConfigured];
                Core.Logging.FileLogger.Info(
                    $"Plink fallback unavailable (path '{settings.PlinkPath}' unresolved); "
                    + $"reporting the original failure {originalFailure} instead.");
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                        SshLocalizationKeys.ErrorPlinkNotConfigured,
                        msg,
                        originalFailure));
            }

            // The chosen agent key is checked again here although the preflight and the retry
            // decision already looked: this method is also entered directly, and the Pageant,
            // key-file and launcher guards below are independent of the lookup that found the key.
            // Ahead of every host-key probe, prompt and file, for the same reason as the preflight.
            // chosenAgentKey is the key as PAGEANT holds it: a key present in both agents is
            // reported by the first one in preference order by an unrestricted search, which would
            // wrongly read as "not in Pageant" here.
            SshAgentIdentityMatch? chosenAgentKey = null;
            if (server.SshAgentKeyFingerprint is { } chosenFingerprint)
            {
                SshAgentRegistry chosenRegistry = _agentRegistryFactory(settings.SshAgentPreference);
                SshAgentKeyRefusal refusal;
                if (await SshAgentKeyPreflight.LocateAsync(chosenFingerprint, chosenRegistry, ct: ct)
                        .ConfigureAwait(false) is null)
                {
                    refusal = SshAgentKeyRefusal.NotLoaded;
                }
                else
                {
                    chosenAgentKey = await SshAgentKeyPreflight.LocateAsync(
                            chosenFingerprint,
                            chosenRegistry,
                            PageantAgent.AgentName,
                            ct: ct)
                        .ConfigureAwait(false);
                    refusal = SshAgentKeyPreflight.RefusalForPlink(
                        chosenAgentKey,
                        keyFileConfigured: !string.IsNullOrWhiteSpace(server.SshKeyPath),
                        plinkAttested: IsPlinkAttested(plinkPath));
                }

                if (refusal != SshAgentKeyRefusal.None)
                {
                    return RefuseAgentKey(server, refusal, chosenFingerprint, originalFailureMessage);
                }
            }

            // Refused here, before the password dialog, before any host-key probe or trust
            // mutation, before the launcher is identified and before the password file exists.
            // Without a username the launcher waits for a login name and writes nothing, so the
            // first byte that normally proves it consumed the file never arrives and the secret
            // would sit on disk for the whole session. Measured, not assumed.
            if (RequiresUsernameBeforeWritingPassword(server))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorSshUsernameRequiredForPassword];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                        SshLocalizationKeys.ErrorSshUsernameRequiredForPassword,
                        msg));
            }

            if (!string.IsNullOrEmpty(server.SshUsername) &&
                !InputValidator.Validate(server.SshUsername, "SshUser"))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidSshUsername];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(SshLocalizationKeys.ErrorInvalidSshUsername, msg));
            }

            if (!InputValidator.IsValidSshHost(targetHost))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidTargetHost];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(
                    false,
                    msg,
                    null,
                    SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(SshLocalizationKeys.ErrorInvalidTargetHost, msg));
            }

            if (!InputValidator.ValidatePortRange(targetPort))
            {
                string msg = _localizer[SshLocalizationKeys.ErrorInvalidTargetPort];
                _connectionSm.SetError(server.Id, msg);
                return new ConnectionResult(false, msg, null, SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(SshLocalizationKeys.ErrorInvalidTargetPort, msg));
            }

            if (!TryValidateKeyPath(server.SshKeyPath, out SshKeyPathValidationError keyPathError))
            {
                string keyPathMessage = LocalizeKeyPathError(keyPathError, server.SshKeyPath);
                _connectionSm.SetError(server.Id, keyPathMessage);
                return new ConnectionResult(false, keyPathMessage, null, SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(SshLocalizationKeys.ErrorConnectionFailed, keyPathMessage));
            }

            string target = !string.IsNullOrEmpty(server.SshUsername)
                ? $"{server.SshUsername}@{targetHost}"
                : targetHost;

            var hostKeyIdentity = ResolvePlinkHostKeyIdentity(server, targetHost, targetPort, usesTunnel);
            string? storedFingerprint = _hostKeyTrustService
                .GetEffectiveEntry(hostKeyIdentity.Host, hostKeyIdentity.Port)
                ?.Fingerprint;
            PlinkHostKeyDecision hostKeyDecision = await PlinkHostKeyDecider.DecideAsync(
                    transportHost: targetHost,
                    transportPort: targetPort,
                    verificationHost: hostKeyIdentity.Host,
                    verificationPort: hostKeyIdentity.Port,
                    username: server.SshUsername,
                    plinkPath: plinkPath,
                    probeTimeoutMs: settings.HostKeyProbeTimeoutMs,
                    storedFingerprint: storedFingerprint,
                    probe: _plinkHostKeyProbe,
                    verifier: _hostKeyVerifier,
                    trustService: _hostKeyTrustService,
                    ct: ct)
                .ConfigureAwait(false);

            if (!hostKeyDecision.ShouldProceed)
            {
                return BuildPlinkHostKeyRejectionResult(
                    server.Id,
                    hostKeyIdentity.Host,
                    hostKeyIdentity.Port,
                    hostKeyDecision);
            }

            string? hostKeyArg = hostKeyDecision.Fingerprint;

            // The password is resolved in full first, dialog included. Nothing is identified and
            // nothing is written to disk while a modal can hold this thread for an unbounded time:
            // an attestation taken before the dialog would describe an image that a legitimate
            // update could have replaced before the launch.
            string? password = ConnectionHelpers.DecryptPassword(server.SshPasswordEncrypted);
            // The chosen key restricts which agent keys are offered and nothing else: the prompt
            // conditions are the same as without one.
            if (ShouldPromptForPlinkPassword(password, server.SshKeyPath))
            {
                // The prompt always says that an empty answer is allowed: a host that signs in
                // with Kerberos or a Pageant key needs no password, and forcing a dummy one
                // was the workaround this replaces.
                string promptMessage = string.Concat(
                    _localizer.Format("DialogSshPasswordPromptMessage", target),
                    Environment.NewLine,
                    _localizer[SshLocalizationKeys.PlinkPasswordPromptPasswordlessHint]);

                password = await _dialogService.ShowPasswordInputAsync(
                        _localizer["DialogSshPasswordPromptTitle"],
                        promptMessage,
                        ct)
                    .ConfigureAwait(false);

                // Cancel answers null and cancels the connection. An empty answer is the
                // credential-free launch: no password file and plink in batch mode, so Kerberos
                // or an agent key decides and a refusal exits at once instead of waiting on a
                // hidden console (measured).
                if (password is null)
                {
                    string message = SshFailureMessageBuilder.Cancelled(_localizer);
                    _connectionSm.SetError(server.Id, message);
                    return new ConnectionResult(
                        false,
                        message,
                        null,
                        SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                            SshLocalizationKeys.ErrorSshCancelled,
                            message,
                            SshFailureCode.Cancelled));
                }
            }

            // True when plink is launched with neither a password file nor a private key file.
            bool credentialFree = string.IsNullOrEmpty(password) && string.IsNullOrWhiteSpace(server.SshKeyPath);

            // Plink takes the remote PTY size only from its saved-session configuration, and the
            // pipe transport cannot resize after start, so the launch is the only moment the size
            // can travel. On a fresh tab the page is still loading here, so its first report is
            // awaited, bounded. Placed before the password file and the attestation lease so the
            // wait widens neither, and so a cancellation here leaves nothing to clean up.
            TerminalSize initialSize = await ResolveInitialTerminalSizeForAsync(
                    server,
                    TimeSpan.FromMilliseconds(Math.Max(0, settings.PlinkInitialSizeWaitMs)),
                    ct)
                .ConfigureAwait(false);

            Heimdall.Terminal.PipeModeSession terminalSession = new Heimdall.Terminal.PipeModeSession();
            Core.Logging.FileLogger.Info(
                $"SSH via Plink ({terminalSession.GetType().Name}) using {plinkPath} for {targetHost}:{targetPort}");

            // The dialog is over, so the gap between identifying the image and launching it is now
            // bounded by this block alone. The lease pins that exact image; the block closes as soon
            // as the launch has been issued, so a later update is not held off for the whole session.
            // Resolved before the attestation so a slow X server start does not widen the gap
            // between identifying the launcher image and launching it.
            bool x11Forwarding = await ResolveX11ForwardingAsync(server).ConfigureAwait(false);

            using (PlinkAttestationLease attestation = _plinkAttestation(plinkPath))
            {
                // The image is identified again at the lease that will be launched: the check
                // above looked at a file that could have been replaced during the prompts. Nothing
                // has been written yet, so refusing here leaves nothing to clean up.
                if (chosenAgentKey is not null && !attestation.FirstByteProvesConsumption)
                {
                    return RefuseAgentKey(
                        server,
                        SshAgentKeyRefusal.PlinkNotBundled,
                        server.SshAgentKeyFingerprint!,
                        originalFailureMessage);
                }

                PlinkLaunchPreparation preparation = PrepareLaunch(
                    server,
                    password,
                    initialSize,
                    x11Forwarding,
                    targetPort,
                    target,
                    hostKeyArg,
                    chosenAgentKey,
                    terminalSession,
                    attestation.FirstByteProvesConsumption,
                    credentialFree);
                if (preparation.Error is { } prepareError)
                {
                    Core.Logging.FileLogger.Error("Plink launch preparation failed", prepareError);
                    _connectionSm.SetError(server.Id, prepareError.Message);
                    return new ConnectionResult(
                        false,
                        prepareError.Message,
                        null,
                        SshSessionDiagnosticFactory.CreatePipeModeFailure(prepareError.Message));
                }

                string args = preparation.Args;
                PlinkPasswordFileReleaseHandle? passwordFileRelease = preparation.PasswordFileRelease;
                PlinkSizeSessionReleaseHandle? sizeSessionRelease = preparation.SizeSessionRelease;
                PlinkPasswordFileReleaseHandle? agentKeyFileRelease = preparation.AgentKeyFileRelease;

                // The attested path comes from the pinned handle, with every junction already
                // followed. Launching the configured string instead would resolve it a second time,
                // and a junction repointed since the attestation makes that a different image -
                // measured, and not prevented by the string being absolute. With nothing attested
                // there is nothing to bind to, so the configured path is used and the password file
                // stays on the process-exit path.
                string launchPath = attestation.LaunchPath ?? plinkPath;

                try
                {
                    await _startPipeModeSession(
                            terminalSession,
                            launchPath,
                            args,
                            initialSize.Columns,
                            initialSize.Rows,
                            ct)
                        .ConfigureAwait(false);
                    Core.Logging.FileLogger.Info($"Plink SSH session started: PID={terminalSession.ProcessId}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    terminalSession.Dispose();
                    passwordFileRelease?.Release();
                    sizeSessionRelease?.Release();
                    agentKeyFileRelease?.Release();
                    throw;
                }
                catch (Exception ex)
                {
                    terminalSession.Dispose();
                    passwordFileRelease?.Release();
                    sizeSessionRelease?.Release();
                    agentKeyFileRelease?.Release();
                    Core.Logging.FileLogger.Error("Plink SSH launch failed", ex);
                    _connectionSm.SetError(server.Id, ex.Message);
                    return new ConnectionResult(
                        false,
                        ex.Message,
                        null,
                        SshSessionDiagnosticFactory.CreatePipeModeFailure(ex.Message));
                }
            }

            _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
            releaseTunnel = false;
            return new ConnectionResult(true, null, new TerminalSessionResult(
                terminalSession,
                BuildDisplayEndpoint(server),
                server.SessionLoggingOverride)
            {
                CredentialFreeLaunch = credentialFree,
            });
        }
        finally
        {
            ReleaseTunnelIfNeeded(releaseTunnel, targetPort, tunnelLeaseId);
        }
    }

    /// <summary>
    /// Refuses, before the tunnel and the dial, a session whose chosen agent key cannot be used.
    /// </summary>
    /// <returns>
    /// The refusal (null when the connection may proceed) and the key found in any agent (null
    /// when none is chosen).
    /// </returns>
    private async Task<(ConnectionResult? Refusal, SshAgentIdentityMatch? Chosen)> PreflightChosenAgentKeyAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct)
    {
        if (server.SshAgentKeyFingerprint is not { } fingerprint)
        {
            return (null, null);
        }

        // An external PuTTY has no way to be told which agent key to use.
        if (string.Equals(server.SshMode, "External", StringComparison.OrdinalIgnoreCase))
        {
            return (RefuseAgentKey(server, SshAgentKeyRefusal.LauncherUnsupported, fingerprint), null);
        }

        SshAgentRegistry registry = _agentRegistryFactory(settings.SshAgentPreference);
        SshAgentIdentityMatch? chosen = await SshAgentKeyPreflight
            .LocateAsync(fingerprint, registry, ct: ct)
            .ConfigureAwait(false);
        if (chosen is null)
        {
            return (RefuseAgentKey(server, SshAgentKeyRefusal.NotLoaded, fingerprint), null);
        }

        // Forwarding sends the connection through Plink, which only reaches Pageant. Whether
        // Pageant holds the key and can describe it in a file Plink reads is judged here; the key
        // file and the launcher need the plink path, so they are judged where it is resolved.
        if (server.SshAgentForwarding && registry.HasPlinkCompatibleAgent())
        {
            SshAgentIdentityMatch? pageantKey = await SshAgentKeyPreflight
                .LocateAsync(fingerprint, registry, PageantAgent.AgentName, ct: ct)
                .ConfigureAwait(false);
            SshAgentKeyRefusal refusal = SshAgentKeyPreflight.RefusalForPlink(
                pageantKey,
                keyFileConfigured: false,
                plinkAttested: true);
            if (refusal != SshAgentKeyRefusal.None)
            {
                return (RefuseAgentKey(server, refusal, fingerprint), null);
            }
        }

        return (null, chosen);
    }

    /// <summary>
    /// Whether a retry through Plink would offer the chosen key and nothing else.
    /// </summary>
    private async Task<bool> CanPlinkHonourChosenKeyAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct)
    {
        string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
        if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath) || server.SshAgentKeyFingerprint is null)
        {
            return false;
        }

        SshAgentIdentityMatch? pageantKey = await SshAgentKeyPreflight
            .LocateAsync(
                server.SshAgentKeyFingerprint,
                _agentRegistryFactory(settings.SshAgentPreference),
                PageantAgent.AgentName,
                ct: ct)
            .ConfigureAwait(false);
        return SshAgentKeyPreflight.RefusalForPlink(
            pageantKey,
            keyFileConfigured: !string.IsNullOrWhiteSpace(server.SshKeyPath),
            plinkAttested: IsPlinkAttested(plinkPath)) == SshAgentKeyRefusal.None;
    }

    /// <summary>What the launch needs, built under a guard that frees everything on any failure.</summary>
    private sealed record PlinkLaunchPreparation(
        string Args,
        PlinkPasswordFileReleaseHandle? PasswordFileRelease,
        PlinkSizeSessionReleaseHandle? SizeSessionRelease,
        PlinkPasswordFileReleaseHandle? AgentKeyFileRelease,
        Exception? Error);

    /// <summary>
    /// Writes the password file, creates the size session, builds the command line and writes the
    /// agent public key file, arming each release as it goes.
    /// </summary>
    /// <remarks>
    /// Any exception frees every file created so far and disposes the session, then comes back as
    /// <see cref="PlinkLaunchPreparation.Error"/>: the password file holds a secret and must not
    /// outlive a failure between its creation and the launch. A method of its own so the launch
    /// catches of the caller keep their shape.
    /// </remarks>
    private PlinkLaunchPreparation PrepareLaunch(
        ServerProfileDto server,
        string? password,
        TerminalSize initialSize,
        bool x11Forwarding,
        int targetPort,
        string target,
        string? hostKeyArg,
        SshAgentIdentityMatch? chosenAgentKey,
        Heimdall.Terminal.PipeModeSession terminalSession,
        bool firstByteProvesConsumption,
        bool credentialFree)
    {
        string? passwordFilePath = null;
        string? sizeSessionName = null;
        string? agentKeyFilePath = null;
        PlinkPasswordFileReleaseHandle? passwordFileRelease = null;
        PlinkSizeSessionReleaseHandle? sizeSessionRelease = null;
        PlinkPasswordFileReleaseHandle? agentKeyFileRelease = null;
        try
        {
            passwordFilePath = CreatePlinkPasswordFile(password);

            // A temporary saved session is the only way the size reaches Plink. A null name
            // means the registry refused and the launch goes ahead at Plink's default size.
            sizeSessionName = PlinkSizeSession.TryCreate(
                _puttySessionRegistry,
                initialSize.Columns,
                initialSize.Rows);

            agentKeyFilePath = chosenAgentKey is null ? null : PlinkAgentKeyFile.NewPath();

            string args = BuildPipeModeArguments(
                server.SshKeyPath,
                server.SshCompression,
                server.SshAgentForwarding,
                x11Forwarding,
                targetPort,
                target,
                hostKeyArg,
                passwordFilePath,
                sizeSessionName,
                agentKeyFilePath,
                credentialFree);

            if (!string.IsNullOrEmpty(passwordFilePath))
            {
                // Armed before the process starts so no output can arrive unobserved. Every path
                // that frees this file goes through the returned handle, including the catches
                // of the caller: disposing the session can itself raise ProcessExited, so a direct
                // delete there would be a second invocation.
                passwordFileRelease = PlinkPasswordFileRelease.Arm(
                    terminalSession,
                    passwordFilePath,
                    _deletePlinkPasswordFile,
                    firstByteProvesConsumption);

                // Disposing kills the process and detaches the exit notification, so a tab closed
                // on a normal path would otherwise leave the plaintext password file behind until
                // the janitor. The handle deletes once whichever signal arrives first.
                PlinkPasswordFileReleaseHandle passwordHandle = passwordFileRelease;
                terminalSession.Disposed += passwordHandle.Release;
            }

            // Same single release path and the same proof as the password file: -load is read
            // in the command-line pass, so the first byte shows the session was loaded.
            if (sizeSessionName is not null)
            {
                sizeSessionRelease = PlinkSizeSession.ArmRelease(
                    terminalSession,
                    sizeSessionName,
                    name => PlinkSizeSession.Delete(_puttySessionRegistry, name),
                    firstByteProvesConsumption);
            }

            // The public key file is written last, once the other releases are armed. It is
            // freed on process exit and also when the session is disposed: disposing kills the
            // process and detaches the exit notification, so a closed tab would otherwise leave
            // it behind. The key is read when authentication starts, not when the command line is
            // parsed, so the first byte of output proves nothing about it.
            if (chosenAgentKey is not null && agentKeyFilePath is not null)
            {
                agentKeyFileRelease = PlinkPasswordFileRelease.Arm(
                    terminalSession,
                    agentKeyFilePath,
                    _deletePlinkPasswordFile,
                    firstByteProvesConsumption: false);
                PlinkPasswordFileReleaseHandle keyFileHandle = agentKeyFileRelease;
                terminalSession.Disposed += keyFileHandle.Release;
                _writeAgentKeyFile(agentKeyFilePath, chosenAgentKey.Key);
            }

            return new PlinkLaunchPreparation(args, passwordFileRelease, sizeSessionRelease, agentKeyFileRelease, null);
        }
        catch (Exception ex)
        {
            terminalSession.Dispose();

            // A file created but not yet armed has no handle: freed directly. An armed one is freed
            // through its handle, which deletes at most once.
            if (passwordFileRelease is not null)
            {
                passwordFileRelease.Release();
            }
            else if (passwordFilePath is not null)
            {
                _deletePlinkPasswordFile(passwordFilePath);
            }

            if (sizeSessionRelease is not null)
            {
                sizeSessionRelease.Release();
            }
            else if (sizeSessionName is not null)
            {
                PlinkSizeSession.Delete(_puttySessionRegistry, sizeSessionName);
            }

            if (agentKeyFileRelease is not null)
            {
                agentKeyFileRelease.Release();
            }
            else if (agentKeyFilePath is not null)
            {
                _deletePlinkPasswordFile(agentKeyFilePath);
            }

            return new PlinkLaunchPreparation(string.Empty, null, null, null, ex);
        }
    }

    private bool IsPlinkAttested(string? plinkPath)
    {
        using PlinkAttestationLease lease = _plinkAttestation(plinkPath);
        return lease.FirstByteProvesConsumption;
    }

    private ConnectionResult RefuseAgentKey(
        ServerProfileDto server,
        SshAgentKeyRefusal refusal,
        string fingerprint,
        string? originalFailureMessage = null)
    {
        string key = SshAgentKeyPreflight.MessageKey(refusal);
        string refusalMessage = SshAgentKeyPreflight.NamesFingerprint(refusal)
            ? _localizer.Format(key, fingerprint)
            : _localizer[key];

        // After a refusal the embedded client already met, its sentence stays first.
        string message = string.IsNullOrWhiteSpace(originalFailureMessage)
            ? refusalMessage
            : SshAuthFailureMessageComposer.AppendSentence(originalFailureMessage, refusalMessage, key)
                ?? refusalMessage;

        Core.Logging.FileLogger.Warn(
            $"SSH connection of {server.DisplayName} refused: agent key {fingerprint}: {refusal}");
        _connectionSm.SetError(server.Id, message);
        return new ConnectionResult(
            false,
            message,
            null,
            SshSessionDiagnosticFactory.CreatePreflightFailure(
                key,
                message,
                refusal == SshAgentKeyRefusal.LauncherUnsupported ? null : SshFailureCode.AgentKeyNotLoaded));
    }

    private static string BuildDisplayEndpoint(ServerProfileDto server)
    {
        var user = string.IsNullOrWhiteSpace(server.SshUsername) ? "?" : server.SshUsername;
        var host = string.IsNullOrWhiteSpace(server.RemoteServer) ? "?" : server.RemoteServer;
        var port = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        return $"{user}@{host}:{port}";
    }

    /// <summary>
    /// Whether this profile would put a password on disk for a launcher that has no login name.
    /// </summary>
    /// <remarks>
    /// <para>Password-backed means either a stored password - with or without a key - or neither a
    /// password nor a key, since that path goes on to ask for a password. A profile with a key and
    /// no password never writes the file, so it is left alone.</para>
    /// <para>Whitespace counts as absent: the launcher would receive a bare host either way.</para>
    /// </remarks>
    internal static bool RequiresUsernameBeforeWritingPassword(ServerProfileDto server)
    {
        if (!string.IsNullOrWhiteSpace(server.SshUsername))
        {
            return false;
        }

        bool hasStoredPassword = !string.IsNullOrEmpty(server.SshPasswordEncrypted);
        bool hasKey = !string.IsNullOrWhiteSpace(server.SshKeyPath);

        return hasStoredPassword || !hasKey;
    }

    internal static bool ShouldPromptForPlinkPassword(string? password, string? keyPath)
    {
        // Takes the password rather than the file that would hold it: the file is created after the
        // dialog now, so asking about it here would mean writing the secret to disk before knowing
        // whether the user was even going to supply one.
        return string.IsNullOrEmpty(password) && string.IsNullOrWhiteSpace(keyPath);
    }

    private static (string Host, int Port) ResolvePlinkHostKeyIdentity(
        ServerProfileDto server,
        string transportHost,
        int transportPort,
        bool usesTunnel)
    {
        return usesTunnel
            ? (server.RemoteServer, server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh)
            : (transportHost, transportPort);
    }

    private static string? CreatePlinkPasswordFile(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var passwordFilePath = Path.Combine(
            Path.GetTempPath(),
            $"{PlinkPasswordFileNaming.Prefix}{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            SecureFileWriter.WriteAndProtect(passwordFilePath, password);
        }
        else
        {
            File.WriteAllText(passwordFilePath, password);
            try
            {
                File.SetUnixFileMode(
                    passwordFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"[SshHandler] Unix file mode restriction failed for Plink password file: {ex.Message}");
            }
        }

        return passwordFilePath;
    }

    private static void DeletePlinkPasswordFile(string? passwordFilePath)
    {
        if (string.IsNullOrWhiteSpace(passwordFilePath))
        {
            return;
        }

        try
        {
            File.Delete(passwordFilePath);
        }
        catch (IOException ex)
        {
            Core.Logging.FileLogger.Warn($"[SshHandler] DeletePlinkPasswordFile: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Core.Logging.FileLogger.Warn($"[SshHandler] DeletePlinkPasswordFile: {ex.Message}");
        }
    }

    internal static System.Diagnostics.ProcessStartInfo BuildPuttyStartInfo(
        string puttyPath,
        string? keyPath,
        bool compression,
        bool agentForwarding,
        bool x11Forwarding,
        int port,
        string target,
        string? hostKeyFingerprint)
    {
        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = puttyPath,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-ssh");
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(keyPath);
        }

        if (compression)
        {
            psi.ArgumentList.Add("-C");
        }

        if (agentForwarding)
        {
            psi.ArgumentList.Add("-A");
        }

        if (x11Forwarding)
        {
            psi.ArgumentList.Add("-X");
        }

        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(port.ToString());

        if (!string.IsNullOrWhiteSpace(hostKeyFingerprint))
        {
            psi.ArgumentList.Add("-hostkey");
            psi.ArgumentList.Add(hostKeyFingerprint);
        }

        psi.ArgumentList.Add(target);
        return psi;
    }

    internal static string BuildPipeModeArguments(
        string? keyPath,
        bool compression,
        bool agentForwarding,
        bool x11Forwarding,
        int port,
        string target,
        string? hostKeyFingerprint,
        string? passwordFilePath = null,
        string? sizeSessionName = null,
        string? agentPublicKeyFilePath = null,
        bool batchMode = false)
    {
        // Plink takes one identity file. Both set means two things asked for the same slot, which
        // the handler refuses before building: reaching here is a programming error.
        if (!string.IsNullOrWhiteSpace(keyPath) && !string.IsNullOrWhiteSpace(agentPublicKeyFilePath))
        {
            throw new ArgumentException(
                "A private key file and an agent public key file cannot both take the -i slot.",
                nameof(agentPublicKeyFilePath));
        }

        var argParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sizeSessionName))
        {
            // First, so every explicit flag below reads as applied on top of the loaded session.
            // Plink replays its explicit options after -load whatever the order (measured: the
            // size and -hostkey hold at any position); the order only keeps that obvious.
            argParts.Add("-load");
            argParts.Add(sizeSessionName);
        }

        argParts.AddRange(["-ssh", "-t", "-no-antispoof"]);
        if (batchMode)
        {
            // Plink cannot answer a prompt in batch mode, so a refused sign-in exits with code 1
            // instead of waiting forever on a console nobody can see.
            argParts.Add("-batch");
        }

        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            argParts.Add("-i");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(keyPath)}\"");
        }
        else if (!string.IsNullOrWhiteSpace(agentPublicKeyFilePath))
        {
            // A public key file given to -i makes Pageant offer that key (PuTTY documents it).
            // Nothing private is read from this file.
            argParts.Add("-i");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(agentPublicKeyFilePath)}\"");
        }

        if (compression)
        {
            argParts.Add("-C");
        }

        if (agentForwarding)
        {
            argParts.Add("-A");
        }

        if (x11Forwarding)
        {
            argParts.Add("-X");
        }

        argParts.Add("-P");
        argParts.Add(port.ToString());

        if (!string.IsNullOrWhiteSpace(hostKeyFingerprint))
        {
            argParts.Add("-hostkey");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(hostKeyFingerprint)}\"");
        }

        if (!string.IsNullOrWhiteSpace(passwordFilePath))
        {
            argParts.Add("-pwfile");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(passwordFilePath)}\"");
        }

        argParts.Add(target);
        return string.Join(' ', argParts);
    }

    internal static bool TryValidateKeyPath(
        string? keyPath,
        out SshKeyPathValidationError error)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            error = SshKeyPathValidationError.None;
            return true;
        }

        if (keyPath.Contains('\0') || keyPath.Contains('"'))
        {
            error = SshKeyPathValidationError.InvalidCharacters;
            return false;
        }

        if (!Path.IsPathRooted(keyPath))
        {
            error = SshKeyPathValidationError.NotAbsolute;
            return false;
        }

        if (!File.Exists(keyPath))
        {
            error = SshKeyPathValidationError.FileNotFound;
            return false;
        }

        error = SshKeyPathValidationError.None;
        return true;
    }

    private string LocalizeKeyPathError(
        SshKeyPathValidationError error,
        string? keyPath)
    {
        string displayPath = keyPath ?? string.Empty;
        return error switch
        {
            SshKeyPathValidationError.InvalidCharacters =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyPathInvalid, displayPath),
            SshKeyPathValidationError.NotAbsolute =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyPathNotAbsolute, displayPath),
            SshKeyPathValidationError.FileNotFound =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyFileNotFound, displayPath),
            _ => string.Empty,
        };
    }

    private ConnectionResult BuildPlinkHostKeyRejectionResult(
        string serverId,
        string targetHost,
        int targetPort,
        PlinkHostKeyDecision decision)
    {
        if (decision.FailureCode == SshFailureCode.HostKeyMismatch
            && decision.StoredFingerprint is not null
            && decision.PresentedFingerprint is not null)
        {
            string message = SshFailureMessageBuilder.HostKeyMismatch(
                _localizer,
                decision.StoredFingerprint,
                decision.PresentedFingerprint);
            _connectionSm.SetError(serverId, message);
            return new ConnectionResult(
                false,
                message,
                null,
                SshSessionDiagnosticFactory.CreateHostKeyMismatchFailure(
                    decision.StoredFingerprint,
                    decision.PresentedFingerprint,
                    targetHost,
                    targetPort));
        }

        if (decision.FailureCode == SshFailureCode.Cancelled)
        {
            string message = SshFailureMessageBuilder.Cancelled(_localizer);
            _connectionSm.SetError(serverId, message);
            return new ConnectionResult(
                false,
                message,
                null,
                SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                    SshLocalizationKeys.ErrorSshCancelled,
                    message,
                    SshFailureCode.Cancelled));
        }

        string unavailableMessage = SshFailureMessageBuilder.HostKeyUnavailable(_localizer);
        _connectionSm.SetError(serverId, unavailableMessage);
        return new ConnectionResult(
            false,
            unavailableMessage,
            null,
            SshSessionDiagnosticFactory.CreateHostKeyUnavailableFailure(
                unavailableMessage,
                targetHost,
                targetPort));
    }

}
