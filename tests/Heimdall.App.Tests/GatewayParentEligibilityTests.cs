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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class GatewayParentEligibilityTests
{
    // The pickers excluded only the gateway itself. With A the parent of B, editing A offered
    // B, and saving it closed a loop that only surfaced when a session failed to connect.
    // Proved against the mutant that drops the descendant walk: B comes back and this goes red.
    [Fact]
    public void EligibleParents_LeavesOutTheGatewayAndEveryDescendant()
    {
        List<SshGatewayDto> gateways =
        [
            Gateway("a"),
            Gateway("b", parent: "a"),
            Gateway("c", parent: "B"),
            Gateway("other")
        ];

        Assert.Equal(["other"], Ids(GatewayParentEligibility.EligibleParents(gateways, "a")));
        Assert.Equal(["a", "other"], Ids(GatewayParentEligibility.EligibleParents(gateways, "b")));
    }

    // A chain the resolver refuses to follow is as broken as a loop, and it was saved the
    // same way. A new gateway adds one hop below the parent it is given.
    [Fact]
    public void EligibleParents_ForANewGateway_StopsAtTheDeepestParentAChainAccepts()
    {
        List<SshGatewayDto> chain = Chain(GatewayChainResolver.DefaultMaxDepth);

        IReadOnlyList<SshGatewayDto> eligible = GatewayParentEligibility.EligibleParents(chain, gatewayId: null);

        Assert.Equal(GatewayChainResolver.DefaultMaxDepth - 1, eligible.Count);
        Assert.DoesNotContain(eligible, gateway => gateway.Id == chain[^1].Id);
    }

    // An edited gateway carries its children with it, so the chain below it counts too.
    [Fact]
    public void EligibleParents_ForAGatewayWithChildren_CountsTheChainBelowIt()
    {
        List<SshGatewayDto> gateways = Chain(GatewayChainResolver.DefaultMaxDepth - 1);
        gateways.Add(Gateway("moved"));
        gateways.Add(Gateway("moved-child", parent: "moved"));

        IReadOnlyList<SshGatewayDto> eligible = GatewayParentEligibility.EligibleParents(gateways, "moved");

        Assert.Equal(
            gateways.Take(GatewayChainResolver.DefaultMaxDepth - 2).Select(gateway => gateway.Id),
            Ids(eligible));
    }

    // A candidate whose own route does not resolve would hand the edited gateway that failure.
    [Fact]
    public void EligibleParents_LeavesOutACandidateWhoseRouteIsBroken()
    {
        List<SshGatewayDto> gateways =
        [
            Gateway("x", parent: "y"),
            Gateway("y", parent: "x"),
            Gateway("orphan", parent: "deleted"),
            Gateway("fine")
        ];

        Assert.Equal(["fine"], Ids(GatewayParentEligibility.EligibleParents(gateways, gatewayId: null)));
    }

    // A loop already on disk must not hang or throw the picker that is the way out of it.
    [Fact]
    public void EligibleParents_EditingAGatewayInsideALoop_OffersEverythingOutsideIt()
    {
        List<SshGatewayDto> gateways =
        [
            Gateway("a", parent: "b"),
            Gateway("b", parent: "a"),
            Gateway("outside")
        ];

        Assert.Equal(["outside"], Ids(GatewayParentEligibility.EligibleParents(gateways, "a")));
    }

    // A hand edit can leave two gateways under one id; a dictionary built from them threw.
    [Fact]
    public void EligibleParents_ToleratesADuplicatedId()
    {
        List<SshGatewayDto> gateways = [Gateway("dup"), Gateway("DUP"), Gateway("fine")];

        Assert.Equal(["dup", "fine"], Ids(GatewayParentEligibility.EligibleParents(gateways, gatewayId: null)));
    }

    [Fact]
    public void BuildOptions_DescribesEachParentByNameAndHost()
    {
        GatewayOption option = Assert.Single(
            GatewayParentEligibility.BuildOptions([Gateway("bastion")], gatewayId: null));

        Assert.Equal("bastion", option.Id);
        Assert.Equal("bastion-name (bastion.example.test)", option.DisplayText);
    }

    // Without an entry for no parent, a parent once chosen could only be swapped for another,
    // and a loop in which every other gateway sat could not be broken from the dialog.
    [Fact]
    public void ParentChoices_OffersADirectConnectionFirst()
    {
        GatewayDialogViewModel vm = new()
        {
            AvailableParents = GatewayParentEligibility.BuildOptions([Gateway("bastion")], gatewayId: null)
        };

        Assert.Equal([string.Empty, "bastion"], vm.ParentChoices.Select(choice => choice.Id));
    }

    private static List<SshGatewayDto> Chain(int length)
    {
        List<SshGatewayDto> chain = [];
        for (int index = 0; index < length; index++)
        {
            chain.Add(Gateway($"hop-{index}", parent: index == 0 ? null : $"hop-{index - 1}"));
        }

        return chain;
    }

    private static string[] Ids(IEnumerable<SshGatewayDto> gateways) =>
        gateways.Select(gateway => gateway.Id).ToArray();

    private static SshGatewayDto Gateway(string id, string? parent = null) => new()
    {
        Id = id,
        Name = $"{id}-name",
        Host = $"{id}.example.test",
        User = "ops",
        ParentGatewayId = parent
    };
}
