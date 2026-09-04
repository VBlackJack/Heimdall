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
/// The check that runs before an RDP session is opened, and what the caller may do with
/// each answer.
/// </summary>
/// <remarks>
/// The safety argument of the whole feature rests on one rule: Heimdall may relax the
/// Windows check only where it has performed an equivalent one itself. Most of what is
/// asserted here is that rule holding in the cases where it would be tempting to cut a
/// corner.
/// </remarks>
public sealed class RdpCertificateVerifierTests
{
    private const string ProfileId = "profile-dc-pool";
    private const string Thumb = "SHA256:AA:BB:01";

    [Fact]
    public async Task Verify_ProbeFailed_ChangesNothingAndDoesNotAsk()
    {
        FakePrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = Build(
            new RdpProbeResult(RdpProbeOutcome.Unreachable, Detail: "timed out"),
            new RdpCertificateTrustStore(),
            prompt);

        RdpVerificationOutcome outcome = await verifier.VerifyAsync(Request(), default);

        // THE rule. A probe that could not run has verified nothing, so the connection has
        // to proceed exactly as it would have without this feature. Reporting anything
        // else here would let a caller switch off the Windows check on the strength of a
        // check that never happened - strictly worse than never having built any of this.
        Assert.Equal(RdpVerificationOutcome.CouldNotVerify, outcome);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public async Task Verify_HandshakeFailed_ChangesNothing()
    {
        RdpVerificationOutcome outcome = await Build(
                new RdpProbeResult(RdpProbeOutcome.HandshakeFailed, Detail: "protocol error"),
                new RdpCertificateTrustStore(),
                new FakePrompt(RdpTrustAnswer.TrustPermanently))
            .VerifyAsync(Request(), default);

        Assert.Equal(RdpVerificationOutcome.CouldNotVerify, outcome);
    }

    [Fact]
    public async Task Verify_EndpointOffersNoCertificate_IsNotTreatedAsVerified()
    {
        FakePrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpVerificationOutcome outcome = await Build(
                new RdpProbeResult(RdpProbeOutcome.TlsNotOffered),
                new RdpCertificateTrustStore(),
                prompt)
            .VerifyAsync(Request(), default);

        // Standard RDP security: there is no certificate, so there is nothing to approve
        // and nothing to relax. Asking the user here would offer to trust something that
        // does not exist.
        Assert.Equal(RdpVerificationOutcome.NoCertificateOffered, outcome);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public async Task Verify_AlreadyTrusted_DoesNotAskAgain()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(RdpTrustKey.ForProfile(ProfileId), Thumb);
        FakePrompt prompt = new(RdpTrustAnswer.Refuse);

        RdpVerificationOutcome outcome = await Build(Obtained(Thumb), store, prompt)
            .VerifyAsync(Request(), default);

        // Convergence, seen from the caller: once approved, the machine is silent forever.
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, outcome);
        Assert.Equal(0, prompt.Asked);
    }

    [Fact]
    public async Task Verify_UnknownCertificate_AsksAndSaysHowManyAreAlreadyTrusted()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(RdpTrustKey.ForProfile(ProfileId), "SHA256:AA:BB:02");
        store.Trust(RdpTrustKey.ForProfile(ProfileId), "SHA256:AA:BB:03");
        FakePrompt prompt = new(RdpTrustAnswer.TrustPermanently);

        await Build(Obtained(Thumb, subject: "CN=dc04"), store, prompt).VerifyAsync(Request(), default);

