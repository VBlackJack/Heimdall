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
using System.Security.Cryptography;
using System.Text;
using Heimdall.App.Localization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services;

/// <summary>
/// Resolves SSH gateway chains and establishes reusable tunnels for protocol handlers.
/// </summary>
public sealed class TunnelService : ITunnelService
{
    private readonly TunnelManager _tunnelManager;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyTrustService _hostKeyTrustService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IPlinkHostKeyProbe _plinkHostKeyProbe;
    private readonly TimeProvider _timeProvider;

    private readonly Func<SshAgentPreference, SshAgentRegistry> _agentRegistryFactory;
    private readonly TunnelFailureLogCoalescer _failureLogCoalescer;
    private readonly TunnelFailureLogWriter _failureLogWriter;

    private AppSettings? _currentSettings;
    private readonly RecentForwardedPortFailureTracker _forwardedPortFailures = new();

    public TunnelService(
        TunnelManager tunnelManager,
        HostKeyStore hostKeyStore,
        IHostKeyTrustService hostKeyTrustService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        IHostKeyVerifier hostKeyVerifier)
        : this(
            tunnelManager,
            hostKeyStore,
            hostKeyTrustService,
            connectionSm,
            localizer,
            hostKeyVerifier,
            new DefaultPlinkHostKeyProbe(),
            TimeProvider.System)
    {
    }

