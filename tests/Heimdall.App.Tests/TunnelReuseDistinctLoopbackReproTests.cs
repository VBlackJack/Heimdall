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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins finding C-06 of the SSH audit of 2026-09-06: a caller that asks for a distinct
/// loopback alias (an external RDP launch, which writes a CredMan entry keyed on the
/// tunnel host) must not be handed an existing tunnel bound on the default loopback
/// address. Reuse used to ignore the bind host and did exactly that.
/// </summary>
public sealed class TunnelReuseDistinctLoopbackReproTests
{
    [Fact]
    public async Task C06_PreferDistinctLoopback_IsNotServedByADefaultLoopbackTunnel()
    {
        using TunnelManager tunnelManager = new();
        const string gatewayId = "gw-A";
        const string remoteHost = "10.0.0.5";
        const int remotePort = 3389;
        const int localPort = 50133;
        SshGatewayDto gateway = new()
        {
            Id = gatewayId,
            Host = "gateway.example.test",
            User = "ssh-user"
        };
        string chainKey = TunnelService.BuildGatewayChainKey([gateway]);

        // An embedded session already opened this tunnel on the default loopback address.
        TunnelInfo existing = TunnelManager.BuildTunnelInfo(
            gateway.Host,
            localPort,
            remoteHost,
            remotePort,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayRoute: null,
            gatewayChainKey: chainKey,
            localBindHost: LoopbackBinding.DefaultHost);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(existing, new NoopDisposable(), () => true));

        TunnelService service = new(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new()
        {
            Id = "server-external",
            RemoteServer = remoteHost,
            RemotePort = remotePort,
            SshGatewayId = gatewayId,
            UseDirectConnection = false
        };
        AppSettings settings = new() { SshGateways = [gateway] };

        // The external launch asks for a distinct alias so its CredMan target does not
        // collide with another external launch reusing another default-loopback tunnel.
        TunnelSetupOutcome result = await service.SetupTunnelIfNeededAsync(
            server,
            remotePort,
            settings,
            CancellationToken.None,
            preferDistinctLoopback: true);

        // With the default-loopback tunnel refused, the service dials a fresh aliased tunnel
        // through the gateway, which this test has no network for: what is pinned is the
        // refusal, not the dial.
        Assert.False(
            result.ReusedExistingTunnel && string.Equals(result.Host, LoopbackBinding.DefaultHost, StringComparison.Ordinal),
            $"preferDistinctLoopback was requested and the reuse path handed back the "
            + $"default-loopback tunnel {result.Host}:{result.Port} (reused={result.ReusedExistingTunnel})");
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
