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

public sealed class TunnelServiceChainDiagnosticsTests
{
    [Fact]
    public async Task SetupTunnelIfNeededAsync_CircularGatewayChain_ReportsCircularChainDependency()
    {
        SshGatewayDto gatewayOne = new SshGatewayDto
        {
            Id = "gw1",
            Host = "gw1.example.test",
            User = "ssh-user",
            ParentGatewayId = "gw2"
        };
        SshGatewayDto gatewayTwo = new SshGatewayDto
        {
            Id = "gw2",
            Host = "gw2.example.test",
            User = "ssh-user",
            ParentGatewayId = "gw1"
        };

        using TunnelManager tunnelManager = new TunnelManager();
        TunnelService service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new ServerProfileDto
        {
            Id = "server-circular",
            RemoteServer = "target.example.test",
            RemotePort = 22,
            SshGatewayId = "gw1",
            UseDirectConnection = false
        };
        AppSettings settings = new AppSettings { SshGateways = [gatewayOne, gatewayTwo] };

        TunnelSetupOutcome outcome = await service.SetupTunnelIfNeededAsync(
            server,
            22,
            settings,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.CircularChainDependency, outcome.FailureCode);
    }

    [Fact]
    public async Task SetupTunnelIfNeededAsync_ChainDeeperThanDefaultMaxDepth_ReportsChainDepthExceeded()
    {
        List<SshGatewayDto> gateways = new List<SshGatewayDto>();
        for (int index = 0; index < 8; index++)
        {
            gateways.Add(new SshGatewayDto
            {
                Id = $"gw{index}",
                Host = $"gw{index}.example.test",
                User = "ssh-user",
                ParentGatewayId = index > 0 ? $"gw{index - 1}" : null
            });
        }

        using TunnelManager tunnelManager = new TunnelManager();
        TunnelService service = new TunnelService(
            tunnelManager,
            new HostKeyStore(),
            new HostKeyTrustService(new HostKeyStore()),
            new ConnectionStateMachine(),
            new LocalizationManager(),
            RejectingHostKeyVerifier.Instance);
        ServerProfileDto server = new ServerProfileDto
        {
            Id = "server-deep-chain",
            RemoteServer = "target.example.test",
            RemotePort = 22,
            SshGatewayId = "gw7",
            UseDirectConnection = false
        };
        AppSettings settings = new AppSettings { SshGateways = gateways };

        TunnelSetupOutcome outcome = await service.SetupTunnelIfNeededAsync(
            server,
            22,
            settings,
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(SshFailureCode.ChainDepthExceeded, outcome.FailureCode);
    }
}