        // The arbitration reaching the user. Without the count, the third prompt reads as
        // the same alarm as the first, when it means "another machine behind this name".
        RdpCertificatePromptContext asked = Assert.Single(prompt.Contexts);
        Assert.Equal(2, asked.AlreadyTrustedCount);
        Assert.Equal(Thumb, asked.Thumbprint);
        Assert.Equal("CN=dc04", asked.Subject);
        Assert.Equal("dc-pool.example.com", asked.Host);
    }

    [Fact]
    public async Task Verify_UserTrustsPermanently_RemembersItWithWhatWasObserved()
    {
        RdpCertificateTrustStore store = new();

        RdpVerificationOutcome outcome = await Build(
                Obtained(Thumb, subject: "CN=dc04", issuer: "CN=dc04"),
                store,
                new FakePrompt(RdpTrustAnswer.TrustPermanently))
            .VerifyAsync(Request(), default);

        // Subject and issuer are carried across so a settings screen can name the machine
        // rather than show forty hexadecimal pairs.
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, outcome);
        RdpCertificateEntry stored = Assert.Single(store.GetApproved(RdpTrustKey.ForProfile(ProfileId)));
        Assert.Equal(Thumb, stored.Thumbprint);
        Assert.Equal("CN=dc04", stored.Subject);
        Assert.Equal("CN=dc04", stored.Issuer);
    }

    [Fact]
    public async Task Verify_UserTrustsForThisRunOnly_NothingBecomesDurable()
    {
        RdpCertificateTrustStore store = new();

        RdpVerificationOutcome outcome = await Build(
                Obtained(Thumb),
                store,
                new FakePrompt(RdpTrustAnswer.TrustForSession))
            .VerifyAsync(Request(), default);

        // The connection is allowed, and nothing is written. "Just this once" that quietly
        // persisted would be a promise broken where it costs most.
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, outcome);
        Assert.Empty(store.GetApproved(RdpTrustKey.ForProfile(ProfileId)));
        Assert.Equal(
            RdpCertificateTrustVerdict.TrustedForSession,
            store.Evaluate(RdpTrustKey.ForProfile(ProfileId), Thumb).Verdict);
    }

    [Fact]
    public async Task Verify_UserRefuses_RemembersNothingAndStopsTheConnection()
    {
        RdpCertificateTrustStore store = new();

        RdpVerificationOutcome outcome = await Build(
                Obtained(Thumb),
                store,
                new FakePrompt(RdpTrustAnswer.Refuse))
            .VerifyAsync(Request(), default);

        // A refusal that still allowed the session would make the question decorative.
        Assert.Equal(RdpVerificationOutcome.RefusedByUser, outcome);
        Assert.Empty(store.GetApproved(RdpTrustKey.ForProfile(ProfileId)));
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(RdpTrustKey.ForProfile(ProfileId), Thumb).Verdict);
    }

    [Fact]
    public async Task Verify_QuestionReachedNobody_IsNotReportedAsARefusal()
    {
        // The defect this separates out. Every way of failing to ask - a pane torn down between
        // the probe and the question, a surface already unregistered - used to come back as
        // RefusedByUser, and the pane then told its user "you did not approve the certificate
        // this server presented" about a question that was never put to them.
        RdpCertificateTrustStore store = new();

        RdpVerificationOutcome outcome = await Build(
                Obtained(Thumb),
                store,
                new FakePrompt(RdpTrustAnswer.NotAsked))
            .VerifyAsync(Request(), default);

        Assert.Equal(RdpVerificationOutcome.QuestionNotAsked, outcome);
        Assert.NotEqual(RdpVerificationOutcome.RefusedByUser, outcome);

        // Nothing was approved, so nothing is written - the same as a refusal, and the reason
        // the distinction is safe to make at all.
        Assert.Empty(store.GetApproved(RdpTrustKey.ForProfile(ProfileId)));
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(RdpTrustKey.ForProfile(ProfileId), Thumb).Verdict);
    }

    [Fact]
    public async Task Verify_TrustIsPerProfile_SoAnotherProfileIsStillAsked()
    {
        RdpCertificateTrustStore store = new();
        store.Trust(RdpTrustKey.ForProfile("some-other-profile"), Thumb);
        FakePrompt prompt = new(RdpTrustAnswer.Refuse);

        await Build(Obtained(Thumb), store, prompt).VerifyAsync(Request(), default);

        Assert.Equal(1, prompt.Asked);
    }

    [Fact]
    public async Task Verify_UnknownCertificate_TellsThePromptWhichProfileIsAsking()
    {
        FakePrompt prompt = new(RdpTrustAnswer.Refuse);

        await Build(Obtained(Thumb), new RdpCertificateTrustStore(), prompt)
            .VerifyAsync(Request(), default);

        // Trust is per profile, so a presenter has to be able to keep two profiles'
        // questions apart. Without this, one dialog naming profile A could supply the
        // answer for profile B - durable trust granted from a question never shown.
        Assert.Equal(RdpTrustKey.ForProfile(ProfileId), Assert.Single(prompt.Contexts).TrustKey);
    }

    private static RdpCertificateVerificationRequest Request()
        => new(RdpTrustKey.ForProfile(ProfileId), "DC pool", "dc-pool.example.com", 3389);

    private static RdpProbeResult Obtained(
        string thumbprint,
        string? subject = null,
        string? issuer = null)
        => new(RdpProbeOutcome.CertificateObtained, thumbprint, subject, issuer);

    private static RdpCertificateVerifier Build(
        RdpProbeResult probed,
        RdpCertificateTrustStore store,
        IRdpCertificateTrustPrompt prompt)
        => new(new FakeProbe(probed), store, prompt);

    private sealed class FakeProbe(RdpProbeResult result) : IRdpCertificateProbe
    {
        public Task<RdpProbeResult> ProbeAsync(string host, int port, CancellationToken ct)
            => Task.FromResult(result);
    }

    private sealed class FakePrompt(RdpTrustAnswer answer) : IRdpCertificateTrustPrompt
    {
        public List<RdpCertificatePromptContext> Contexts { get; } = [];

        public int Asked => Contexts.Count;

        public Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            Contexts.Add(context);
            return Task.FromResult(answer);
        }
    }
}
