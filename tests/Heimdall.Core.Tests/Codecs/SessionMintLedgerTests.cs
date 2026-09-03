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

using Heimdall.Core.Codecs;

namespace Heimdall.Core.Tests.Codecs;

/// <summary>
/// What the ledger remembers, and what it says once it has stopped remembering.
/// </summary>
/// <remarks>
/// On private instances at a capacity of two, never on the static one
/// <see cref="SessionIdCodec"/> holds: filling that to reach the boundary would evict the mints
/// of every test running beside these, and those tests resolve identifiers.
/// </remarks>
public sealed class SessionMintLedgerTests
{
    [Fact]
    public void AnIdentifierItMintedResolvesToTheProfileItWasMintedFor()
    {
        SessionMintLedger ledger = new(capacity: 2);
        ledger.Record("prod_deadbeef", "prod");

        Assert.Equal("prod", ledger.ResolveOrigin("prod_deadbeef"));
    }

    // The half that matters most, and the one an inventory could not answer. This identifier has
    // exactly the shape a mint produces, and it is a profile's own - an import keeps whatever
    // identifier its file carried. The ledger did not mint it, so it is nobody's session.
    [Fact]
    public void AnIdentifierItDidNotMintIsItsOwn_EvenWithTheShapeOfAMint()
    {
        SessionMintLedger ledger = new(capacity: 2);
        ledger.Record("other_a1b2c3d4", "other");

        Assert.Equal("prod_deadbeef", ledger.ResolveOrigin("prod_deadbeef"));
    }

    // Forgetting is a real behaviour with a bound, not a hypothetical: the ledger is a static
    // that lives as long as the process, and nothing removes an entry when a pane closes.
    [Fact]
    public void PastItsCapacityTheOldestMintIsForgottenAndTheRestAreKept()
    {
        SessionMintLedger ledger = new(capacity: 2);
        ledger.Record("a_00000001", "a");
        ledger.Record("b_00000002", "b");
        ledger.Record("c_00000003", "c");

        // The direction forgetting takes: back to being its own identifier. An approval filed
        // under it dies with the pane and the certificate is asked about again - never an
        // approval reaching a profile that did not earn it.
        Assert.Equal("a_00000001", ledger.ResolveOrigin("a_00000001"));

        Assert.Equal("b", ledger.ResolveOrigin("b_00000002"));
        Assert.Equal("c", ledger.ResolveOrigin("c_00000003"));
    }

    // Without this, an implementation that re-enqueued on every Record would evict a live entry
    // early while the assertions above still passed.
    [Fact]
    public void RecordingTheSameMintTwiceDoesNotConsumeTwoPlaces()
    {
        SessionMintLedger ledger = new(capacity: 2);
        ledger.Record("a_00000001", "a");
        ledger.Record("a_00000001", "a");
        ledger.Record("b_00000002", "b");

        Assert.Equal("a", ledger.ResolveOrigin("a_00000001"));
        Assert.Equal("b", ledger.ResolveOrigin("b_00000002"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyIdentifierIsEchoed(string identifier)
        => Assert.Equal(identifier, new SessionMintLedger(capacity: 2).ResolveOrigin(identifier));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACapacityThatCouldRememberNothingIsRefused(int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SessionMintLedger(capacity));
}
