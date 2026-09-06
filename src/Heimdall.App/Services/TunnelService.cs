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
        return new TunnelSetupOutcome(true, true, localBindHost, localPort, null, null)
        {
            ReusedExistingTunnel = tunnelResult.ReusedExistingTunnel,

            // Read off the tunnel, not off the attempt. Every opening path builds its tunnel
            // record with the route already on it, and a reuse hands back a copy of that record,
            // so this one read covers a fresh SSH.NET tunnel, a Plink fallback and a reuse of
            // either. An attempt-level field would need writing on each of those paths, and the
            // two that were missed were missed exactly that way.
            GatewayRoute = tunnelResult.Tunnel?.GatewayRoute,
        };
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

        // Composed here, from the settings instance the chain above was resolved from, because
        // this is the instant the route is true at. Asking again later reads a fresh clone of the
        // gateway list and so answers with every edit made since - which named the wrong city in
        // a certificate question during a slow establishment.
        string? resolvedRoute = RdpTrustPromptRoute.Describe(false, gatewayId, settings.SshGateways);

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

            // The route this tunnel was OPENED through, not the one just resolved. The reuse key
            // hashes gateway identifiers and an edit leaves those alone, so the tunnel handed
            // back here can have been dialled from an older settings instance, through a gateway
            // host that has since been changed - or by another profile entirely.
            //
            // Null when nothing recorded that opening, which is a tunnel this process did not
            // open. The question then shows no route line, as it did for every reuse before the
            // opening was recorded at all.
            return new TunnelResult(true, existing, null, null)
            {
                ReusedExistingTunnel = true,
            };
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

        // Read once, here, because this is the last statement before the chain is
        // dialled. Every sentence composed after the refusal comes back is a claim
        // about what was offered, and the agents keep changing while a refusal is
        // in flight: a key loaded during a wrong-password timeout would otherwise
        // be counted as a key this dial presented. For one hop this is the instant
        // of that hop's dial. For a chain it is the instant of the FIRST hop's,
        // which AppendAuthFailureContext states and bounds.
        SshAgentObservation agentAtDial = agentRegistry.Observe();

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
                    localBindHost: localBindHost,
                    gatewayRoute: resolvedRoute)
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
                    localBindHost: localBindHost,
                    gatewayRoute: resolvedRoute)
                .ConfigureAwait(false);
        }

        // Sentences the manager composed itself travel as locale keys; formatted here,
        // once, before anything below reads or appends to the message.
        result = TunnelFailureMessageResolver.Localize(result, _localizer);

        // A refused sign-in on a single gateway. Both routes out of here used
        // to compose a message of their own and return, so on the one machine
        // state where they fire - an SSH agent actually running - the gateway's
        // own sentence was replaced by a sentence about Heimdall's fallback.
        // The refusal is composed once, first, and every route below appends to
        // it. Every route also reads the same agent observation, taken before
        // the dial, so one agent state decides the whole message - the count it
        // quotes, and whether a Plink retry was attempted at all.
        if (!result.Success
            && chain.Count == 1
            && result.FailureCode is SshFailureCode.AuthRejected
                or SshFailureCode.KeyRejected
                or SshFailureCode.PassphraseRejected)
        {
            string? refusedByServer = result.ErrorMessage;
            TunnelResult refusal = AppendAuthFailureContext(result, chain, agentAtDial);
            string refusalMessage = refusal.ErrorMessage
                ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed];

            if (agentAtDial.HasPlinkCompatibleAgent)
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
                        resolvedRoute,
                        preferDistinctLoopback,
                        refusalMessage,
                        socksProxyPort,
                        remoteBindPort)
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

            if (agentAtDial.HasAnyNonPlinkAgent)
            {
                // Why no fallback was attempted. It is an observation about
                // Heimdall, appended last, and it names no cause for the
                // gateway's refusal.
                string unusableAgentMessage = SshAuthFailureMessageComposer.AppendSentence(
                    refusalMessage,
                    _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported],
                    SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported)
                    ?? refusalMessage;
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
            int establishedLocalPort = result.Tunnel?.LocalPort ?? localPort;
            await WaitForTunnelEstablishmentOrReleaseAsync(
                    _tunnelManager,
                    establishedLocalPort,
                    settings.TunnelEstablishmentDelayMs,
                    _timeProvider,
                    ct)
                .ConfigureAwait(false);

            Core.Logging.FileLogger.Info($"Tunnel established for {serverId} on port {establishedLocalPort}");
            _connectionSm.SetTunnelInfo(serverId, establishedLocalPort, 0);
            _connectionSm.TryTransition(serverId, Core.Models.ConnectionState.TunnelEstablished);

            // Nothing is stamped here. The route was set when the tunnel record was built, which
            // is the only instant every opening path passes through: this block runs AFTER the
            // establishment delay, and the tunnel is registered and reusable before it, so a
            // connection reusing it during that window read a route this line had not written
            // yet. The successful Plink fallback never reaches this block at all.
        }
        else
        {
            string? relayedMessage = result.ErrorMessage;
            result = AppendAuthFailureContext(result, chain, agentAtDial);
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
    /// fact the user cannot see - how many keys an agent had loaded when the
    /// chain was dialled - and claims nothing about the cause.
    /// <para>
    /// The count comes from <paramref name="agentAtDial"/>, read before the
    /// chain is dialled. Reading the agents here instead would quote the state
    /// they are in after the refusal has travelled back, which is a different
    /// instant and can be a different number.
    /// </para>
    /// <para>
    /// <b>That instant is the refusing gateway's own dial for a single hop only.</b>
    /// <c>TunnelManager</c> builds each hop's client as it reaches it, and
    /// <c>SshConnectionFactory</c> takes the agent identities from the registry at
    /// the moment each hop's connection is built, so a refusal from the third hop is
    /// described with a reading taken before the first: a key loaded while the earlier
    /// hops were negotiating WAS offered to the hop that refused, and is not counted
    /// here. The sentence the user then reads is early by however long those hops
    /// took, which is the honest residue of moving the reading before the dial rather
    /// than after the refusal. Making it exact for chains means observing per hop,
    /// which means threading the registry through <c>TunnelManager</c>. The gap is
    /// named here rather than closed in prose.
    /// </para>
    /// </summary>
    private TunnelResult AppendAuthFailureContext(
        TunnelResult result,
        IReadOnlyList<SshConnectionParams> chain,
        SshAgentObservation agentAtDial)
    {
        if (result.Success || !SshAuthFailureDiagnoser.IsAuthRejection(result.FailureCode))
        {
            return result;
        }

        return result with
        {
            ErrorMessage = SshAuthFailureMessageComposer.AppendAgentObservation(
                _localizer,
                result.ErrorMessage,
                chain,
                agentAtDial.OfferableIdentityCount)
        };
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
        string message = TunnelFailureMessageResolver.ResolvePreflightMessage(preflight.Result, _localizer);
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
    /// <param name="socksProxyPort">
    /// SOCKS proxy port the profile needs, or <c>0</c>. Plink is launched with
    /// a local forward only, so a profile that needs one is refused here rather
    /// than handed a tunnel that silently lacks it.
    /// </param>
    /// <param name="remoteBindPort">
    /// Remote reverse-forward bind port the profile needs, or <c>0</c>. Refused
    /// for the same reason as <paramref name="socksProxyPort"/>.
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
        string? gatewayRoute,
        bool preferDistinctLoopback = false,
        string? precedingRefusal = null,
        int socksProxyPort = 0,
        int remoteBindPort = 0)
    {
        TunnelResult Refuse(string message, SshFailureCode? code, string? messageKey = null)
        {
            string composed = SshAuthFailureMessageComposer.AppendSentence(
                precedingRefusal ?? string.Empty,
                message,
                messageKey)
                ?? string.Empty;
            _connectionSm.SetError(serverId, composed);
            return new TunnelResult(false, null, composed, code);
        }

        // Before anything is looked up or launched: the fallback builds a
        // plink command line with a single -L, so it cannot serve a profile
        // that needs a SOCKS proxy or a reverse forward. Returning a tunnel
        // without them reported success for a proxy that did not exist, and
        // registered it under a reuse identity (0, 0) that no later attempt
        // for the same profile could match.
        if (socksProxyPort > 0 || remoteBindPort > 0)
        {
            Core.Logging.FileLogger.Warn(
                $"Plink fallback refused for {serverId}: the profile needs socks={socksProxyPort} remoteBind={remoteBindPort}, which plink is not launched with.");
            return Refuse(
                _localizer[SshLocalizationKeys.ErrorPlinkForwardingModeUnsupported],
                SshFailureCode.ForwardingFailed,
                SshLocalizationKeys.ErrorPlinkForwardingModeUnsupported);
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
        result = TunnelFailureMessageResolver.Localize(result, _localizer);

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

        // Through the same builder the SSH.NET paths use. This record used to be written out by
        // hand here, which is how it came to be the one opening path with no route on it: a field
        // added to the builder simply did not exist in this copy, and no test that covered the
        // builder could see the difference.
        TunnelInfo tunnelInfo = TunnelManager.BuildTunnelInfo(
            gatewayParams.Host,
            localPort,
            remoteHost,
            remotePort,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayChainKey: gatewayChainKey,
            localBindHost: localBindHost,
            gatewayRoute: gatewayRoute);

        if (!_tunnelManager.TryRegisterExternalTunnel(tunnelInfo, runner, () => runner.IsRunning))
        {
            runner.Dispose();
            return Refuse(
                _localizer[SshLocalizationKeys.ErrorTunnelPortConcurrent],
                SshFailureCode.PortInUse,
                SshLocalizationKeys.ErrorTunnelPortConcurrent);
        }

        await WaitForTunnelEstablishmentOrReleaseAsync(
                _tunnelManager,
                localPort,
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

    /// <summary>
    /// Waits out the establishment delay on a tunnel that is already registered
    /// and referenced, and releases that reference if the wait is cancelled.
    /// </summary>
    /// <remarks>
    /// Every opening path registers its tunnel, with one reference, before this
    /// wait, and tells the connection state the port only after it. A
    /// cancellation in between used to leave a tunnel nobody knew the port of:
    /// the orphan cleanup on close found nothing to release, and the SSH.NET or
    /// plink tunnel stayed open until the application exited or the user closed
    /// it by hand from the tunnels list.
    /// </remarks>
    /// <param name="tunnelManager">Registry holding the tunnel's reference.</param>
    /// <param name="localPort">Local port the tunnel was registered under.</param>
    /// <param name="delayMs">Establishment delay, in milliseconds; non-positive means none.</param>
    /// <param name="timeProvider">Clock the delay is measured on.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    internal static async Task WaitForTunnelEstablishmentOrReleaseAsync(
        TunnelManager tunnelManager,
        int localPort,
        int delayMs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tunnelManager);

        try
        {
            await WaitForTunnelEstablishmentAsync(delayMs, timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Core.Logging.FileLogger.Info(
                $"Tunnel establishment on port {localPort} was cancelled; releasing the tunnel reference.");
            tunnelManager.ReleaseReference(localPort);
            throw;
        }
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

}
