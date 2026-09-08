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


using System.Windows;
using Heimdall.App.Localization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;

namespace Heimdall.App.Services;

/// <summary>Opens the complete configured gateway route for a network tool.</summary>
internal static class ToolGatewayConnector
{
    /// <summary>
    /// Connects without blocking the tool UI. The returned client owns the complete
    /// parent route; disposing it releases the forwarding ports, sessions and keys.
    /// Every hop must already have a trusted host key.
    /// </summary>
    public static async Task<SshClient> ConnectAsync(SshGatewayDto gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ct.ThrowIfCancellationRequested();
        IServiceProvider services = (Application.Current as App)?.Services
            ?? throw new InvalidOperationException("Tool gateway services are unavailable; refusing an unverified connection.");
        IConfigManager config = services.GetRequiredService<IConfigManager>();
        IHostKeyTrustService trust = services.GetRequiredService<IHostKeyTrustService>();
        LocalizationManager localizer = services.GetRequiredService<LocalizationManager>();
        AppSettings settings = await config.LoadSettingsAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Local agent/key preparation can perform synchronous I/O too. Keep all of it
        // off the dispatcher, while the transport itself observes the cancellation token.
        return await Task.Run(async () =>
        {
            List<SshGatewayDto> gateways = GatewayChainResolver.ResolveChainDtos(gateway.Id, settings.SshGateways);
            List<SshConnectionParams> chain = GatewayChainResolver.ToConnectionParams(
                gateways, CredentialProtector.Unprotect, settings.SshAgentPreference);
            return await ConnectCoreAsync(chain, trust, localizer, settings.SshKeepAliveIntervalSeconds, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    internal sealed record ParentRoute(string Host, int Port, IDisposable Lifetime);
    internal delegate Task<ParentRoute> OpenParentRoute(
        IReadOnlyList<SshConnectionParams> parents, SshConnectionParams target,
        IHostKeyVerifier verifier, int keepAliveSeconds, CancellationToken ct);

    internal static async Task<SshClient> ConnectCoreAsync(
        IReadOnlyList<SshConnectionParams> chain, IHostKeyTrustService trust,
        LocalizationManager localizer, int keepAliveSeconds, CancellationToken ct,
        OpenParentRoute? openParents = null,
        Func<SshConnectionParams, IDisposable?, SshClient>? createClient = null,
        Func<SshClient, CancellationToken, Task>? connectClient = null)
    {
        ct.ThrowIfCancellationRequested();
        if (chain.Count == 0) throw new ArgumentException("A gateway route cannot be empty.", nameof(chain));
        Dictionary<(string Host, int Port), string> fingerprints = [];
        foreach (SshConnectionParams hop in chain)
        {
            string fingerprint = trust.GetEffectiveEntry(hop.Host, hop.Port)?.Fingerprint
                ?? throw new InvalidOperationException(localizer.Format("ErrorGatewayHostKeyNotTrusted", hop.Host, hop.Port));
            fingerprints[(hop.Host, hop.Port)] = fingerprint;
        }
        ChainPreflightResult preflight = AuthPreflightChecker.CheckChain(chain, isTunnelMode: true);
        if (!preflight.Result.Success)
        {
            throw new InvalidOperationException(TunnelFailureMessageResolver.ResolvePreflightMessage(preflight.Result, localizer));
        }
        openParents ??= (parents, target, verifier, interval, token) =>
            OpenParentsAsync(parents, target, verifier, interval, token, localizer);
        createClient ??= (parameters, route) => route is null
            ? SshConnectionFactory.CreateSshClient(parameters)
            : SshConnectionFactory.CreateSshClient(parameters, route);
        connectClient ??= (client, token) => client.ConnectAsync(token);
        SshConnectionParams target = chain[^1];
        ParentRoute? parentRoute = null;
        SshClient? client = null;
        try
        {
            SshConnectionParams dial = target;
            if (chain.Count > 1)
            {
                parentRoute = await openParents(chain.Take(chain.Count - 1).ToArray(), target,
                    new RouteVerifier(fingerprints), keepAliveSeconds, ct).ConfigureAwait(false);
                dial = new SshConnectionParams
                {
                    Host = parentRoute.Host,
                    Port = parentRoute.Port,
                    LogicalHost = target.Host,
                    LogicalPort = target.Port,
                    Username = target.Username,
                    Password = target.Password,
                    KeyPath = target.KeyPath,
                    KeyPassphrase = target.KeyPassphrase,
                    SshAgentPreference = target.SshAgentPreference,
                    UseLegacyPasswordAsKeyPassphrase = target.UseLegacyPasswordAsKeyPassphrase,
                    LegacyCredentialName = target.LegacyCredentialName,
                    ConnectTimeout = target.ConnectTimeout
                };
            }
            ct.ThrowIfCancellationRequested();
            client = createClient(dial, parentRoute?.Lifetime);
            // The factory transfers lifetime ownership only when it returns a client.
            parentRoute = null;
            client.KeepAliveInterval = TimeSpan.FromSeconds(keepAliveSeconds);
            SshConnectionFactory.AttachPinnedHostKeyVerification(client, target.Host, target.Port,
                new PinnedFingerprintVerifier(target.Host, target.Port, fingerprints[(target.Host, target.Port)]));
            await connectClient(client, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Heimdall.Core.Logging.FileLogger.Info(
                $"Tool gateway connected through {chain.Count} hop(s): {target.Host}:{target.Port}");
            return client;
        }
        catch
        {
            client?.Dispose();
            parentRoute?.Lifetime.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<ParentRoute> OpenParentsAsync(
        IReadOnlyList<SshConnectionParams> parents, SshConnectionParams target,
        IHostKeyVerifier verifier, int keepAliveSeconds, CancellationToken ct, LocalizationManager localizer)
    {
        TunnelManager manager = new();
        try
        {
            TunnelResult result = await manager.OpenChainedTunnelAsync(parents,
                target.Host, target.Port, 0, new HostKeyStore(), verifier,
                cancellationToken: ct, keepAliveIntervalSeconds: keepAliveSeconds).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            result = TunnelFailureMessageResolver.Localize(result, localizer);
            if (!result.Success || result.Tunnel is null)
            {
                throw new InvalidOperationException(result.ErrorMessage ?? localizer[SshLocalizationKeys.ErrorTunnelFailed]);
            }
            return new ParentRoute(result.Tunnel.LocalBindHost, result.Tunnel.LocalPort, manager);
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    private sealed class RouteVerifier(IReadOnlyDictionary<(string Host, int Port), string> fingerprints) : IHostKeyVerifier
    {
        public Task<HostKeyDecision> VerifyAsync(string host, int port, string algorithm,
            string presentedFingerprint, string? storedFingerprint, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            bool trusted = fingerprints.TryGetValue((host, port), out string? fingerprint)
                && new PinnedFingerprintVerifier(host, port, fingerprint).Matches(host, port, presentedFingerprint);
            return Task.FromResult(trusted ? HostKeyDecision.TrustOnce : HostKeyDecision.Reject);
        }
    }
}
