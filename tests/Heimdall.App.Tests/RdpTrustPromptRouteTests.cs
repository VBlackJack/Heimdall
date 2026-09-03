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

namespace Heimdall.App.Tests;

/// <summary>
/// The gateways a certificate question names, which is the half of a machine's identity the
/// endpoint text could not carry.
/// </summary>
/// <remarks>
/// <para><b>The defect this measures.</b> The question showed the profile's own
/// <c>RemoteServer</c> and port, plus the local end of the SSH tunnel. Two saved profiles both
/// named "Production", both reaching "srv01:3389", one through a Paris gateway and one through a
/// Berlin gateway - two physically different machines behind one short name, an ordinary
/// enterprise layout - produced two questions differing only by an ephemeral local port the user
/// has never seen and cannot map to a gateway. Approving the fingerprint believed to be Paris
/// wrote durable trust into the Berlin profile.</para>
/// <para>The gateway was already being read to choose the endpoint's format string, and then
/// thrown away.</para>
/// </remarks>
public sealed class RdpTrustPromptRouteTests
{
    [Fact]
    public void TwoProfilesBehindDifferentGateways_DescribeDifferently()
    {
        // The scenario, end to end at this layer. Nothing else in the question differs.
        SshGatewayDto[] gateways =
        [
            new() { Id = "gw-paris", Name = "Paris datacentre", Host = "gw1.example.com" },
            new() { Id = "gw-berlin", Name = "Berlin datacentre", Host = "gw2.example.com" },
        ];

        string? paris = RdpTrustPromptRoute.Describe(false, "gw-paris", gateways);
        string? berlin = RdpTrustPromptRoute.Describe(false, "gw-berlin", gateways);

        Assert.Equal("Paris datacentre", paris);
        Assert.Equal("Berlin datacentre", berlin);
        Assert.NotEqual(paris, berlin);
    }

    [Fact]
    public void ADirectProfile_ReachesNothingThroughAnything()
    {
        SshGatewayDto[] gateways = [new() { Id = "gw-paris", Name = "Paris" }];

        Assert.Null(RdpTrustPromptRoute.Describe(true, "gw-paris", gateways));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AProfileWithNoGateway_ReachesTheMachineItself(string? gatewayId)
        => Assert.Null(RdpTrustPromptRoute.Describe(false, gatewayId, []));

    [Fact]
    public void AChainedGateway_NamesEveryHopFromTheUserOutwards()
    {
        // The tunnels panel already draws the chain this way round, so a user who has seen one
        // recognises the other.
        SshGatewayDto[] gateways =
        [
            new() { Id = "edge", Name = "Edge", ParentGatewayId = "bastion" },
            new() { Id = "bastion", Name = "Bastion" },
        ];

        Assert.Equal(
            "Bastion" + RdpTrustPromptRoute.ChainSeparator + "Edge",
            RdpTrustPromptRoute.Describe(false, "edge", gateways));
    }

    [Fact]
    public void AGatewayWithNoName_IsNamedByItsHost()
    {
        // A gateway the user never renamed. Its host is what they typed and what they will
        // recognise; the empty name would identify nothing.
        SshGatewayDto[] gateways = [new() { Id = "gw", Name = "  ", Host = "gw1.example.com" }];

        Assert.Equal("gw1.example.com", RdpTrustPromptRoute.Describe(false, "gw", gateways));
    }

    [Fact]
    public void AGatewayThatNoLongerExists_StillIdentifiesTheRoute()
    {
        // A profile pointing at a deleted gateway. The id is not a name the user chose, but it
        // differs between two profiles - and saying nothing is the failure this field exists to
        // end.
        Assert.Equal("gw-vanished", RdpTrustPromptRoute.Describe(false, "gw-vanished", []));
        Assert.Equal("gw-vanished", RdpTrustPromptRoute.Describe(false, " gw-vanished ", null));
    }

    [Fact]
    public void AChainThatLoopsBackOnItself_StopsAtTheRepeat()
    {
        // Configuration is user-supplied and nothing on this path enforces a tree. A question
        // that never renders is a session that never connects.
        SshGatewayDto[] gateways =
        [
            new() { Id = "a", Name = "A", ParentGatewayId = "b" },
            new() { Id = "b", Name = "B", ParentGatewayId = "a" },
        ];

        Assert.Equal(
            "B" + RdpTrustPromptRoute.ChainSeparator + "A",
            RdpTrustPromptRoute.Describe(false, "a", gateways));
    }

    [Fact]
    public void TheGatewayIdIsMatchedWithoutRegardToCase()
    {
        SshGatewayDto[] gateways = [new() { Id = "GW-Paris", Name = "Paris" }];

        Assert.Equal("Paris", RdpTrustPromptRoute.Describe(false, "gw-paris", gateways));
    }
}
