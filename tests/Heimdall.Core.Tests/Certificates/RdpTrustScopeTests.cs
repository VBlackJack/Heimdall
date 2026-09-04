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

using Heimdall.Core.Certificates;

namespace Heimdall.Core.Tests.Certificates;

/// <summary>
/// A saved profile and a destination typed by hand are two owners, even when their identity
/// strings agree.
/// </summary>
/// <remarks>
/// <para>The oracle the audit named: approve under one scope, assert Unknown under the other
/// WITH THE SAME IDENTITY STRING. A store that folds the scope away - keys on the identity, or
/// serves one scope's set to the other - passes every older test in this directory and fails
/// these. The identity is deliberately the same string on both sides, because that is the
/// collision: the palette mints <c>adhoc-rdp-&lt;host&gt;</c> for a typed destination and an old
/// import can put that very string on a saved profile.</para>
/// </remarks>
public sealed class RdpTrustScopeTests
{
    private const string SharedIdentity = "adhoc-rdp-prod.example";
    private const string Thumbprint = "SHA256:AA:BB:01";
    private const string OtherThumbprint = "SHA256:AA:BB:02";

    private static readonly RdpTrustKey ProfileOwner = RdpTrustKey.ForProfile(SharedIdentity);
    private static readonly RdpTrustKey TypedOwner = RdpTrustKey.ForTypedDestination(SharedIdentity);

    [Fact]
    public void TwoOwnersWithOneIdentityString_AreTwoKeys()
    {
        Assert.Equal(ProfileOwner.Identity, TypedOwner.Identity);
        Assert.NotEqual(ProfileOwner, TypedOwner);
        Assert.NotEqual(ProfileOwner.ToString(), TypedOwner.ToString());
    }

    [Fact]
    public void Trust_ForTheProfile_LeavesTheTypedDestinationUnknown()
    {
        RdpCertificateTrustStore store = new();

        store.Trust(ProfileOwner, Thumbprint);

        Assert.Equal(RdpCertificateTrustVerdict.Trusted, store.Evaluate(ProfileOwner, Thumbprint).Verdict);
        RdpCertificateTrustDecision seenByTyped = store.Evaluate(TypedOwner, Thumbprint);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, seenByTyped.Verdict);
        Assert.Equal(0, seenByTyped.AlreadyTrustedCount);
    }

    [Fact]
    public void Trust_ForTheTypedDestination_LeavesTheProfileUnknown()
    {
        RdpCertificateTrustStore store = new();

        store.Trust(TypedOwner, Thumbprint);

        Assert.Equal(RdpCertificateTrustVerdict.Trusted, store.Evaluate(TypedOwner, Thumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, store.Evaluate(ProfileOwner, Thumbprint).Verdict);
    }

    // The session half of the same rule. Session trust used to be a second dictionary keyed the
    // same way as the durable one, so a "just this once" given for a typed destination silenced
    // the profile sharing its identifier for the length of the run, with nothing written to disk
    // to notice it by.
    [Fact]
    public void TrustForSession_IsScopedExactlyAsDurableTrustIs()
    {
        RdpCertificateTrustStore store = new();

        store.TrustForSession(TypedOwner, Thumbprint);

        Assert.Equal(RdpCertificateTrustVerdict.TrustedForSession, store.Evaluate(TypedOwner, Thumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, store.Evaluate(ProfileOwner, Thumbprint).Verdict);
    }

    // The load objection to the earlier two-dictionary design: a load per scope wipes the
    // scope loaded before it. One call carries both, and both survive it.
    [Fact]
    public void LoadFromConfig_OneCallCarriesBothScopes_AndBothSurvive()
    {
        RdpCertificateTrustStore store = new();

        store.LoadFromConfig(
        [
            (ProfileOwner, [Entry(Thumbprint)]),
            (TypedOwner, [Entry(OtherThumbprint)]),
        ]);

        Assert.Equal(RdpCertificateTrustVerdict.Trusted, store.Evaluate(ProfileOwner, Thumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, store.Evaluate(ProfileOwner, OtherThumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Trusted, store.Evaluate(TypedOwner, OtherThumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, store.Evaluate(TypedOwner, Thumbprint).Verdict);
        Assert.Equal(2, store.GetAllApproved().Count);
    }

    [Fact]
    public void Remove_UnderOneScope_LeavesTheOtherScopesEntryAlone()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(ProfileOwner, Thumbprint);
        store.Trust(TypedOwner, Thumbprint);

        Assert.True(store.Remove(TypedOwner, Thumbprint));

        Assert.Equal(RdpCertificateTrustVerdict.Unknown, store.Evaluate(TypedOwner, Thumbprint).Verdict);
        Assert.Equal(RdpCertificateTrustVerdict.Trusted, store.Evaluate(ProfileOwner, Thumbprint).Verdict);
        Assert.False(store.Remove(TypedOwner, Thumbprint));
    }

    // The persistence subscriber picks the dictionary by the key it is handed. A key without
    // its scope would send a typed destination's approval to the profile dictionary, which is
    // the old shape with one more file.
    [Fact]
    public void TrustChanged_CarriesTheKeyWithItsScope()
    {
        RdpCertificateTrustStore store = new();
        List<RdpTrustKey> written = [];
        store.TrustChanged += (key, _) => written.Add(key);

        store.Trust(TypedOwner, Thumbprint);
        store.Trust(ProfileOwner, Thumbprint);

        Assert.Equal([TypedOwner, ProfileOwner], written);
        Assert.Equal(RdpTrustScope.TypedDestination, written[0].Scope);
    }

    [Theory]
    [InlineData("prod.example")]
    [InlineData("PROD.example")]
    [InlineData("  Prod.Example  ")]
    public void ForTypedDestination_SpellsTheHostOneWay(string typed)
    {
        RdpTrustKey key = RdpTrustKey.ForTypedDestination(typed);

        Assert.Equal("prod.example", key.Identity);
        Assert.Equal(RdpTrustKey.ForTypedDestination("prod.example"), key);
    }

    // The control that keeps the normalization meaningful: a profile identifier is NOT
    // normalized, because it is whatever the inventory holds and is compared ordinal there.
    [Fact]
    public void ForProfile_KeepsTheIdentifierExactly()
    {
        Assert.Equal("  Prod  ", RdpTrustKey.ForProfile("  Prod  ").Identity);
        Assert.NotEqual(RdpTrustKey.ForProfile("prod"), RdpTrustKey.ForProfile("PROD"));
    }

    [Fact]
    public void ForTypedDestination_RefusesAnEmptyHost()
    {
        Assert.ThrowsAny<ArgumentException>(() => RdpTrustKey.ForTypedDestination("   "));
        Assert.ThrowsAny<ArgumentException>(() => RdpTrustKey.ForProfile(string.Empty));
    }

    private static RdpCertificateEntry Entry(string thumbprint)
        => new(thumbprint, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
}
