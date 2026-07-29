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
        TimeProvider? timeProvider = null)
    {
        _tunnelManager = tunnelManager;
        _hostKeyStore = hostKeyStore;
        _hostKeyTrustService = hostKeyTrustService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _hostKeyVerifier = hostKeyVerifier;
        _plinkHostKeyProbe = plinkHostKeyProbe;
        _timeProvider = timeProvider ?? TimeProvider.System;

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
    public async Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)>
        SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
    {
        if (server.UseDirectConnection || string.IsNullOrEmpty(server.SshGatewayId))
        {
            return (true, false, server.RemoteServer, remotePort, null);
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
            return (false, false, string.Empty, 0, tunnelResult.ErrorMessage);
        }

        int localPort = tunnelResult.Tunnel?.LocalPort ?? server.LocalPort;

        // A fresh tunnel is live on this port; drop any stale failure recorded
        // for it so it cannot mislabel a later, unrelated disconnect.
        _forwardedPortFailures.Clear(localPort);
        var localBindHost = tunnelResult.Tunnel?.LocalBindHost ?? LoopbackBinding.DefaultHost;
        return (true, true, localBindHost, localPort, null);
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
        catch (Exception ex)
        {
            _connectionSm.SetError(serverId, ex.Message);
            return new TunnelResult(false, null, ex.Message, SshFailureCode.Unknown);
        }

        IReadOnlyList<TunnelInfo> existingTunnels = _tunnelManager.GetActiveTunnels();
        TunnelInfo? existing = FindReusableTunnel(
            existingTunnels,
            gatewayChainKey,
            remoteHost,
            remotePort,
            socksProxyPort,
            remoteBindPort);

        if (existing is not null)
        {
            Core.Logging.FileLogger.Info(
                $"Reusing existing tunnel on port {existing.LocalPort} for {serverId}");
            _tunnelManager.AddReference(existing.LocalPort);
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

        SshAgentRegistry agentRegistry = SshAgentRegistry.CreateDefault(settings.SshAgentPreference);
        if (chain.Any(hop => hop.AgentForwarding)
            && !agentRegistry.HasPlinkCompatibleAgent()
            && agentRegistry.HasAnyNonPlinkAgent())
        {
            string message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(false, null, message, SshFailureCode.PageantKeyUnavailable);
        }

        PreflightResult preflight = AuthPreflightChecker.Check(chain[0], isTunnelMode: true);
        if (!preflight.Success)
        {
            string msg = ResolvePreflightMessage(preflight.Message);
            _connectionSm.SetError(serverId, msg);
            return new TunnelResult(false, null, msg, preflight.FailureCode);
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

        if (!result.Success
            && chain.Count == 1
            && result.FailureCode is SshFailureCode.AuthRejected
                or SshFailureCode.KeyRejected
                or SshFailureCode.PassphraseRejected
            && SshAgentRegistry.CreateDefault(settings.SshAgentPreference).HasPlinkCompatibleAgent())
        {
            Core.Logging.FileLogger.Info(
                $"SSH.NET auth failed, falling back to Plink: {result.ErrorMessage}");

            int plinkLocalPort = useOsAssignedLocalPort
                ? _tunnelManager.AllocatePort(0)
                : localPort;
            Core.Logging.FileLogger.Info($"Allocated Plink fallback tunnel port: {plinkLocalPort}");

            return await EstablishPlinkTunnelAsync(
                    serverId,
                    chain[0],
                    remoteHost,
                    remotePort,
                    plinkLocalPort,
                    settings,
                    gatewayChainKey,
                    ct,
                    preferDistinctLoopback)
                .ConfigureAwait(false);
        }

        if (!result.Success
            && chain.Count == 1
            && result.FailureCode is SshFailureCode.AuthRejected
                or SshFailureCode.KeyRejected
                or SshFailureCode.PassphraseRejected)
        {
            SshAgentRegistry registry = SshAgentRegistry.CreateDefault(settings.SshAgentPreference);
            if (!registry.HasPlinkCompatibleAgent() && registry.HasAnyNonPlinkAgent())
            {
                string message = _localizer[SshLocalizationKeys.ErrorPlinkOpenSshAgentUnsupported];
                _connectionSm.SetError(serverId, message);
                return new TunnelResult(false, null, message, result.FailureCode);
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
            Core.Logging.FileLogger.Error($"Tunnel failed for {serverId}: {result.ErrorMessage}");
            _connectionSm.SetError(serverId, result.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed]);
        }

        return result;
    }

    internal async Task<TunnelResult> EstablishPlinkTunnelAsync(
        string serverId,
        SshConnectionParams gatewayParams,
        string remoteHost,
        int remotePort,
        int localPort,
        AppSettings settings,
        string gatewayChainKey,
        CancellationToken ct,
        bool preferDistinctLoopback = false)
    {
        string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
        if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath))
        {
            string message = _localizer[SshLocalizationKeys.ErrorPlinkNotConfigured];
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(false, null, message, SshFailureCode.Unknown);
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
            string message = BuildPlinkHostKeyFailureMessage(hostKeyDecision);
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(
                false,
                null,
                message,
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
            string message = _localizer[SshLocalizationKeys.ErrorTunnelNoLoopbackAlias];
            Core.Logging.FileLogger.Warn($"Loopback alias allocation failed for Plink tunnel {serverId}: {ex.Message}");
            _connectionSm.SetError(serverId, message);
            return new TunnelResult(false, null, message, SshFailureCode.PortInUse);
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
            string errorMsg = result.ErrorMessage ?? _localizer[SshLocalizationKeys.ErrorTunnelFailed];
            Core.Logging.FileLogger.Error(
                $"Plink tunnel failed for {serverId} via {gatewayParams.Host}:{gatewayParams.Port}: {errorMsg}");
            _connectionSm.SetError(serverId, errorMsg);
            runner.Dispose();
            return new TunnelResult(false, null, errorMsg, result.FailureCode);
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
            string duplicateMessage = _localizer[SshLocalizationKeys.ErrorTunnelPortConcurrent];
            _connectionSm.SetError(serverId, duplicateMessage);
            return new TunnelResult(false, null, duplicateMessage, SshFailureCode.PortInUse);
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

    internal static TunnelInfo? FindReusableTunnel(
        IReadOnlyList<TunnelInfo> activeTunnels,
        string gatewayChainKey,
        string remoteHost,
        int remotePort,
        int socksProxyPort,
        int remoteBindPort)
    {
        ArgumentNullException.ThrowIfNull(activeTunnels);
        ArgumentNullException.ThrowIfNull(gatewayChainKey);
        ArgumentNullException.ThrowIfNull(remoteHost);

        foreach (TunnelInfo tunnel in activeTunnels)
        {
            if (!tunnel.IsAlive)
            {
                continue;
            }

            if (!string.Equals(tunnel.GatewayChainKey, gatewayChainKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(tunnel.RemoteHost, remoteHost, StringComparison.Ordinal))
            {
                continue;
            }

            if (tunnel.RemotePort != remotePort)
            {
                continue;
            }

            if (tunnel.SocksProxyPort != socksProxyPort)
            {
                continue;
            }

            if (tunnel.RemoteBindPort != remoteBindPort)
            {
                continue;
            }

            return tunnel;
        }

        return null;
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
