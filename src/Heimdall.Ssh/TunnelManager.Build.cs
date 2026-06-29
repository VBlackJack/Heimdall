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

using System.Net.Sockets;
using Heimdall.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh;

public sealed partial class TunnelManager
{
    private const int DefaultMaxStartAttempts = 3;
    private const int DefaultRetryDelayMs = 50;

    private static async Task<PinnedFingerprintVerifier> ResolvePinnedVerifierAsync(
        SshConnectionParams connectionParams,
        string verificationHost,
        int verificationPort,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier verifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(verifier);

        return await SshConnectionFactory.ResolveHostKeyAsync(
                connectionParams,
                verificationHost,
                verificationPort,
                hostKeyStore,
                verifier,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ConnectSshClientWithCancellationAsync(
        SshClient client,
        string verificationHost,
        int verificationPort,
        PinnedFingerprintVerifier pinnedVerifier,
        CancellationToken cancellationToken,
        string cancelLogMessage)
    {
        SshConnectionFactory.AttachPinnedHostKeyVerification(
            client,
            verificationHost,
            verificationPort,
            pinnedVerifier);

        await using var connectReg = cancellationToken.Register(
            () =>
            {
                try { client.Disconnect(); }
                catch (Exception ex) { Core.Logging.FileLogger.Debug(cancelLogMessage, ex); }
            });

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Connect();
        }, cancellationToken).ConfigureAwait(false);
    }

    private int WireFinalForwardedPorts(
        TunnelBuildContext context,
        string remoteHost,
        int remotePort,
        int localPort,
        int socksProxyPort,
        int remoteBindPort,
        int remoteLocalPort,
        bool isChained,
        string localBindHost)
    {
        var finalClient = context.FinalClient
            ?? throw new InvalidOperationException("Final SSH client must be connected before wiring forwarded ports.");

        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);
        context.FinalPort = new ForwardedPortLocal(localBindHost, (uint)localPort, remoteHost, (uint)remotePort);
        context.FinalPort.Exception += (_, args) =>
        {
            int reportedLocalPort = ResolveStartedLocalPortOrDefault(context.FinalPort, localPort);
            Core.Logging.FileLogger.Error(
                $"SSH forwarded port {reportedLocalPort} -> {remoteHost}:{remotePort} "
                + $"exception: {args.Exception.Message}");
            RaiseForwardedPortFailed(new TunnelForwardedPortFailure(
                reportedLocalPort,
                remoteHost,
                remotePort,
                args.Exception.Message,
                DateTimeOffset.UtcNow));
        };

        finalClient.AddForwardedPort(context.FinalPort);
        StartForwardedPortWithRetry(context.FinalPort, DescribeLocalPort(localPort));
        int boundLocalPort = ResolveStartedLocalPort(context.FinalPort, localPort);

        var logSuffix = isChained ? " (chained tunnel)" : string.Empty;
        if (socksProxyPort > 0)
        {
            context.DynamicPort = new ForwardedPortDynamic(LoopbackBinding.DefaultHost, (uint)socksProxyPort);
            finalClient.AddForwardedPort(context.DynamicPort);
            StartForwardedPortWithRetry(context.DynamicPort, $"SOCKS5 port {socksProxyPort}");
            Core.Logging.FileLogger.Info(
                $"SOCKS5 proxy started on {LoopbackBinding.DefaultHost}:{socksProxyPort}{logSuffix}");
        }

        if (remoteBindPort > 0)
        {
            int localFwd = remoteLocalPort > 0 ? remoteLocalPort : remoteBindPort;
            context.RemotePortForward = new ForwardedPortRemote(
                LoopbackBinding.DefaultHost, (uint)remoteBindPort,
                LoopbackBinding.DefaultHost, (uint)localFwd);
            finalClient.AddForwardedPort(context.RemotePortForward);
            context.RemotePortForward.Start();
            Core.Logging.FileLogger.Info(
                $"Remote forward started: server:{remoteBindPort} \u2192 local:{localFwd}{logSuffix}");
        }

