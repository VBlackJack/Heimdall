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

    // A hand edit can leave two gateways under one id. The resolver refuses that inventory, so
    // neither is a parent anything could connect through, and building the list must not throw.
    [Fact]
    public void EligibleParents_LeavesOutADuplicatedId()
    {
        List<SshGatewayDto> gateways = [Gateway("dup"), Gateway("DUP"), Gateway("fine")];

        Assert.Equal(["fine"], Ids(GatewayParentEligibility.EligibleParents(gateways, gatewayId: null)));
    }

    // The rule is only worth what the resolver agrees with. On seeded random inventories, loops
    // included, a candidate is offered exactly when choosing it lets every chain through the
    // edited gateway resolve: the gateway itself and everything below it.
    [Fact]
    public void EligibleParents_AgreesWithTheResolverOnRandomInventories()
    {
        Random random = new(20261004);
        for (int round = 0; round < 300; round++)
        {
            List<SshGatewayDto> gateways = RandomInventory(random);
            foreach (SshGatewayDto edited in gateways)
            {
                HashSet<string> eligible = [.. Ids(GatewayParentEligibility.EligibleParents(gateways, edited.Id))];
                foreach (SshGatewayDto candidate in gateways.Where(gateway => gateway != edited))
                {
                    bool resolves = AllChainsResolve(gateways, edited.Id, candidate.Id);
                    Assert.True(
                        resolves == eligible.Contains(candidate.Id),
                        $"Round {round}: parent {candidate.Id} for {edited.Id} resolves={resolves}.");
                }
            }
        }
    }

    // A parent the rule would refuse today is still the parent the gateway has. Leaving it out
    // made the picker clear it, and a rename saved the gateway as a direct connection.
    [Fact]
    public void BuildOptions_KeepsTheCurrentParentEvenWhenTheRuleWouldRefuseIt()
    {
        List<SshGatewayDto> gateways =
        [
            Gateway("broken-parent", parent: "deleted"),
            Gateway("edited", parent: "BROKEN-PARENT"),
            Gateway("fine")
        ];

        Assert.Equal(
            ["broken-parent", "fine"],
            GatewayParentEligibility.BuildOptions(gateways, "edited").Select(option => option.Id));
    }

    // The picker compares ids exactly while the rest of the product ignores case, so a parent
    // stored in another case read as no selection and was cleared.
    [Fact]
    public void AvailableParents_AdoptsTheOptionSpellingOfTheCurrentParentWithoutMarkingAnEdit()
    {
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(Gateway("edited", parent: "BASTION"));

        vm.AvailableParents = GatewayParentEligibility.BuildOptions(
            [Gateway("bastion"), Gateway("edited", parent: "BASTION")],
            "edited");

        Assert.Equal("bastion", vm.SelectedParentGatewayId);
        Assert.False(vm.IsDirty);
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

    private static List<SshGatewayDto> RandomInventory(Random random)
    {
        int count = random.Next(1, 9);
        List<SshGatewayDto> gateways = [];
        for (int index = 0; index < count; index++)
        {
            int parent = random.Next(-2, count);
            gateways.Add(Gateway(
                $"g{index}",
                parent switch
                {
                    -2 => "missing",
                    -1 => null,
                    _ when parent == index => null,
                    _ => $"g{parent}"
                }));
        }

        return gateways;
    }

    private static bool AllChainsResolve(List<SshGatewayDto> gateways, string editedId, string parentId)
    {
        List<SshGatewayDto> changed = gateways.Select(gateway => gateway.CloneFaithfully()).ToList();
        changed.Single(gateway => gateway.Id == editedId).ParentGatewayId = parentId;

        HashSet<string> affected = [editedId];
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (SshGatewayDto gateway in changed)
            {
                if (gateway.ParentGatewayId is not null
                    && affected.Contains(gateway.ParentGatewayId)
                    && affected.Add(gateway.Id))
                {
                    grew = true;
                }
            }
        }

        try
        {
            foreach (string id in affected)
            {
                GatewayChainResolver.ResolveChainDtos(id, changed);
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or GatewayChainException)
        {
            return false;
        }
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
