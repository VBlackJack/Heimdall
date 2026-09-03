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

using System.Text.Json;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests.Configuration;

/// <summary>
/// Which profile owns the trust a connection establishes, when the identifier it runs under is
/// not the identifier it belongs to.
/// </summary>
/// <remarks>
/// Three string-based answers shipped before this one and each handed a certificate approval to
/// an unrelated profile. The decisive property is the one no string-based scheme can have: the
/// SAME text resolving two different ways according to the role the object holds it in.
/// </remarks>
public sealed class ServerProfileTrustIdentityTests
{
    [Fact]
    public void AProfileRunningUnderItsOwnIdentifierOwnsItsOwnTrust()
    {
        ServerProfileDto profile = new() { Id = "prod" };

        Assert.Equal("prod", profile.InventoryProfileId);
        Assert.Null(profile.SessionOriginProfileId);
    }

    [Fact]
    public void APaneThatAdoptedASessionIdentityStillOwnsTheProfilesTrust()
    {
        ServerProfileDto pane = new() { Id = "prod" };
        pane.AdoptSessionIdentity(SessionIdCodec.Create("prod"));

        Assert.Equal("prod", pane.InventoryProfileId);
        Assert.NotEqual("prod", pane.Id);
    }

    // The whole point, and the case that defeated every previous answer.
    //
    // A session identifier is written to the log, and an import preserves whatever identifier its
    // file carried - so a profile can arrive named exactly like some earlier session. Reading the
    // shape sent both to "prod". Asking the inventory sent both to "prod" once the imported one
    // was deleted. A process-wide record of every mint sent both to "prod" because the record is
    // keyed by the text, and the text is the same.
    //
    // Here the two are the same string and answer differently, because the answer is not a
    // property of the string.
    [Fact]
    public void TheSameIdentifierResolvesDifferentlyForAPaneAndForAnImportedProfile()
    {
        string minted = SessionIdCodec.Create("prod");

        ServerProfileDto pane = new() { Id = "prod" };
        pane.AdoptSessionIdentity(minted);

        // The import, arriving later under a string it read out of a file.
        ServerProfileDto imported = new() { Id = minted, DisplayName = "Lab" };

        Assert.Equal(minted, pane.Id);
        Assert.Equal(minted, imported.Id);

        Assert.Equal("prod", pane.InventoryProfileId);
        Assert.Equal(minted, imported.InventoryProfileId);
    }

    // A split of a split mints again over a profile that is already pane-scoped. The trust still
    // belongs to the inventory profile at the bottom, not to the pane in the middle - which would
    // die with its own pane and take the approval with it.
    [Fact]
    public void MintingAgainOverAPaneKeepsTheInventoryProfileUnderneath()
    {
        ServerProfileDto pane = new() { Id = "prod" };
        pane.AdoptSessionIdentity(SessionIdCodec.Create("prod"));
        pane.AdoptSessionIdentity(SessionIdCodec.Create(pane.Id));

        Assert.Equal("prod", pane.InventoryProfileId);
    }

    // The copy taken for a pane, a reconnect or a multi-monitor coercion must carry the origin
    // with it: a clone that dropped it would look exactly like a profile running under its own
    // identifier, and file its approval under a key that dies with the pane.
    [Fact]
    public void AFaithfulCloneCarriesTheOriginItWasCopiedFrom()
    {
        ServerProfileDto pane = new() { Id = "prod" };
        pane.AdoptSessionIdentity(SessionIdCodec.Create("prod"));

        ServerProfileDto clone = pane.CloneFaithfully();

        Assert.Equal("prod", clone.InventoryProfileId);
    }

    // It is a runtime role, not a stored field: writing it into servers.json would make it an
    // imported profile's property too, which is the very thing that must not be forgeable.
    [Fact]
    public void TheOriginIsNeverSerialised()
    {
        ServerProfileDto pane = new() { Id = "prod" };
        pane.AdoptSessionIdentity(SessionIdCodec.Create("prod"));

        string json = JsonSerializer.Serialize(pane);

        Assert.DoesNotContain("SessionOriginProfileId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InventoryProfileId", json, StringComparison.OrdinalIgnoreCase);

        // And it does not survive a round trip, so a crafted file cannot supply one.
        ServerProfileDto revived = JsonSerializer.Deserialize<ServerProfileDto>(json)!;
        Assert.Null(revived.SessionOriginProfileId);
        Assert.Equal(revived.Id, revived.InventoryProfileId);
    }

    [Fact]
    public void ACraftedFileCannotSupplyAnOrigin()
    {
        // The attack the JsonIgnore above forbids: a profile whose file names another profile as
        // its origin would read that profile's approvals and write into them.
        const string crafted =
            "{\"Id\":\"lab\",\"SessionOriginProfileId\":\"prod\",\"DisplayName\":\"Lab\"}";

        ServerProfileDto imported = JsonSerializer.Deserialize<ServerProfileDto>(crafted)!;

        Assert.Null(imported.SessionOriginProfileId);
        Assert.Equal("lab", imported.InventoryProfileId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AdoptingAnEmptySessionIdentityIsRefused(string? sessionId)
    {
        ServerProfileDto profile = new() { Id = "prod" };

        Assert.ThrowsAny<ArgumentException>(() => profile.AdoptSessionIdentity(sessionId!));
        Assert.Equal("prod", profile.InventoryProfileId);
    }
}
