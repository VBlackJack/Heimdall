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
/// What a profile remembers about the RDP certificates it has been shown.
/// </summary>
/// <remarks>
/// The measured defect this exists to end: several machines answer to one name - a pool of
/// domain controllers, each with its own self-signed certificate - and Windows keeps
/// exactly ONE thumbprint per name. Each acceptance overwrites the previous one, so the
/// warning returns forever and never settles.
/// </remarks>
public sealed class RdpCertificateTrustTests
{
    private const string Profile = "profile-dc-pool";
    private const string FirstDc = "SHA256:AA:BB:01";
    private const string SecondDc = "SHA256:AA:BB:02";
    private const string ThirdDc = "SHA256:AA:BB:03";

    /// <summary>The first thumbprint as a hand-edited settings file may spell it.</summary>
    private const string LowerCaseFirstDc = "sha256:aa:bb:01";

    [Fact]
    public void Trust_ASecondThumbprintForTheSameProfile_KeepsTheFirst()
    {
        RdpCertificateTrustStore store = new();

        store.Trust(Profile, FirstDc);
        store.Trust(Profile, SecondDc);

        // THE defect, stated as an assertion. A store that replaced instead of adding
        // would still let the connection just accepted succeed, and only the NEXT one
        // landing on the other machine would ask again - which is the Windows loop moved
        // into Heimdall rather than fixed.
        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(Profile, FirstDc).Verdict);
        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(Profile, SecondDc).Verdict);
    }

    [Fact]
    public void Evaluate_AfterApprovingEveryMachineOfAPool_AsksAboutNoneOfThem()
    {
        RdpCertificateTrustStore store = new();

        foreach (string thumbprint in new[] { FirstDc, SecondDc, ThirdDc })
        {
            Assert.Equal(
                RdpCertificateTrustVerdict.Unknown,
                store.Evaluate(Profile, thumbprint).Verdict);
            store.Trust(Profile, thumbprint);
        }

        // Convergence is the whole promise: asked once per machine, then never again.
        // Under Windows this sequence never terminates.
        Assert.All(
            new[] { FirstDc, SecondDc, ThirdDc },
            thumbprint => Assert.Equal(
                RdpCertificateTrustVerdict.Trusted,
                store.Evaluate(Profile, thumbprint).Verdict));
    }

    [Fact]
    public void Evaluate_UnknownThumbprint_SaysHowManyAreAlreadyTrusted()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);
        store.Trust(Profile, SecondDc);

        RdpCertificateTrustDecision decision = store.Evaluate(Profile, ThirdDc);

        // The arbitration of 2026-08-23. Without this count the third prompt reads as the
        // same alarm as the first, when it in fact means "another machine behind this
        // name" - which on a pool is the normal situation, not a sign of tampering.
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, decision.Verdict);
        Assert.Equal(2, decision.AlreadyTrustedCount);
    }

    [Fact]
    public void Evaluate_FirstEverCertificate_ReportsNothingTrustedYet()
    {
        RdpCertificateTrustStore store = new();

        RdpCertificateTrustDecision decision = store.Evaluate(Profile, FirstDc);

        // The count has to distinguish "you have never approved anything here" from
        // "you already approved others", because the two deserve different wording.
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, decision.Verdict);
        Assert.Equal(0, decision.AlreadyTrustedCount);
    }

    [Fact]
    public void TrustForSession_IsNotDurableAndNeverReachesTheConfiguration()
    {
        RdpCertificateTrustStore store = new();
        List<string> persisted = [];
        store.TrustChanged += (profileId, _) => persisted.Add(profileId);

        store.TrustForSession(Profile, FirstDc);

        // "Just this once" must not silently become forever. The verdict is distinct so a
        // settings screen can show the durable set without inventing an entry, and nothing
        // is offered to the writer.
        Assert.Equal(
            RdpCertificateTrustVerdict.TrustedForSession,
            store.Evaluate(Profile, FirstDc).Verdict);
        Assert.Empty(store.GetApproved(Profile));
        Assert.Empty(persisted);
    }

    [Fact]
    public void Trust_RaisesTrustChangedOnceWithTheWholeSet()
    {
        RdpCertificateTrustStore store = new();
        List<IReadOnlyCollection<RdpCertificateEntry>> writes = [];
        store.TrustChanged += (_, set) => writes.Add(set);

        store.Trust(Profile, FirstDc);
        store.Trust(Profile, SecondDc);
        store.Trust(Profile, SecondDc);

        // The writer receives the SET, not the delta, so a persist cannot narrow it. The
        // duplicate raises nothing: re-approving is not a change.
        Assert.Equal(2, writes.Count);
        Assert.Single(writes[0]);
        Assert.Equal(2, writes[1].Count);
    }

    [Fact]
    public void Remove_ForgetsOneCertificateAndLeavesTheOthers()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);
        store.Trust(Profile, SecondDc);

        Assert.True(store.Remove(Profile, FirstDc));

        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(Profile, FirstDc).Verdict);
        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(Profile, SecondDc).Verdict);
        Assert.False(store.Remove(Profile, FirstDc));
    }

    [Fact]
    public void Evaluate_TrustIsPerProfile_AndDoesNotLeakToAnother()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);

        // Two profiles may point at the same name with different expectations; approving
        // for one says nothing about the other.
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate("another-profile", FirstDc).Verdict);
    }

    [Fact]
    public void LoadFromConfig_ReplacesDurableTrustAndLeavesSessionTrustAlone()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);
        store.TrustForSession(Profile, ThirdDc);

        store.LoadFromConfig([(Profile, new[] { Entry(SecondDc) })]);

        // A load is the file speaking, so it replaces what the file owns - and only that.
        // Session trust belongs to the run; dropping it here would re-ask for a machine
        // the user accepted minutes ago.
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(Profile, FirstDc).Verdict);
        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(Profile, SecondDc).Verdict);
        Assert.Equal(
            RdpCertificateTrustVerdict.TrustedForSession,
            store.Evaluate(Profile, ThirdDc).Verdict);
    }

    [Fact]
    public void Decide_MatchLateInTheSet_IsStillFound()
    {
        string[] approved = [FirstDc, SecondDc, ThirdDc];

        // The lookup compares every member rather than returning on the first hit, so the
        // time it takes says how many certificates are trusted and not which one matched.
        // A rewrite that breaks out early would still pass this - it is here to stop a
        // rewrite that breaks out and gets the ANSWER wrong.
        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            RdpCertificateTrust.Decide(ThirdDc, approved, []).Verdict);
    }

    [Fact]
    public void Trust_StampsWhenTheUserApproved_FromTheInjectedClock()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));
        RdpCertificateTrustStore store = new(clock);

        store.Trust(Profile, FirstDc);

        // Asserted against a known instant rather than "a stamp exists", which passes
        // whatever the code writes there.
        RdpCertificateEntry entry = Assert.Single(store.GetApproved(Profile));
        Assert.Equal(clock.GetUtcNow(), entry.FirstTrusted);
    }

    [Fact]
    public void Trust_ReapprovingTheSameCertificate_KeepsTheOriginalStamp()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));
        RdpCertificateTrustStore store = new(clock);
        store.Trust(Profile, FirstDc);
        DateTimeOffset firstApproval = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromDays(3));
        store.Trust(Profile, FirstDc);

        // The stamp answers "since when has this been trusted". Refreshing it on every
        // reconnection would erase the only fact it carries.
        RdpCertificateEntry entry = Assert.Single(store.GetApproved(Profile));
        Assert.Equal(firstApproval, entry.FirstTrusted);
    }

    [Fact]
    public void LoadFromConfig_RestoresWhatAPreviousRunWrote()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));
        RdpCertificateTrustStore writer = new(clock);
        Dictionary<string, IReadOnlyCollection<RdpCertificateEntry>> persisted = [];
        writer.TrustChanged += (profileId, set) => persisted[profileId] = set;

        writer.Trust(Profile, FirstDc);
        writer.Trust(Profile, SecondDc);

        RdpCertificateTrustStore reloaded = new(clock);
        reloaded.LoadFromConfig(persisted.Select(pair => (pair.Key, (IEnumerable<RdpCertificateEntry>)pair.Value)));

        // The whole promise is "asked once per machine, then never again". Without a
        // round trip that survives a restart, convergence starts over every launch and
        // the feature is worth nothing.
        Assert.All(
            new[] { FirstDc, SecondDc },
            thumbprint => Assert.Equal(
                RdpCertificateTrustVerdict.Trusted,
                reloaded.Evaluate(Profile, thumbprint).Verdict));
        Assert.Equal(
            clock.GetUtcNow(),
            reloaded.GetApproved(Profile).First().FirstTrusted);
    }

    [Fact]
    public void TrustChanged_CarriesTheWholeSetSoAWriteCannotNarrowIt()
    {
        RdpCertificateTrustStore store = new();
        List<IReadOnlyCollection<RdpCertificateEntry>> writes = [];
        store.TrustChanged += (_, set) => writes.Add(set);

        store.Trust(Profile, FirstDc);
        store.Trust(Profile, SecondDc);
        store.Remove(Profile, FirstDc);

        // A persister that received a delta could write a narrower set than the one held,
        // which is the same loss as replacing - just one layer out.
        Assert.Equal(3, writes.Count);
        Assert.Equal(2, writes[1].Count);
        Assert.Equal(SecondDc, Assert.Single(writes[2]).Thumbprint);
    }

    [Fact]
    public void Decide_CountsEachCertificateOnce_SessionAndDurableTogether()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);
        store.TrustForSession(Profile, FirstDc);
        store.TrustForSession(Profile, SecondDc);

        // What the user reads is "you already trust N certificates for this name", so N
        // counts machines, not decisions - the same thumbprint approved twice is one.
        Assert.Equal(2, store.Evaluate(Profile, ThirdDc).AlreadyTrustedCount);
    }

    [Fact]
    public void LoadFromConfig_ThumbprintWrittenInAnotherCase_IsRecognisedWhenThatMachineAnswers()
    {
        RdpCertificateTrustStore store = new();

        // Hand-editing settings.json is currently the only way to manage these entries, so
        // a lower-case thumbprint in the file is a reachable state. The probe always
        // renders upper case, so both halves have to state ONE rule about case, or the
        // profile is asked about a machine it already trusts on every single connect.
        store.LoadFromConfig([(Profile, new[] { Entry(LowerCaseFirstDc) })]);

        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(Profile, FirstDc).Verdict);
    }

    [Fact]
    public void Trust_AnEquivalentThumbprintInAnotherCase_StillLandsInTheDurableSetOnce()
    {
        RdpCertificateTrustStore store = new();
        List<IReadOnlyCollection<RdpCertificateEntry>> writes = [];
        store.TrustChanged += (_, set) => writes.Add(set);

        store.Trust(Profile, LowerCaseFirstDc);
        store.Trust(Profile, FirstDc);

        // Dedupe and lookup have to agree. When they do not, the second call is swallowed
        // as a duplicate while the lookup keeps missing, and no answer the user gives can
        // ever end the question.
        RdpCertificateEntry stored = Assert.Single(store.GetApproved(Profile));
        Assert.Equal(FirstDc, stored.Thumbprint);
        Assert.Single(writes);
    }

    [Fact]
    public void Evaluate_SessionThumbprintApprovedInAnotherCase_IsStillTrustedForTheRun()
    {
        RdpCertificateTrustStore store = new();

        store.TrustForSession(Profile, LowerCaseFirstDc);

        Assert.Equal(
            RdpCertificateTrustVerdict.TrustedForSession,
            store.Evaluate(Profile, FirstDc).Verdict);
    }

    [Fact]
    public void Remove_ThumbprintGivenInAnotherCase_ForgetsTheStoredOne()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(Profile, FirstDc);

        // The revocation half of the same rule: a caller that spells the thumbprint the
        // other way must not be told there was nothing to forget.
        Assert.True(store.Remove(Profile, LowerCaseFirstDc));
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(Profile, FirstDc).Verdict);
    }

    private static RdpCertificateEntry Entry(string thumbprint)
        => new(thumbprint, new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero));

    /// <summary>A clock the test moves by hand, so no assertion depends on wall time.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
