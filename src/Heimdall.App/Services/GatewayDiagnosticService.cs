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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>Adapts the dialog snapshot to isolated, pinned diagnostics without saving configuration.</summary>
internal static class GatewayDiagnosticService
{
    public static void Configure(
        GatewayDialogViewModel viewModel,
        AppSettings settings,
        IHostKeyTrustService trust,
        LocalizationManager? localizer = null)
    {
        viewModel.ConfigureDiagnostics(settings.SshGateways, (route, host, port, progress, ct) =>
            Task.Run(async () =>
            {
                // Authentication material is prepared off the dispatcher and never enters report models.
                List<SshConnectionParams> chain = BuildChainParams(route, settings, localizer, ct);
                return await GatewayRouteDiagnostic.RunAsync(chain,
                    hop => trust.GetEffectiveEntry(hop.Host, hop.Port)?.Fingerprint,
                    ResolveStepTimeout(chain),
                    host, port, progress, ct,
                    isRevoked: (hop, pin) => trust.IsRevoked(hop.Host, hop.Port, pin)).ConfigureAwait(false);
            }, ct));
    }

    internal static List<SshConnectionParams> BuildChainParams(
        IReadOnlyList<SshGatewayDto> route,
        AppSettings settings,
        LocalizationManager? localizer,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(settings);

        List<SshConnectionParams> rawChain = GatewayChainResolver.ToConnectionParams(
            route,
            CredentialProtector.Unprotect,
            settings.SshAgentPreference,
            settings.SshCryptoPolicy);

        Action<ISshAgentKey> beforeAgentSign = SshAgentSignConfirmHelper.BuildBeforeSignNotification(
            localizer,
            reportStatus: null);

        return rawChain.Select(hop => new SshConnectionParams
        {
            Host = hop.Host,
            Port = hop.Port,
            LogicalHost = hop.LogicalHost,
            LogicalPort = hop.LogicalPort,
            Username = hop.Username,
            KeyPath = hop.KeyPath,
            CertificatePath = hop.CertificatePath,
            Password = hop.Password,
            KeyPassphrase = hop.KeyPassphrase,
            UseLegacyPasswordAsKeyPassphrase = hop.UseLegacyPasswordAsKeyPassphrase,
            LegacyCredentialName = hop.LegacyCredentialName,
            SshAgentPreference = hop.SshAgentPreference,
            AgentKeyFingerprint = hop.AgentKeyFingerprint,
            DisableAgentAuthentication = hop.DisableAgentAuthentication,
            OnBeforeAgentSign = beforeAgentSign,
            ConfirmAgentSign = SshAgentSignConfirmHelper.BuildConfirmCallback(
                settings,
                hop.HostKeyVerificationHost,
                hop.HostKeyVerificationPort,
                localizer,
                ct),
            AgentForwarding = hop.AgentForwarding,
            Compression = hop.Compression,
            CryptoPolicy = hop.CryptoPolicy,
            ProxyType = hop.ProxyType,
            ProxyHost = hop.ProxyHost,
            ProxyPort = hop.ProxyPort,
            ProxyUsername = hop.ProxyUsername,
            ProxyPassword = hop.ProxyPassword,
            X11Forwarding = hop.X11Forwarding,
            ConnectTimeout = hop.ConnectTimeout,
            AuthenticationTimeout = hop.AuthenticationTimeout,
            KeepAliveIntervalSeconds = hop.KeepAliveIntervalSeconds,
            KeyboardInteractive = hop.KeyboardInteractive,
            KeyboardInteractiveResponder = hop.KeyboardInteractiveResponder
        }).ToList();
    }

    /// <summary>
    /// Bound on one diagnostic step: one hop's connection and authentication, or the final
    /// destination probe.
    /// </summary>
    /// <remarks>
    /// The connect timeout of the route's hops, the same value a real tunnel gives each hop
    /// (<c>TunnelManager</c> copies <see cref="SshConnectionParams.ConnectTimeout"/> from the
    /// same <see cref="GatewayChainResolver.ToConnectionParams"/> output), taking the largest
    /// when hops differ so no hop is judged more harshly than its tunnel would be. The host key
    /// probe timeout used before is meant for a banner and a key exchange alone; a step also
    /// authenticates, and a hop a real tunnel reaches was reported as timed out. The destination
    /// probe opens a direct-tcpip channel whose wait SSH.NET bounds by the last hop's connect
    /// timeout as well.
    /// </remarks>
    internal static TimeSpan ResolveStepTimeout(IReadOnlyList<SshConnectionParams> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        // An empty route has no hop to take a bound from; the diagnostic itself refuses it
        // as an invalid route, which is what the dialog reports.
        return chain.Select(static hop => hop.ConnectTimeout).DefaultIfEmpty(TimeSpan.Zero).Max();
    }
}