    internal TunnelService(
        TunnelManager tunnelManager,
        HostKeyStore hostKeyStore,
        IHostKeyTrustService hostKeyTrustService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        IHostKeyVerifier hostKeyVerifier,
        IPlinkHostKeyProbe plinkHostKeyProbe,
        TimeProvider? timeProvider = null,
        Func<SshAgentPreference, SshAgentRegistry>? agentRegistryFactory = null,
        TunnelFailureLogCoalescer? failureLogCoalescer = null,
        TunnelFailureLogWriter? failureLogWriter = null)
    {
        _tunnelManager = tunnelManager;
        _hostKeyStore = hostKeyStore;
        _hostKeyTrustService = hostKeyTrustService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _hostKeyVerifier = hostKeyVerifier;
        _plinkHostKeyProbe = plinkHostKeyProbe;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _agentRegistryFactory = agentRegistryFactory ?? SshAgentRegistry.CreateDefault;
        _failureLogCoalescer = failureLogCoalescer ?? new TunnelFailureLogCoalescer();
        _failureLogWriter = failureLogWriter ?? WriteTunnelFailureToFileLog;

        // TunnelService and TunnelManager are both DI singletons living for the
        // application lifetime, so this subscription needs no explicit teardown.
        _tunnelManager.ForwardedPortFailed += _forwardedPortFailures.Record;
    }

    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _currentSettings = settings;
    }

    /// <inheritdoc />
    public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort)
        => _forwardedPortFailures.GetRecent(localPort);

    /// <inheritdoc />
    public void ReleaseTunnelReference(int localPort)
    {
        _tunnelManager.ReleaseReference(localPort);
    }

    /// <summary>
    /// Checks whether the server requires a tunnel and establishes it if needed.
    /// Returns the resolved host and port to connect to.
    /// </summary>
    public async Task<TunnelSetupOutcome>
        SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
    {
        if (server.UseDirectConnection || string.IsNullOrEmpty(server.SshGatewayId))
        {
            return new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, null, null);
        }

        bool useOsAssignedLocalPort = ShouldUseOsAssignedLocalPort(server, settings);
        TunnelResult tunnelResult = await EstablishTunnelAsync(
                server.Id,
                server.SshGatewayId,
                server.RemoteServer,
                remotePort,
                server.LocalPort,
                settings,
                ct,
                server.SocksProxyPort,
                server.RemoteBindPort,
                server.RemoteLocalPort,
                preferDistinctLoopback,
                useOsAssignedLocalPort)
            .ConfigureAwait(false);

        if (!tunnelResult.Success)
        {
            return new TunnelSetupOutcome(false, false, string.Empty, 0, tunnelResult.ErrorMessage, tunnelResult.FailureCode);
        }

        int localPort = tunnelResult.Tunnel?.LocalPort ?? server.LocalPort;

        // A fresh tunnel is live on this port; drop any stale failure recorded
        // for it so it cannot mislabel a later, unrelated disconnect.
        _forwardedPortFailures.Clear(localPort);
        var localBindHost = tunnelResult.Tunnel?.LocalBindHost ?? LoopbackBinding.DefaultHost;
        return new TunnelSetupOutcome(true, true, localBindHost, localPort, null, null);
    }

    /// <summary>
    /// Resolves the gateway chain, performs preflight, and opens a tunnel.
    /// </summary>
    private async Task<TunnelResult> EstablishTunnelAsync(
        string serverId,
        string gatewayId,
        string remoteHost,
        int remotePort,
        int localPort,
        AppSettings settings,
        CancellationToken ct,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int remoteLocalPort = 0,
        bool preferDistinctLoopback = false,
        bool useOsAssignedLocalPort = false)
    {
        Core.Logging.FileLogger.Info(
            $"Establish tunnel: serverId={serverId} gatewayId={gatewayId} target={remoteHost}:{remotePort} requestedPort={localPort}");

        List<SshGatewayDto> chainDtos;
        List<SshConnectionParams> chain;
        string gatewayChainKey;

        try
        {
            chainDtos = GatewayChainResolver.ResolveChainDtos(gatewayId, settings.SshGateways);
            chain = GatewayChainResolver.ToConnectionParams(
                chainDtos,
                ConnectionHelpers.DecryptPassword,
                settings.SshAgentPreference);
            gatewayChainKey = BuildGatewayChainKey(chainDtos);
        }
        catch (GatewayChainException chainEx)
        {
            _connectionSm.SetError(serverId, chainEx.Message);
            return new TunnelResult(false, null, chainEx.Message, chainEx.Code);
        }
        catch (Exception ex)
        {
            _connectionSm.SetError(serverId, ex.Message);
            return new TunnelResult(false, null, ex.Message, SshFailureCode.Unknown);
        }

        TunnelInfo? existing = _tunnelManager.AcquireReusableTunnel(
            gatewayChainKey,
            remoteHost,
            remotePort,
            socksProxyPort,
            remoteBindPort,
            remoteLocalPort);

        if (existing is not null)
        {
            Core.Logging.FileLogger.Info(
                $"Reusing existing tunnel on port {existing.LocalPort} for {serverId}");
            _connectionSm.SetTunnelInfo(serverId, existing.LocalPort, 0);
            _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.EstablishingTunnel);
            _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.TunnelEstablished);
            return new TunnelResult(true, existing, null, null);
        }

        _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.EstablishingTunnel);

        localPort = useOsAssignedLocalPort
            ? 0
            : _tunnelManager.AllocatePort(localPort);
        Core.Logging.FileLogger.Info(
            localPort == 0
                ? "Using OS-assigned tunnel port."
                : $"Allocated tunnel port: {localPort}");

        SshAgentRegistry agentRegistry = _agentRegistryFactory(settings.SshAgentPreference);
        if (chain.Any(hop => hop.AgentForwarding)
            && !agentRegistry.HasPlinkCompatibleAgent()
            && agentRegistry.HasAnyNonPlinkAgent())
        {
            string message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(false, null, message, SshFailureCode.PageantKeyUnavailable);
        }

        // Every hop is checked, not just the root: a hop whose only sign-in
        // source is an agent that offers nothing is doomed before the chain is
        // dialled up to it, and saying so here costs no round trip.
        ChainPreflightResult preflight = AuthPreflightChecker.CheckChain(
            chain,
            isTunnelMode: true,
            agentRegistry);
        if (!preflight.Result.Success)
        {
            string msg = FormatChainPreflightMessage(preflight, chainDtos);
            Core.Logging.FileLogger.Warn(
                $"Tunnel preflight refused {serverId} at gateway hop {preflight.FailedHopIndex + 1}/{chain.Count}: {msg}");
            _connectionSm.SetError(serverId, msg);
            return new TunnelResult(false, null, msg, preflight.Result.FailureCode);
        }

        TunnelResult result;
        string localBindHost;
        try
        {
            localBindHost = preferDistinctLoopback
                ? _tunnelManager.AllocateLoopbackAlias()
                : LoopbackBinding.DefaultHost;
        }
        catch (InvalidOperationException ex)
        {
            string message = _localizer[SshLocalizationKeys.ErrorTunnelNoLoopbackAlias];
            Core.Logging.FileLogger.Warn($"Loopback alias allocation failed for {serverId}: {ex.Message}");
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(false, null, message, SshFailureCode.PortInUse);
        }

        if (chain.Count == 1)
        {
            int keepAlive = _currentSettings?.SshKeepAliveIntervalSeconds ?? AppSettings.DefaultSshKeepAliveIntervalSeconds;
            result = await _tunnelManager.OpenTunnelAsync(
                    chain[0],
                    remoteHost,
                    remotePort,
                    localPort,
                    hostKeyStore: _hostKeyStore,
                    verifier: _hostKeyVerifier,
                    cancellationToken: ct,
                    keepAliveIntervalSeconds: keepAlive,
                    socksProxyPort: socksProxyPort,
                    remoteBindPort: remoteBindPort,
                    remoteLocalPort: remoteLocalPort,
                    gatewayChainKey: gatewayChainKey,
                    localBindHost: localBindHost)
                .ConfigureAwait(false);
        }
        else
        {
            result = await _tunnelManager.OpenChainedTunnelAsync(
                    chain,
                    remoteHost,
                    remotePort,
                    localPort,
                    hostKeyStore: _hostKeyStore,
                    verifier: _hostKeyVerifier,
                    cancellationToken: ct,
                    socksProxyPort: socksProxyPort,
                    remoteBindPort: remoteBindPort,
                    remoteLocalPort: remoteLocalPort,
                    gatewayChainKey: gatewayChainKey,
                    localBindHost: localBindHost)
                .ConfigureAwait(false);
        }

        // A refused sign-in on a single gateway. Both routes out of here used
        // to compose a message of their own and return, so on the one machine
        // state where they fire - an SSH agent actually running - the gateway's
        // own sentence was replaced by a sentence about Heimdall's fallback.
        // The refusal is composed once, first, and every route below appends to
        // it. The registry is the one observed before the dial and the one the
        // diagnosis reads, so a single agent state decides the whole message.
        if (!result.Success
            && chain.Count == 1
            && result.FailureCode is SshFailureCode.AuthRejected
                or SshFailureCode.KeyRejected
                or SshFailureCode.PassphraseRejected)
        {
            string? refusedByServer = result.ErrorMessage;
            TunnelResult refusal = AppendAuthFailureContext(result, chain, agentRegistry);
            string refusalMessage = refusal.ErrorMessage
                ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed];

            if (agentRegistry.HasPlinkCompatibleAgent())
            {
                Core.Logging.FileLogger.Info(
                    $"SSH.NET auth failed, falling back to Plink: {result.ErrorMessage}");

                int plinkLocalPort = useOsAssignedLocalPort
                    ? _tunnelManager.AllocatePort(0)
                    : localPort;
                Core.Logging.FileLogger.Info($"Allocated Plink fallback tunnel port: {plinkLocalPort}");

                // The refusal is handed down rather than composed onto the
                // result here. What the pane shows is the message the state
                // machine holds, and Error -> Error is not a valid transition
                // there: a second SetError after the fallback has set its own
                // is dropped without a word, leaving the pane the fallback's
                // wording and the caller the belief that it composed over it.
                TunnelResult fallback = await EstablishPlinkTunnelAsync(
                        serverId,
                        chain[0],
                        remoteHost,
                        remotePort,
                        plinkLocalPort,
                        settings,
                        gatewayChainKey,
                        ct,
                        preferDistinctLoopback,
                        refusalMessage)
                    .ConfigureAwait(false);

                if (fallback.Success)
                {
                    return fallback;
                }

                ReportTunnelFailure(
                    serverId,
                    gatewayId,
                    gatewayChainKey,
                    fallback,
                    fallback.ErrorMessage ?? refusalMessage,
                    refusedByServer);
                return fallback;
            }

            if (agentRegistry.HasAnyNonPlinkAgent())
            {
                // Why no fallback was attempted. It is an observation about
                // Heimdall, appended last, and it names no cause for the
                // gateway's refusal.
                string unusableAgentMessage = AppendSentence(
                    refusalMessage,
                    _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported],
                    SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported);
                ReportTunnelFailure(
                    serverId,
                    gatewayId,
                    gatewayChainKey,
                    refusal,
                    unusableAgentMessage,
                    refusedByServer);
                _connectionSm.SetError(serverId, unusableAgentMessage);
                return refusal with { ErrorMessage = unusableAgentMessage };
            }
        }

        if (result.Success)
        {
            await WaitForTunnelEstablishmentAsync(
                    settings.TunnelEstablishmentDelayMs,
                    _timeProvider,
                    ct)
                .ConfigureAwait(false);

            int establishedLocalPort = result.Tunnel?.LocalPort ?? localPort;
            Core.Logging.FileLogger.Info($"Tunnel established for {serverId} on port {establishedLocalPort}");
            _connectionSm.SetTunnelInfo(serverId, establishedLocalPort, 0);
            _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.TunnelEstablished);
        }
        else
        {
            string? relayedMessage = result.ErrorMessage;
            result = AppendAuthFailureContext(result, chain, agentRegistry);
            string message = result.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed];
            ReportTunnelFailure(serverId, gatewayId, gatewayChainKey, result, message, relayedMessage);
            _connectionSm.SetError(serverId, message);
        }

        return result;
    }

    /// <summary>
    /// Appends what Heimdall observed locally to the server's own refusal
    /// wording. The transport relays the sentence the gateway sent, which is
    /// true and which nothing here may drop: a wrong stored password and an
    /// unloaded agent key both come back as "Permission denied (password)", and
    /// only the server knows which it was. The appended sentence adds the one
    /// fact the user cannot see - how many keys an agent had loaded - and
    /// claims nothing about the cause.
    /// </summary>
    private TunnelResult AppendAuthFailureContext(
        TunnelResult result,
        IReadOnlyList<SshConnectionParams> chain,
        SshAgentRegistry agentRegistry)
    {
        if (result.Success || !SshAuthFailureDiagnoser.IsAuthRejection(result.FailureCode))
        {
            return result;
        }

        SshAuthFailureDiagnosis diagnosis = SshAuthFailureDiagnoser.Diagnose(chain, agentRegistry);
        string context = _localizer.Format(diagnosis.ContextMessageKey, diagnosis.AgentIdentityCount);

        // A key missing from the catalogue resolves to itself. Showing an
        // identifier would be worse than showing nothing, and worse still than
        // the server's own sentence, so the context is dropped rather than
        // shipped raw. CSharpLocaleKeyCoverageTests is what makes that state
        // visible before a release; this only bounds the damage if it ships.
        if (string.Equals(context, diagnosis.ContextMessageKey, StringComparison.Ordinal))
        {
            return result;
        }

        return result with
        {
            ErrorMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? context
                : $"{result.ErrorMessage} {context}"
        };
    }

    /// <summary>
    /// Joins one more sentence onto a message already composed. The head is
    /// never rewritten: what the gateway said stays first, whatever Heimdall
    /// has to add after it.
    /// <para>
    /// A sentence is dropped when it is empty, when it resolved to its own
    /// locale key - showing an identifier would be worse than showing nothing -
    /// or when the head already carries it.
    /// </para>
    /// </summary>
    private static string AppendSentence(string head, string? sentence, string? sentenceKey = null)
    {
        if (string.IsNullOrWhiteSpace(sentence)
            || (sentenceKey is not null
                && string.Equals(sentence, sentenceKey, StringComparison.Ordinal)))
        {
            return head;
        }

        if (string.IsNullOrWhiteSpace(head))
        {
            return sentence;
        }

        return head.Contains(sentence, StringComparison.Ordinal)
            ? head
            : $"{head} {sentence}";
    }

    /// <summary>
    /// Writes one full diagnosis per (gateway chain, failure kind, message) per
    /// window. Reconnecting a session set produces the same failure once per
    /// profile; the repeats carry no new information and are recorded at Debug,
    /// in full, so the line that mattered stays visible without any text being
    /// lost.
    /// </summary>
    private void ReportTunnelFailure(
        string serverId,
        string gatewayId,
        string gatewayChainKey,
        TunnelResult result,
        string message,
        string? relayedMessage)
    {
        TunnelFailureReportDecision decision =
            _failureLogCoalescer.Evaluate(gatewayChainKey, result.FailureCode, message);

        // The relayed wording is repeated only when the composed message does
        // not already carry it, which it does whenever a context sentence was
        // appended to it.
        string relaySuffix = relayedMessage is not null
            && !message.Contains(relayedMessage, StringComparison.Ordinal)
            ? $" [server reported: {relayedMessage}]"
            : string.Empty;

        if (!decision.ShouldReport)
        {
            // The message is part of the coalescer key, so "identical" is true
            // by construction here. It is written out anyway: a Debug log that
            // asserts sameness without showing the text cannot answer "why did
            // attempt N fail" when the reader only has the file.
            _failureLogWriter(
                false,
                $"Tunnel failed for {serverId} via gateway {gatewayId}, identical to the failure already "
                + $"reported ({result.FailureCode}); repeat {decision.SuppressedRepeats}: {message}{relaySuffix}");
            return;
        }

        string repeatSuffix = decision.SuppressedRepeats > 0
            ? $" [{decision.SuppressedRepeats} identical failure(s) since the previous report were logged at Debug]"
            : string.Empty;

        _failureLogWriter(
            true,
            $"Tunnel failed for {serverId} via gateway {gatewayId}: {message}{relaySuffix}{repeatSuffix}");
    }

    private static void WriteTunnelFailureToFileLog(bool fullReport, string message)
    {
        if (fullReport)
        {
            Core.Logging.FileLogger.Error(message);
        }
        else
        {
            Core.Logging.FileLogger.Debug(message);
        }
    }

    /// <summary>
    /// Localizes a chain pre-flight failure, naming the gateway that failed when
    /// the chain has more than one hop.
    /// </summary>
    private string FormatChainPreflightMessage(
        ChainPreflightResult preflight,
        IReadOnlyList<SshGatewayDto> chainDtos)
    {
        string message = ResolvePreflightMessage(preflight.Result.Message);
        if (chainDtos.Count <= 1
            || preflight.FailedHopIndex < 0
            || preflight.FailedHopIndex >= chainDtos.Count)
        {
            return message;
        }

        SshGatewayDto failedGateway = chainDtos[preflight.FailedHopIndex];
        string gatewayLabel = string.IsNullOrWhiteSpace(failedGateway.Name)
            ? failedGateway.Host
            : failedGateway.Name;

        // Same gateway-scoping convention as FailureClassifier.FormatMessage:
        // the label is data, the separator is punctuation, and neither needs a
        // catalogue entry of its own.
        return $"{gatewayLabel}: {message}";
    }

    /// <param name="precedingRefusal">
    /// What the gateway already said, when this fallback follows a refused
    /// SSH.NET sign-in. Every failure below is appended to it and the composed
    /// text is what reaches the connection state, so the sentence the server
    /// sent stays at the head of the message whatever the fallback runs into.
    /// </param>
    internal async Task<TunnelResult> EstablishPlinkTunnelAsync(
        string serverId,
        SshConnectionParams gatewayParams,
        string remoteHost,
        int remotePort,
        int localPort,
        AppSettings settings,
        string gatewayChainKey,
        CancellationToken ct,
        bool preferDistinctLoopback = false,
        string? precedingRefusal = null)
    {
        TunnelResult Refuse(string message, SshFailureCode? code, string? messageKey = null)
        {
            string composed = AppendSentence(precedingRefusal ?? string.Empty, message, messageKey);
            _connectionSm.SetError(serverId, composed);
            return new TunnelResult(false, null, composed, code);
        }

        string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
        if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath))
        {
            return Refuse(
                _localizer[SshLocalizationKeys.ErrorPlinkNotConfigured],
                SshFailureCode.Unknown,
                SshLocalizationKeys.ErrorPlinkNotConfigured);
        }

        string? storedFingerprint = _hostKeyTrustService.GetEffectiveEntry(gatewayParams.Host, gatewayParams.Port)?.Fingerprint;
        PlinkHostKeyDecision hostKeyDecision = await PlinkHostKeyDecider.DecideAsync(
                gatewayParams.Host,
                gatewayParams.Port,
                gatewayParams.Username,
                plinkPath,
                settings.HostKeyProbeTimeoutMs,
                storedFingerprint,
                _plinkHostKeyProbe,
                _hostKeyVerifier,
                _hostKeyTrustService,
                ct)
            .ConfigureAwait(false);

        if (!hostKeyDecision.ShouldProceed)
        {
            return Refuse(
                BuildPlinkHostKeyFailureMessage(hostKeyDecision),
                hostKeyDecision.FailureCode ?? SshFailureCode.Unknown);
        }

        string? fingerprint = hostKeyDecision.Fingerprint;
        string localBindHost;
        try
        {
            localBindHost = preferDistinctLoopback
                ? _tunnelManager.AllocateLoopbackAlias()
                : LoopbackBinding.DefaultHost;
        }
        catch (InvalidOperationException ex)
        {
            Core.Logging.FileLogger.Warn($"Loopback alias allocation failed for Plink tunnel {serverId}: {ex.Message}");
            return Refuse(
                _localizer[SshLocalizationKeys.ErrorTunnelNoLoopbackAlias],
                SshFailureCode.PortInUse,
                SshLocalizationKeys.ErrorTunnelNoLoopbackAlias);
        }

        PlinkTunnelRunner runner = new PlinkTunnelRunner(
            _currentSettings?.PlinkPortCheckIntervalMs ?? AppSettings.DefaultPlinkPortCheckIntervalMs,
            _currentSettings?.PlinkKillGracePeriodMs ?? AppSettings.DefaultPlinkKillGracePeriodMs);
        PlinkTunnelResult result = await runner.StartAsync(
                plinkPath,
                gatewayParams.Host,
                gatewayParams.Port,
                gatewayParams.Username,
                gatewayParams.KeyPath,
                gatewayParams.Password,
                remoteHost,
                remotePort,
                localPort,
                fingerprint,
                ct,
                gatewayParams.KeyPassphrase,
                _localizer[SshLocalizationKeys.ErrorPlinkPassphraseUnsupported],
                localBindHost)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _tunnelManager.ReleaseLoopbackAliasReservation(localBindHost);
            string errorMsg = result.FailureCode is SshFailureCode.TunnelPortOwnedByDifferentProcess
                    or SshFailureCode.TunnelPortNotListening
                    or SshFailureCode.TunnelPortOwnershipIndeterminate
                ? _localizer[SshLocalizationKeys.ErrorSshTunnelPortOwnershipUnattested] +
                    $" ({plinkPath})"
                : result.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed];
            Core.Logging.FileLogger.Error(
                $"Plink tunnel failed for {serverId} via {gatewayParams.Host}:{gatewayParams.Port}: {errorMsg}");
            runner.Dispose();
            return Refuse(errorMsg, result.FailureCode);
        }

        TunnelInfo tunnelInfo = new TunnelInfo(
            gatewayParams.Host,
            localPort,
            remoteHost,
            remotePort,
            DateTime.UtcNow,
            IsAlive: true)
        {
            LocalBindHost = localBindHost,
            GatewayChainKey = gatewayChainKey
        };

        if (!_tunnelManager.TryRegisterExternalTunnel(tunnelInfo, runner, () => runner.IsRunning))
        {
            runner.Dispose();
            return Refuse(
                _localizer[SshLocalizationKeys.ErrorTunnelPortConcurrent],
                SshFailureCode.PortInUse,
                SshLocalizationKeys.ErrorTunnelPortConcurrent);
        }

        await WaitForTunnelEstablishmentAsync(
                settings.TunnelEstablishmentDelayMs,
                _timeProvider,
                ct)
            .ConfigureAwait(false);

        _connectionSm.SetTunnelInfo(serverId, localPort, runner.ProcessId ?? 0);
        _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.TunnelEstablished);
        Core.Logging.FileLogger.Info(
            $"Plink tunnel established for {serverId} on port {localPort} (pid={runner.ProcessId?.ToString() ?? "unknown"})");

        return new TunnelResult(true, tunnelInfo, null, null);
    }

    internal static Task WaitForTunnelEstablishmentAsync(
        int delayMs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        return delayMs <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(delayMs), timeProvider, cancellationToken);
    }

    internal static bool ShouldUseOsAssignedLocalPort(ServerProfileDto server, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        if (server.SocksProxyPort > 0 || server.RemoteBindPort > 0)
        {
            return false;
        }

        if (server.LocalPort <= 0)
        {
            return true;
        }

        return server.LocalPort == GetSuggestedTunnelPort(server.ConnectionType, settings);
    }

    private static int GetSuggestedTunnelPort(string? connectionType, AppSettings settings)
    {
        if (string.Equals(connectionType, "RDP", StringComparison.OrdinalIgnoreCase))
        {
            return settings.DefaultRdpTunnelPort;
        }

        if (string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultPorts.WinRmTunnel;
        }

        return settings.DefaultSshTunnelPort;
    }

    internal static string BuildGatewayChainKey(IReadOnlyList<SshGatewayDto> chainDtos)
    {
        ArgumentNullException.ThrowIfNull(chainDtos);
        if (chainDtos.Count == 0)
        {
            return string.Empty;
        }

        using MemoryStream payload = new MemoryStream();
        foreach (SshGatewayDto gateway in chainDtos)
        {
            WriteLengthPrefixedString(payload, gateway.Id ?? string.Empty);
        }

        byte[] hash = SHA256.HashData(payload.ToArray());
        return $"v1:sha256:{Convert.ToBase64String(hash)}";
    }

    private static void WriteLengthPrefixedString(Stream destination, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int length = bytes.Length;

        destination.WriteByte((byte)(length >> 24));
        destination.WriteByte((byte)(length >> 16));
        destination.WriteByte((byte)(length >> 8));
        destination.WriteByte((byte)length);
        destination.Write(bytes, 0, bytes.Length);
    }

    private string BuildPlinkHostKeyFailureMessage(PlinkHostKeyDecision decision)
    {
        if (decision.FailureCode == SshFailureCode.HostKeyMismatch
            && decision.StoredFingerprint is not null
            && decision.PresentedFingerprint is not null)
        {
            return SshFailureMessageBuilder.HostKeyMismatch(
                _localizer,
                decision.StoredFingerprint,
                decision.PresentedFingerprint);
        }

        if (decision.FailureCode == SshFailureCode.Cancelled)
        {
            return SshFailureMessageBuilder.Cancelled(_localizer);
        }

        if (decision.FailureCode == SshFailureCode.HostKeyUnavailable)
        {
            return SshFailureMessageBuilder.HostKeyUnavailable(_localizer);
        }

        return decision.FailureMessageKey is null
            ? _localizer[SshLocalizationKeys.ErrorTunnelFailed]
            : _localizer[decision.FailureMessageKey];
    }

    private string ResolvePreflightMessage(string? messageOrKey)
    {
        if (string.IsNullOrWhiteSpace(messageOrKey))
        {
            return _localizer[SshLocalizationKeys.ErrorPreflightFailed];
        }

        string localized = _localizer[messageOrKey];
        return string.Equals(localized, messageOrKey, StringComparison.Ordinal)
            ? messageOrKey
            : localized;
    }
}
