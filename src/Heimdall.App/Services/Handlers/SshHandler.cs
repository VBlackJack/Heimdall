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
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles SSH connection logic.
/// </summary>
internal sealed class SshHandler : IProtocolHandler, IDisposable
{
    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyTrustService _hostKeyTrustService;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly X11ServerManager _x11ServerManager;
    private readonly IDialogService _dialogService;
    private readonly IPlinkHostKeyProbe _plinkHostKeyProbe;
    private readonly PlinkPasswordFileJanitor _plinkPasswordFileJanitor;
    private readonly SensitiveFileJanitorScheduler _plinkPasswordFileJanitorScheduler;
    private readonly Action<string?> _deletePlinkPasswordFile;
    private readonly Func<string?, bool> _plinkFirstByteAttestation;

    internal Action<string>? SetStatusText { get; set; }

    public SshHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        HostKeyStore hostKeyStore,
        IHostKeyTrustService hostKeyTrustService,
        IHostKeyVerifier hostKeyVerifier,
        X11ServerManager x11ServerManager,
        IDialogService dialogService,
        IPlinkHostKeyProbe? plinkHostKeyProbe = null,
        PlinkPasswordFileJanitor? plinkPasswordFileJanitor = null,
        Action<string?>? deletePlinkPasswordFile = null,
        Func<string?, bool>? plinkFirstByteAttestation = null)
    {
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
        _plinkFirstByteAttestation =
            plinkFirstByteAttestation ?? PlinkCompatibilityAttestation.FirstByteProvesConsumption;
        _plinkPasswordFileJanitorScheduler = new SensitiveFileJanitorScheduler(
            nameof(PlinkPasswordFileJanitor),
            _plinkPasswordFileJanitor.SweepStale);
        _plinkPasswordFileJanitorScheduler.Start();
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

        int sshPort = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        TunnelSetupOutcome tunnelOutcome = await _tunnelService
            .SetupTunnelIfNeededAsync(server, sshPort, settings, ct)
            .ConfigureAwait(false);
        (bool tunnelOk, bool usesTunnel, string targetHost, int targetPort, string? tunnelError) = tunnelOutcome;

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
            return await ConnectSshExternal(server, settings, targetHost, targetPort, usesTunnel, ct)
                .ConfigureAwait(false);
        }

        var sshParams = new SshConnectionParams
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
            UseLegacyPasswordAsKeyPassphrase = server.UsesLegacySshCredentialMapping,
            LegacyCredentialName = server.DisplayName,
            AgentForwarding = server.SshAgentForwarding,
            Compression = server.SshCompression,
            X11Forwarding = server.SshX11Forwarding
        };

        var agentRegistry = SshAgentRegistry.CreateDefault(settings.SshAgentPreference);
        if (sshParams.AgentForwarding
            && !agentRegistry.HasPlinkCompatibleAgent()
            && agentRegistry.HasAnyNonPlinkAgent())
        {
            var message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
            _connectionSm.SetError(server.Id, message);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);
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
            if (server.SshX11Forwarding)
            {
                await _x11ServerManager.EnsureRunningAsync().ConfigureAwait(false);
            }

            return await ConnectSshViaPlinkAsync(
                    server,
                    settings,
                    targetHost,
                    targetPort,
                    usesTunnel,
                    originalFailure: null,
                    ct)
                .ConfigureAwait(false);
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

        var session = new SshShellSession();
        try
        {
            await session.ConnectAsync(
                    sshParams,
                    hostKeyStore: _hostKeyStore,
                    hostKeyVerifier: _hostKeyVerifier,
                    cancellationToken: ct)
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
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);

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

            string cancelledMessage = SshFailureMessageBuilder.Cancelled(_localizer);
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
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);
            throw;
        }
        catch (Exception ex)
        {
            session.Dispose();
            var failure = FailureClassifier.Classify(ex, sshParams);
            SshFailureInfo localizedFailure = LocalizeFailure(failure, targetHost);

            if (failure.Code is SshFailureCode.AuthRejected
                    or SshFailureCode.KeyRejected
                    or SshFailureCode.PassphraseRejected
                    or SshFailureCode.PasswordRejected
                    or SshFailureCode.NoSupportedAuth
                    or SshFailureCode.KeyboardInteractiveNoPassword)
            {
                var fallbackAgentRegistry = SshAgentRegistry.CreateDefault(settings.SshAgentPreference);
                if (!fallbackAgentRegistry.HasPlinkCompatibleAgent() && fallbackAgentRegistry.HasAnyNonPlinkAgent())
                {
                    var message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
                    _connectionSm.SetError(server.Id, message);
                    ReleaseTunnelIfNeeded(usesTunnel, targetPort);
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
                if (server.SshX11Forwarding)
                {
                    await _x11ServerManager.EnsureRunningAsync().ConfigureAwait(false);
                }

                return await ConnectSshViaPlinkAsync(
                        server,
                        settings,
                        targetHost,
                        targetPort,
                        usesTunnel,
                        failure.Code,
                        ct)
                    .ConfigureAwait(false);
            }

            _connectionSm.SetError(server.Id, localizedFailure.Message);
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);
            return new ConnectionResult(
                false,
                localizedFailure.Message,
                null,
                SshSessionDiagnosticFactory.FromClassifiedFailure(localizedFailure, usesTunnel));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
        return new ConnectionResult(true, null, new SshSessionResult(session, server.SessionLoggingOverride));
    }

    private SshFailureInfo LocalizeFailure(SshFailureInfo failure, string targetHost)
    {
        string message = FailureClassifier.FormatMessage(
            failure,
            key =>
            {
                if (!_localizer.HasKey(key))
                {
                    return null;
                }

                object formatArgument = failure.Code is SshFailureCode.NetworkRefused
                    or SshFailureCode.NetworkTimedOut
                    or SshFailureCode.NetworkReset
                    ? targetHost
                    : failure.Message;
                return _localizer.Format(key, formatArgument);
            });

        return new SshFailureInfo(
            failure.Code,
            message,
            failure.IsFatal,
            failure.OriginalException);
    }

    private void ReleaseTunnelIfNeeded(bool usesTunnel, int tunnelLocalPort)
    {
        if (!usesTunnel || tunnelLocalPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(tunnelLocalPort);
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

            System.Diagnostics.ProcessStartInfo psi = BuildPuttyStartInfo(
                puttyPath,
                server.SshKeyPath,
                server.SshCompression,
                server.SshAgentForwarding,
                server.SshX11Forwarding,
                targetPort,
                target,
                hostKeyArg);
            Core.Logging.FileLogger.Info($"Launching PuTTY: {puttyPath} for {targetHost}:{targetPort}");

            if (server.SshX11Forwarding)
            {
                await _x11ServerManager.EnsureRunningAsync().ConfigureAwait(false);
            }

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
                        ReleaseTunnelIfNeeded(usesTunnel, targetPort);
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
            ReleaseTunnelIfNeeded(releaseTunnel, targetPort);
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
        CancellationToken ct)
    {
        bool releaseTunnel = usesTunnel;
        try
        {
            string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
            if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath))
            {
                string msg = originalFailure is not null
                    ? _localizer.Format(SshLocalizationKeys.ErrorPlinkNotConfiguredWithReason, originalFailure)
                    : _localizer[SshLocalizationKeys.ErrorPlinkNotConfigured];
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

            // Attested before the file exists: whether an early deletion is allowed is a property
            // of the launcher, decided once, and never re-derived from anything it prints.
            bool firstByteProvesConsumption = _plinkFirstByteAttestation(plinkPath);

            string? passwordFilePath = CreatePlinkPasswordFile(
                ConnectionHelpers.DecryptPassword(server.SshPasswordEncrypted));
            if (ShouldPromptForPlinkPassword(passwordFilePath, server.SshKeyPath))
            {
                string? promptedPassword = await _dialogService.ShowPasswordInputAsync(
                        _localizer["DialogSshPasswordPromptTitle"],
                        _localizer.Format("DialogSshPasswordPromptMessage", target),
                        ct)
                    .ConfigureAwait(false);

                passwordFilePath = CreatePlinkPasswordFile(promptedPassword);
                if (passwordFilePath is null)
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

            string args = BuildPipeModeArguments(
                server.SshKeyPath,
                server.SshCompression,
                server.SshAgentForwarding,
                server.SshX11Forwarding,
                targetPort,
                target,
                hostKeyArg,
                passwordFilePath);

            Heimdall.Terminal.PipeModeSession terminalSession = new Heimdall.Terminal.PipeModeSession();
            Core.Logging.FileLogger.Info(
                $"SSH via Plink ({terminalSession.GetType().Name}) using {plinkPath} for {targetHost}:{targetPort}");

            PlinkPasswordFileReleaseHandle? passwordFileRelease = null;
            if (!string.IsNullOrEmpty(passwordFilePath))
            {
                // Armed before the process starts so no output can arrive unobserved. Every path
                // that frees this file goes through the returned handle, including the catches
                // below: disposing the session can itself raise ProcessExited, so a direct delete
                // there would be a second invocation.
                passwordFileRelease = PlinkPasswordFileRelease.Arm(
                    terminalSession,
                    passwordFilePath,
                    _deletePlinkPasswordFile,
                    firstByteProvesConsumption);
            }

            try
            {
                await terminalSession.StartAsync(plinkPath, args, cancellationToken: ct)
                    .ConfigureAwait(false);
                Core.Logging.FileLogger.Info($"Plink SSH session started: PID={terminalSession.ProcessId}");
                passwordFilePath = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                terminalSession.Dispose();
                passwordFileRelease?.Release();
                throw;
            }
            catch (Exception ex)
            {
                terminalSession.Dispose();
                passwordFileRelease?.Release();
                Core.Logging.FileLogger.Error("Plink SSH launch failed", ex);
                _connectionSm.SetError(server.Id, ex.Message);
                return new ConnectionResult(
                    false,
                    ex.Message,
                    null,
                    SshSessionDiagnosticFactory.CreatePipeModeFailure(ex.Message));
            }

            _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
            releaseTunnel = false;
            return new ConnectionResult(true, null, new TerminalSessionResult(
                terminalSession,
                BuildDisplayEndpoint(server),
                server.SessionLoggingOverride));
        }
        finally
        {
            ReleaseTunnelIfNeeded(releaseTunnel, targetPort);
        }
    }

    private static string BuildDisplayEndpoint(ServerProfileDto server)
    {
        var user = string.IsNullOrWhiteSpace(server.SshUsername) ? "?" : server.SshUsername;
        var host = string.IsNullOrWhiteSpace(server.RemoteServer) ? "?" : server.RemoteServer;
        var port = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        return $"{user}@{host}:{port}";
    }

    internal static bool ShouldPromptForPlinkPassword(string? passwordFilePath, string? keyPath)
    {
        return string.IsNullOrEmpty(passwordFilePath) && string.IsNullOrWhiteSpace(keyPath);
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
        string? passwordFilePath = null)
    {
        var argParts = new List<string> { "-ssh", "-t", "-no-antispoof" };
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            argParts.Add("-i");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(keyPath)}\"");
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
