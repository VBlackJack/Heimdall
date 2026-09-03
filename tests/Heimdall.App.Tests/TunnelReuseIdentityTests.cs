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
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class TunnelReuseIdentityTests
{
    [Fact]
    public async Task SetupTunnelIfNeededAsync_ReusedTunnelReturnsExistingLocalBindHost()
    {
        using var tunnelManager = new TunnelManager();
        const string gatewayId = "gw-A";
        const string remoteHost = "10.0.0.5";
        const int remotePort = 3389;
        const int localPort = 50123;
        string localBindHost = LoopbackBinding.FormatAlias(2);
        var gateway = new SshGatewayDto
        {
            Id = gatewayId,
            Host = "gateway.example.test",
            User = "ssh-user"
        };
        TunnelInfo existing = MakeTunnel(
            TunnelService.BuildGatewayChainKey([gateway]),
            remoteHost,
            remotePort) with
        {
            LocalPort = localPort,
            LocalBindHost = localBindHost
        };

        Assert.True(tunnelManager.TryRegisterExternalTunnel(existing, new TestDisposable(), () => true));
        var service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        var server = new ServerProfileDto
        {
            Id = "server-1",
            RemoteServer = remoteHost,
            RemotePort = remotePort,
            SshGatewayId = gatewayId,
            UseDirectConnection = false
        };
        var settings = new AppSettings { SshGateways = [gateway] };

        var result = await service.SetupTunnelIfNeededAsync(
            server,
            remotePort,
            settings,
            CancellationToken.None,
            preferDistinctLoopback: true);

        Assert.True(result.Success);
        Assert.True(result.UsesTunnel);
        Assert.Equal(localBindHost, result.Host);
        Assert.Equal(localPort, result.Port);

        // The chain resolved just above is not what this tunnel was opened through, and the
        // caller has to be told so. The reuse key hashes gateway identifiers, which an edit
        // leaves alone, so the certificate question would otherwise name the gateway host as it
        // reads TODAY for a certificate that answered at the end of a tunnel opened through the
        // host it had BEFORE.
        Assert.True(result.ReusedExistingTunnel);
    }

    // What the reuse flag above is FOR, measured on the value the question actually shows.
    //
    // Reuse is decided on a hash over the chain's gateway identifiers, and editing a gateway
    // leaves its identifier alone - so the tunnel handed back here was dialled through a chain
    // this connection cannot reconstruct, and may have been dialled by another profile entirely.
    // Resolving the route from the settings in hand would name Berlin for a certificate that
    // answered at the end of the Paris tunnel.
    //
    // Withholding it instead - which is what shipped - is honest and not sufficient: the line
    // exists so that two identically named profiles reaching two different sites can be told
    // apart, and both of them reusing a tunnel is precisely when both lines vanished. So the
    // opener records the route on the tunnel, and reuse reads it back.
    [Fact]
    public async Task SetupTunnelIfNeededAsync_ReusedTunnel_ReportsTheRouteThatTunnelWasOpenedThrough()
    {
        using var tunnelManager = new TunnelManager();
        const string gatewayId = "gw-A";
        const string remoteHost = "10.0.0.5";
        const int remotePort = 3389;
        var gateway = new SshGatewayDto
        {
            Id = gatewayId,
            Host = "gateway.example.test",
            User = "ssh-user"
        };
        // Carrying the route it was built with, back when the gateway still read this way.
        // Written in the initialiser because the property is init-only: a route assigned after
        // the record exists reaches whichever copy is in hand, and this record is copied twice
        // more before any caller sees it.
        TunnelInfo existing = MakeTunnel(
            TunnelService.BuildGatewayChainKey([gateway]),
            remoteHost,
            remotePort) with
        {
            LocalPort = 50124,
            GatewayRoute = "Paris datacentre"
        };

        Assert.True(tunnelManager.TryRegisterExternalTunnel(existing, new TestDisposable(), () => true));
        var service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        var server = new ServerProfileDto
        {
            Id = "server-1",
            RemoteServer = remoteHost,
            RemotePort = remotePort,
            SshGatewayId = gatewayId,
            UseDirectConnection = false
        };

        // The same identifier, renamed since - which is exactly what reuse cannot see.
        var settings = new AppSettings
        {
            SshGateways =
            [
                new SshGatewayDto
                {
                    Id = gatewayId,
                    Name = "Berlin datacentre",
                    Host = "berlin.example.test",
                    User = "ssh-user"
                }
            ]
        };

        var result = await service.SetupTunnelIfNeededAsync(
            server,
            remotePort,
            settings,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ReusedExistingTunnel);

        // Paris, which this tunnel was dialled through. Not Berlin, which the chain resolved for
        // this attempt would say, and not null, which is what shipped.
        Assert.Equal("Paris datacentre", result.GatewayRoute);
    }

    // The other half, and the reason the route is allowed to be absent at all: a tunnel this
    // process did not open records nothing, so there is nothing truthful to show. The question
    // then draws no route line, which is what every reuse did before the opening was recorded.
    // Without this control the assertion above would also pass on an implementation that echoed
    // any non-empty string it could find.
    [Fact]
    public async Task SetupTunnelIfNeededAsync_ReusedTunnelNobodyRecorded_ReportsNoRoute()
    {
        using var tunnelManager = new TunnelManager();
        const string gatewayId = "gw-A";
        const string remoteHost = "10.0.0.5";
        const int remotePort = 3389;
        var gateway = new SshGatewayDto
        {
            Id = gatewayId,
            Host = "gateway.example.test",
            User = "ssh-user"
        };
        TunnelInfo existing = MakeTunnel(
            TunnelService.BuildGatewayChainKey([gateway]),
            remoteHost,
            remotePort) with
        {
            LocalPort = 50125
        };

        Assert.True(tunnelManager.TryRegisterExternalTunnel(existing, new TestDisposable(), () => true));
        var service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        var server = new ServerProfileDto
        {
            Id = "server-1",
            RemoteServer = remoteHost,
            RemotePort = remotePort,
            SshGatewayId = gatewayId,
            UseDirectConnection = false
        };
        var settings = new AppSettings
        {
            SshGateways =
            [
                new SshGatewayDto
                {
                    Id = gatewayId,
                    Name = "Berlin datacentre",
                    Host = "berlin.example.test",
                    User = "ssh-user"
                }
            ]
        };

        var result = await service.SetupTunnelIfNeededAsync(
            server,
            remotePort,
            settings,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ReusedExistingTunnel);
        Assert.Null(result.GatewayRoute);
    }

    [Fact]
    public async Task SetupTunnelIfNeededAsync_NoReusableTunnel_DoesNotReportReuse()
    {
        // The negative control the flag needs: an open tunnel whose chain key does not match is
        // not reusable, so this attempt goes on to dial and fails against a verifier that
        // refuses every host key. Failure or not, nothing was reused, and a flag that answered
        // true here would withhold the route line from every connection instead of from the
        // reusing ones.
        using var tunnelManager = new TunnelManager();
        const string gatewayId = "gw-A";
        const string remoteHost = "10.0.0.5";
        const int remotePort = 3389;
        var gateway = new SshGatewayDto
        {
            Id = gatewayId,
            Host = "gateway.example.test",
            User = "ssh-user"
        };
        TunnelInfo unrelated = MakeTunnel(
            TunnelService.BuildGatewayChainKey(
                [new SshGatewayDto { Id = "gw-OTHER", Host = "other.example.test" }]),
            remoteHost,
            remotePort) with
        {
            LocalPort = 50321
        };

        Assert.True(tunnelManager.TryRegisterExternalTunnel(unrelated, new TestDisposable(), () => true));
        var service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        var server = new ServerProfileDto
        {
            Id = "server-2",
            RemoteServer = remoteHost,
            RemotePort = remotePort,
            SshGatewayId = gatewayId,
            UseDirectConnection = false
        };
        var settings = new AppSettings { SshGateways = [gateway] };

        var result = await service.SetupTunnelIfNeededAsync(
            server,
            remotePort,
            settings,
            CancellationToken.None);

        Assert.False(result.ReusedExistingTunnel);
    }

    [Fact]
    public void ShouldUseOsAssignedLocalPort_ReturnsTrue_WhenLocalPortMatchesSuggestedDefault()
    {
        var settings = new AppSettings
        {
            DefaultRdpTunnelPort = 45000,
            DefaultSshTunnelPort = 46000
        };

        Assert.True(TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "RDP", LocalPort = 45000 },
            settings));
        Assert.True(TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "SSH", LocalPort = 46000 },
            settings));
        Assert.True(TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "WINRM", LocalPort = DefaultPorts.WinRmTunnel },
            settings));
    }

    [Fact]
    public void ShouldUseOsAssignedLocalPort_ReturnsTrue_WhenLocalPortIsUnset()
    {
        var result = TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "SSH", LocalPort = 0 },
            new AppSettings());

        Assert.True(result);
    }

    [Fact]
    public void ShouldUseOsAssignedLocalPort_ReturnsFalse_WhenLocalPortIsManual()
    {
        var result = TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "SSH", LocalPort = 47000 },
            new AppSettings { DefaultSshTunnelPort = 46000 });

        Assert.False(result);
    }

    [Fact]
    public void ShouldUseOsAssignedLocalPort_ReturnsFalse_ForSocksOrRemoteForward()
    {
        var settings = new AppSettings { DefaultSshTunnelPort = 46000 };

        Assert.False(TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "SSH", LocalPort = 46000, SocksProxyPort = 1080 },
            settings));
        Assert.False(TunnelService.ShouldUseOsAssignedLocalPort(
            new ServerProfileDto { ConnectionType = "SSH", LocalPort = 46000, RemoteBindPort = 2222 },
            settings));
    }

    [Fact]
    public void AcquireReusableTunnel_SameChainAndTarget_ReturnsExistingTunnel()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A");

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Equal(existing.LocalPort, result?.LocalPort);
    }

    [Fact]
    public void AcquireReusableTunnel_DifferentChainSameTarget_ReturnsNull()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A");

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-B",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Null(result);
    }

    [Fact]
    public void AcquireReusableTunnel_SameChainDifferentTarget_ReturnsNull()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A");

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-A",
            "10.0.0.5",
            3390,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Null(result);
    }

    [Fact]
    public void AcquireReusableTunnel_DifferentSocksProxyPort_ReturnsNull()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A", socksProxyPort: 1080);

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 1081,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Null(result);
    }

    [Fact]
    public void AcquireReusableTunnel_DifferentRemoteBindPort_ReturnsNull()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A", remoteBindPort: 2222);

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 2223,
            remoteLocalPort: 0);

        Assert.Null(result);
    }

    [Fact]
    public void AcquireReusableTunnel_DifferentEffectiveRemoteLocalPort_ReturnsNull()
    {
        using var tunnelManager = new TunnelManager();
        TunnelInfo existing = MakeTunnel(
            gatewayChainKey: "gw-A",
            remoteBindPort: 2222,
            effectiveRemoteLocalPort: 2200);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(
            existing,
            new TestDisposable(),
            () => true));

        TunnelInfo? result = tunnelManager.AcquireReusableTunnel(
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 2222,
            remoteLocalPort: 2201);

        Assert.Null(result);
        Assert.True(tunnelManager.ReleaseReference(existing.LocalPort));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2222)]
    public void AcquireReusableTunnel_DefaultAndExplicitReverseLocalPort_AreEquivalent(
        int requestedRemoteLocalPort)
    {
        using var tunnelManager = new TunnelManager();
        TunnelInfo existing = MakeTunnel(
            gatewayChainKey: "gw-A",
            remoteBindPort: 2222,
            effectiveRemoteLocalPort: 2222);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(
            existing,
            new TestDisposable(),
            () => true));

        TunnelInfo? result = tunnelManager.AcquireReusableTunnel(
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 2222,
            remoteLocalPort: requestedRemoteLocalPort);

        Assert.NotNull(result);
        Assert.Equal(existing.LocalPort, result.LocalPort);
        Assert.False(tunnelManager.ReleaseReference(existing.LocalPort));
        Assert.True(tunnelManager.ReleaseReference(existing.LocalPort));
    }

    [Fact]
    public void AcquireReusableTunnel_ReverseDisabled_IgnoresRemoteLocalPort()
    {
        using var tunnelManager = new TunnelManager();
        TunnelInfo existing = MakeTunnel(
            gatewayChainKey: "gw-A",
            remoteBindPort: 0,
            effectiveRemoteLocalPort: 0);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(
            existing,
            new TestDisposable(),
            () => true));

        TunnelInfo? result = tunnelManager.AcquireReusableTunnel(
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 65535);

        Assert.NotNull(result);
        Assert.False(tunnelManager.ReleaseReference(existing.LocalPort));
        Assert.True(tunnelManager.ReleaseReference(existing.LocalPort));
    }

    [Fact]
    public void AcquireReusableTunnel_DeadTunnel_ReturnsNull()
    {
        var existing = MakeTunnel(gatewayChainKey: "gw-A", isAlive: false);

        var result = AcquireFromSingleCandidate(
            existing,
            "gw-A",
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Null(result);
    }

    [Fact]
    public void AcquireReusableTunnel_EmptyChainKeyMatchesEmptyChainKey()
    {
        var existing = MakeTunnel(gatewayChainKey: string.Empty);

        var result = AcquireFromSingleCandidate(
            existing,
            string.Empty,
            "10.0.0.5",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.Equal(existing.LocalPort, result?.LocalPort);
    }

    [Fact]
    public void AcquireReusableTunnel_NullGatewayChainKey_Throws()
    {
        using var tunnelManager = new TunnelManager();

        Assert.Throws<ArgumentNullException>(
            () => tunnelManager.AcquireReusableTunnel(
                null!,
                "10.0.0.5",
                3389,
                socksProxyPort: 0,
                remoteBindPort: 0,
                remoteLocalPort: 0));
    }

    [Fact]
    public void AcquireReusableTunnel_NullRemoteHost_Throws()
    {
        using var tunnelManager = new TunnelManager();

        Assert.Throws<ArgumentNullException>(
            () => tunnelManager.AcquireReusableTunnel(
                "gw-A",
                null!,
                3389,
                socksProxyPort: 0,
                remoteBindPort: 0,
                remoteLocalPort: 0));
    }

    [Fact]
    public void BuildGatewayChainKey_EmptyChain_ReturnsEmpty()
    {
        var result = TunnelService.BuildGatewayChainKey([]);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildGatewayChainKey_SingleHop_IsStableAndVersioned()
    {
        var chain = new[] { new SshGatewayDto { Id = "gw-A" } };

        var first = TunnelService.BuildGatewayChainKey(chain);
        var second = TunnelService.BuildGatewayChainKey(chain);

        Assert.StartsWith("v1:sha256:", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildGatewayChainKey_MultiHop_IsOrderSensitiveAndSeparatorSafe()
    {
        var chain = new[]
        {
            new SshGatewayDto { Id = "foo:1" },
            new SshGatewayDto { Id = "bar" }
        };
        var sameIdsDifferentOrder = new[]
        {
            new SshGatewayDto { Id = "bar" },
            new SshGatewayDto { Id = "foo:1" }
        };
        var naiveJoinCollision = new[]
        {
            new SshGatewayDto { Id = "foo" },
            new SshGatewayDto { Id = "1|bar" }
        };

        var key = TunnelService.BuildGatewayChainKey(chain);

        Assert.NotEqual(key, TunnelService.BuildGatewayChainKey(sameIdsDifferentOrder));
        Assert.NotEqual(key, TunnelService.BuildGatewayChainKey(naiveJoinCollision));
    }

    private static TunnelInfo MakeTunnel(
        string gatewayChainKey,
        string remoteHost = "10.0.0.5",
        int remotePort = 3389,
        bool isAlive = true,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int effectiveRemoteLocalPort = 0)
    {
        return new TunnelInfo(
            "gateway",
            50123,
            remoteHost,
            remotePort,
            DateTime.UtcNow,
            isAlive)
        {
            GatewayChainKey = gatewayChainKey,
            SocksProxyPort = socksProxyPort,
            RemoteBindPort = remoteBindPort,
            EffectiveRemoteLocalPort = effectiveRemoteLocalPort
        };
    }

    private static TunnelInfo? AcquireFromSingleCandidate(
        TunnelInfo existing,
        string gatewayChainKey,
        string remoteHost,
        int remotePort,
        int socksProxyPort,
        int remoteBindPort,
        int remoteLocalPort)
    {
        using var tunnelManager = new TunnelManager();
        Assert.True(tunnelManager.TryRegisterExternalTunnel(
            existing,
            new TestDisposable(),
            () => existing.IsAlive));

        return tunnelManager.AcquireReusableTunnel(
            gatewayChainKey,
            remoteHost,
            remotePort,
            socksProxyPort,
            remoteBindPort,
            remoteLocalPort);
    }

    private sealed class TestDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
