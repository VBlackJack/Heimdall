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

using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Whether the gateway chain a tunnel was dialled through survives from the record's construction
/// to the caller that asks about it.
/// </summary>
/// <remarks>
/// <para><b>The gap these close.</b> The route used to be assigned to the returned record once
/// the tunnel was up, and a test that handed a reuse an already-stamped tunnel measured the
/// hand-back rather than the stamping. Two opening paths were therefore never covered: the
/// assignment ran after the configured establishment delay, while the tunnel was registered and
/// reusable before it, and the successful Plink fallback returned from a branch above the
/// assignment entirely. Both produced a live, reusable tunnel carrying no route.</para>
/// <para><b>What is measured instead.</b> That the route is on the record from construction, and
/// that it survives each <c>with</c> copy taken between there and the caller. Those copies are
/// what a post-hoc assignment could not reach, and they are reachable from a test without
/// dialling anything.</para>
/// </remarks>
public sealed class TunnelRouteRecordTests
{
    private const string Route = "Paris datacentre";

    [Fact]
    public void TheRecordCarriesTheRouteItWasBuiltWith()
    {
        TunnelInfo info = TunnelManager.BuildTunnelInfo(
            "gw.example.test",
            51001,
            "srv01",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayRoute: Route);

        Assert.Equal(Route, info.GatewayRoute);
    }

    // A direct connection has no chain to name, and an empty string under "Reached through" is a
    // label with nothing after it rather than an absent line.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARecordBuiltWithNoRouteCarriesNone(string? gatewayRoute)
    {
        TunnelInfo info = TunnelManager.BuildTunnelInfo(
            "gw.example.test",
            51002,
            "srv01",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayRoute: gatewayRoute);

        Assert.Null(info.GatewayRoute);
    }

    // The copy the reuse hand-back takes. `AcquireReusableTunnel` returns `info with { IsAlive =
    // true }`, so a route that reached only the instance the opener held would be present on a
    // record nobody else ever sees.
    [Fact]
    public void ReusingATunnelHandsBackTheRouteItWasOpenedThrough()
    {
        using TunnelManager manager = new();
        TunnelInfo info = TunnelManager.BuildTunnelInfo(
            "gw.example.test",
            51003,
            "srv01",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayChainKey: "chain-key-1",
            gatewayRoute: Route);

        Assert.True(manager.TryRegisterExternalTunnel(info, new NoopDisposable(), () => true));

        TunnelInfo? reused = manager.AcquireReusableTunnel(
            "chain-key-1",
            "srv01",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0);

        Assert.NotNull(reused);
        Assert.Equal(Route, reused!.GatewayRoute);
    }

    // And the copy external registration takes on its way in: `TryRegisterExternalTunnel`
    // rewrites LocalBindHost with `with`, which is the path every Plink tunnel arrives by. A
    // route dropped there would be missing for exactly the fallback that had no route at all
    // before.
    [Fact]
    public void RegisteringAnExternalTunnelKeepsItsRouteThroughTheBindHostRewrite()
    {
        using TunnelManager manager = new();
        TunnelInfo info = TunnelManager.BuildTunnelInfo(
            "gw.example.test",
            51004,
            "srv01",
            3389,
            socksProxyPort: 0,
            remoteBindPort: 0,
            remoteLocalPort: 0,
            gatewayChainKey: "chain-key-2",
            localBindHost: "127.0.0.9",
            gatewayRoute: Route);

        Assert.True(manager.TryRegisterExternalTunnel(info, new NoopDisposable(), () => true));

        TunnelInfo registered = Assert.Single(
            manager.GetActiveTunnels(),
            tunnel => tunnel.LocalPort == 51004);

        Assert.Equal(Route, registered.GatewayRoute);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