        return boundLocalPort;
    }

    private static string DescribeLocalPort(int localPort)
        => localPort > 0 ? $"local port {localPort}" : "OS-assigned local port";

    private static int ResolveStartedLocalPort(ForwardedPortLocal port, int requestedLocalPort)
    {
        uint boundPort = port.BoundPort;
        if (boundPort > 0)
        {
            return checked((int)boundPort);
        }

        if (requestedLocalPort > 0)
        {
            return requestedLocalPort;
        }

        throw new InvalidOperationException("SSH.NET did not report a bound local port after starting the forward.");
    }

    private static int ResolveStartedLocalPortOrDefault(ForwardedPortLocal port, int requestedLocalPort)
    {
        try
        {
            return ResolveStartedLocalPort(port, requestedLocalPort);
        }
        catch (InvalidOperationException)
        {
            return requestedLocalPort;
        }
    }

    private static TunnelInfo BuildTunnelInfo(
        string gatewayHost,
        int localPort,
        string remoteHost,
        int remotePort,
        int socksProxyPort,
        int remoteBindPort,
        string? label = null,
        string? gatewayChainKey = null,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        return new TunnelInfo(
            gatewayHost,
            localPort,
            remoteHost,
            remotePort,
            DateTime.UtcNow,
            IsAlive: true)
        {
            SocksProxyPort = socksProxyPort,
            RemoteBindPort = remoteBindPort,
            LocalBindHost = LoopbackBinding.NormalizeHost(localBindHost),
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            GatewayChainKey = gatewayChainKey ?? string.Empty
        };
    }

    private TunnelResult RegisterTunnelSession(
        TunnelSession session,
        int localPort,
        TunnelInfo info)
    {
        bool registered = false;

        lock (_registryLock)
        {
            if (!IsPortTracked(localPort) && _activeTunnels.TryAdd(localPort, session))
            {
                AddReferenceUnderLock(localPort);
                registered = true;
            }
            else
            {
                ReleaseLoopbackAliasReservationIfUnboundUnderLock(info.LocalBindHost);
            }
        }

        if (!registered)
        {
            session.Dispose();
            return new TunnelResult(false, null, $"Local port {localPort} was claimed concurrently.", SshFailureCode.PortInUse);
        }

        RaiseTunnelOpened(info);
        return new TunnelResult(true, info, null, null);
    }

    internal static TunnelResult ClassifyAndBuildFailureResult(
        Exception ex,
        Action cleanup,
        bool isChained)
    {
        cleanup();

        if (HostKeyRejectionFinder.TryFind(ex, out HostKeyRejectedException? rejectedHostKey))
        {
            return new TunnelResult(
                false,
                null,
                rejectedHostKey.Message,
                rejectedHostKey.IsMismatch ? SshFailureCode.HostKeyMismatch : SshFailureCode.Cancelled);
        }

        return ex switch
        {
            OperationCanceledException => new TunnelResult(
                false,
                null,
                isChained ? "Chained tunnel establishment was cancelled." : "Tunnel establishment was cancelled.",
                SshFailureCode.Cancelled),
            HostKeyRejectedException directHostKeyRejected => new TunnelResult(
                false,
                null,
                directHostKeyRejected.Message,
                directHostKeyRejected.IsMismatch ? SshFailureCode.HostKeyMismatch : SshFailureCode.Cancelled),
            SshAuthenticationException authEx => new TunnelResult(
                false,
                null,
                authEx.Message,
                SshFailureCode.AuthRejected),
            SocketException socketEx => new TunnelResult(
                false,
                null,
                socketEx.Message,
                ClassifySocketException(socketEx)),
            SshConnectionException connectionEx when !isChained => new TunnelResult(
                false,
                null,
                connectionEx.Message,
                SshFailureCode.NetworkRefused),
            SshException sshEx when !isChained && sshEx.Message.Contains("port", StringComparison.OrdinalIgnoreCase) => new TunnelResult(
                false,
                null,
                sshEx.Message,
                SshFailureCode.PortInUse),
            _ => new TunnelResult(false, null, ex.Message, SshFailureCode.Unknown)
        };
    }

    private static SshConnectionParams CreateLoopbackHopParams(
        SshConnectionParams nextGateway,
        int intermediateLocalPort)
    {
        return new SshConnectionParams
        {
            Host = LoopbackBinding.DefaultHost,
            Port = intermediateLocalPort,
            Username = nextGateway.Username,
            KeyPath = nextGateway.KeyPath,
            Password = nextGateway.Password,
            KeyPassphrase = nextGateway.KeyPassphrase,
            SshAgentPreference = nextGateway.SshAgentPreference,
            UseLegacyPasswordAsKeyPassphrase = nextGateway.UseLegacyPasswordAsKeyPassphrase,
            LegacyCredentialName = nextGateway.LegacyCredentialName,
            AgentForwarding = nextGateway.AgentForwarding,
            Compression = nextGateway.Compression,
            ConnectTimeout = nextGateway.ConnectTimeout
        };
    }

    internal static void StartForwardedPortWithRetry(
        ForwardedPort port,
        string logContext,
        int maxAttempts = DefaultMaxStartAttempts,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(port);

        ExecuteStartWithRetry(
            port.Start,
            logContext,
            maxAttempts,
            retryDelay ?? TimeSpan.FromMilliseconds(DefaultRetryDelayMs));
    }

    internal static void ExecuteStartWithRetry(
        Action startAction,
        string logContext,
        int maxAttempts,
        TimeSpan retryDelay,
        Action<TimeSpan>? sleep = null)
    {
        ArgumentNullException.ThrowIfNull(startAction);

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Maximum attempts must be at least 1.");
        }

        sleep ??= Thread.Sleep;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                startAction();
                return;
            }
            catch (SocketException ex) when (IsLocalBindAlreadyInUse(ex) && attempt < maxAttempts)
            {
                Core.Logging.FileLogger.Info(
                    $"Forwarded port {logContext} bind failed, retrying (attempt {attempt}/{maxAttempts}): {ex.Message}");
                sleep(retryDelay);
            }
        }
    }

    private static bool IsLocalBindAlreadyInUse(SocketException ex)
    {
        return ex.SocketErrorCode == SocketError.AddressAlreadyInUse;
    }

    /// <summary>Classifies a SocketException into a structured failure code.</summary>
    private static SshFailureCode ClassifySocketException(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => SshFailureCode.NetworkRefused,
            SocketError.TimedOut => SshFailureCode.NetworkTimedOut,
            SocketError.HostNotFound or SocketError.HostUnreachable => SshFailureCode.NetworkUnreachable,
            SocketError.AddressAlreadyInUse => SshFailureCode.PortInUse,
            _ => SshFailureCode.Unknown
        };
    }

    private static void SafeDispose(IDisposable? resource, string logMessage)
    {
        if (resource is null) { return; }
        try { resource.Dispose(); }
        catch (Exception ex) { Core.Logging.FileLogger.Debug(logMessage, ex); }
    }

    internal sealed class TunnelBuildContext
    {
        public List<SshClient> IntermediateClients { get; } = [];
        public List<ForwardedPortLocal> IntermediatePorts { get; } = [];
        public SshClient? FinalClient { get; set; }
        public ForwardedPortLocal? FinalPort { get; set; }
        public ForwardedPortDynamic? DynamicPort { get; set; }
        public ForwardedPortRemote? RemotePortForward { get; set; }

        public TunnelSession CreateSession(TunnelInfo info)
        {
            return new TunnelSession(
                FinalClient ?? throw new InvalidOperationException("Final SSH client was not created."),
                FinalPort ?? throw new InvalidOperationException("Final forwarded port was not created."),
                info,
                IntermediateClients,
                IntermediatePorts)
            { DynamicPort = DynamicPort, RemotePort = RemotePortForward };
        }

        public void Cleanup()
        {
            SafeDispose(DynamicPort, "Dynamic port dispose suppressed");
            SafeDispose(RemotePortForward, "Remote port forward dispose suppressed");
            CleanupPortAndClient(FinalPort, FinalClient, "final");

            for (int i = IntermediatePorts.Count - 1; i >= 0; i--)
            {
                CleanupPort(IntermediatePorts[i], $"intermediate port[{i}]");
            }

            for (int i = IntermediateClients.Count - 1; i >= 0; i--)
            {
                CleanupClient(IntermediateClients[i], $"intermediate client[{i}]");
            }
        }

        private static void CleanupPortAndClient(
            ForwardedPortLocal? port,
            SshClient? client,
            string label)
        {
            CleanupPort(port, $"{label} port");
            CleanupClient(client, $"{label} client");
        }

        private static void CleanupPort(ForwardedPortLocal? port, string label)
        {
            try
            {
                if (port is { IsStarted: true })
                {
                    port.Stop();
                }

                port?.Dispose();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"[TunnelManager] Cleanup {label}: {ex.Message}");
            }
        }

        private static void CleanupClient(SshClient? client, string label)
        {
            try
            {
                if (client is { IsConnected: true })
                {
                    client.Disconnect();
                }

                client?.Dispose();
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn($"[TunnelManager] Cleanup {label}: {ex.Message}");
            }
        }
    }

    private sealed class ExternalTunnelSession : IDisposable
    {
        private readonly IDisposable _tunnelHandle;
        private readonly Func<bool> _isAlive;

        public ExternalTunnelSession(TunnelInfo info, IDisposable tunnelHandle, Func<bool> isAlive)
        {
            Info = info;
            _tunnelHandle = tunnelHandle;
            _isAlive = isAlive;
        }

        public TunnelInfo Info { get; }

        public bool IsAlive
        {
            get
            {
                try
                {
                    return _isAlive();
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Warn($"[TunnelManager.ExternalTunnelSession] IsAlive: {ex.Message}");
                    return false;
                }
            }
        }

        public void Dispose()
        {
            _tunnelHandle.Dispose();
        }
    }
}
