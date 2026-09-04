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
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// One profile's approval, and whether another profile can read it.
/// </summary>
/// <remarks>
/// <para>Run end to end through the real <see cref="RdpCertificateVerifier"/> and the real
/// <see cref="RdpCertificateTrustStore"/> rather than asserted on the identifier the builder
/// returns, because the identifier is only a means: what matters is whether the certificate a
/// person approved for one machine lets an unrelated profile connect without being asked. The
/// builder-level assertions in <c>RdpCertificateVerificationRequestBuilderTests</c> say which
/// identifier comes out; this says what that costs.</para>
/// <para>The reproduction is an import: <c>ProfileImportService</c> preserves the identifier the
/// incoming file carried, so two profiles whose identifiers are <c>prod</c> and
/// <c>prod_deadbeef</c> can both exist, and the second's identifier has exactly the shape
/// <see cref="SessionIdCodec"/> mints.</para>
/// </remarks>
public sealed class RdpTrustIdentityCollisionTests
{
    private const string ProductionId = "prod";
    private const string LabId = "prod_deadbeef";
    private const string Thumbprint =
        "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public async Task AnApprovalGivenForOneProfileIsNotReadableByTheProfileNamedByItsPrefix()
    {
        // The reproduction. The user connects "Lab", is asked about its certificate, and
        // approves it permanently. Nothing about that may teach "Production" anything.
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        RdpVerificationOutcome labOutcome = await verifier.VerifyAsync(
            RequestFor(LabId, "Lab"),
            CancellationToken.None);

        Assert.Equal(RdpVerificationOutcome.TrustedByUser, labOutcome);
        Assert.Equal(1, prompt.Asked);

        // Production now meets the same certificate. It has approved nothing, so it must ask.
        RdpCertificateTrustDecision seenByProduction = store.Evaluate(RdpTrustKey.ForProfile(ProductionId), Thumbprint);
        Assert.Equal(RdpCertificateTrustVerdict.Unknown, seenByProduction.Verdict);
        Assert.Equal(0, seenByProduction.AlreadyTrustedCount);

        RdpVerificationOutcome productionOutcome = await verifier.VerifyAsync(
            RequestFor(ProductionId, "Production"),
            CancellationToken.None);

        Assert.Equal(2, prompt.Asked);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, productionOutcome);
        Assert.Equal(
            new[] { LabId, ProductionId },
            prompt.ProfileIdsAsked);
    }

    [Fact]
    public async Task ASessionOnlyApprovalByOneProfileDoesNotSilenceTheOther()
    {
        // The session half of the same rule, which the durable case above does not reach: the
        // store keeps session trust in a set of its own, keyed on the profile exactly as the
        // durable one is, so a shared key would let an approval given for the length of one run
        // answer for the other profile too. Under that defect the SECOND connection is the one
        // that never asks - and it is the one that opens a session.
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustForSession);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        _ = await verifier.VerifyAsync(RequestFor(LabId, "Lab"), CancellationToken.None);
        _ = await verifier.VerifyAsync(
            RequestFor(ProductionId, "Production"),
            CancellationToken.None);

        Assert.Equal(2, prompt.Asked);
    }

    [Fact]
    public async Task ASplitPaneOfOneProfileStillSharesThatProfilesApproval()
    {
        // And the fix does not undo the fix before it. A pane key names no profile, so it is
        // decoded: the approval given in the pane is the profile's, and the next connection of
        // that profile - pane or not - is not asked again.
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        ServerProfileDto pane = new()
        {
            Id = ProductionId,
            DisplayName = "Production",
            RemoteServer = "srv01",
            RemotePort = 3389,
        };

        // Through the supported route, which is what records that this copy is a pane OF
        // Production rather than a profile in its own right.
        pane.AdoptSessionIdentity(SessionIdCodec.Create(ProductionId));

        RdpCertificateVerificationRequest paneRequest =
            RdpCertificateVerificationRequestBuilder.Build(
                pane,
                new RdpCertificateProbeTarget("127.0.0.1", 53211),
                "pane-7");

        _ = await verifier.VerifyAsync(paneRequest, CancellationToken.None);
        RdpVerificationOutcome again = await verifier.VerifyAsync(
            RequestFor(ProductionId, "Production"),
            CancellationToken.None);

        Assert.Equal(1, prompt.Asked);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, again);
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(RdpTrustKey.ForProfile(LabId), Thumbprint).Verdict);
    }

    // The case the inventory lookup could not reach, and the reason nothing reads an inventory
    // here any more.
    //
    // Deleting a profile does not end the connection it started. So: import "Lab" as
    // prod_deadbeef, start it behind a slow tunnel, and delete it while the tunnel is still being
    // established. The certificate question arrives afterwards, still naming Lab. An
    // implementation that decided by looking Lab up - exact identifier first, invert the mint
    // only when it names no profile - now finds nothing, decodes prod_deadbeef to prod, and files
    // Lab's approval in Production's trust set. Production then meets that certificate and is
    // never asked.
    //
    // No refinement of that lookup helps: a minted identifier and a deleted profile's identifier
    // are both absent from the inventory, and absence is all the inventory can report. Nor could
    // the mint keep a record of its own, because an import can arrive carrying a string an
    // earlier session was minted. This is why the profile copy records what it is a copy OF, at
    // the instant its identifier is replaced.
    [Fact]
    public async Task AProfileAbsentFromTheInventoryStillKeepsItsApprovalToItself()
    {
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        // Nothing minted this identifier - it came in on an imported file - and no inventory
        // holds it, because the profile was deleted a moment ago.
        _ = await verifier.VerifyAsync(RequestFor(LabId, "Lab"), CancellationToken.None);

        Assert.Equal(
            RdpCertificateTrustVerdict.Trusted,
            store.Evaluate(RdpTrustKey.ForProfile(LabId), Thumbprint).Verdict);

        // The whole point: Production, which is still very much in the inventory, learned
        // nothing.
        Assert.Equal(
            RdpCertificateTrustVerdict.Unknown,
            store.Evaluate(RdpTrustKey.ForProfile(ProductionId), Thumbprint).Verdict);
    }

    private static RdpCertificateVerificationRequest RequestFor(
        string profileId,
        string displayName)
    {
        ServerProfileDto profile = new()
        {
            Id = profileId,
            DisplayName = displayName,
            RemoteServer = "srv01",
            RemotePort = 3389,
        };

        return RdpCertificateVerificationRequestBuilder.Build(
            profile,
            new RdpCertificateProbeTarget("srv01", 3389),
            "pane-" + profileId);
    }

    private sealed class StubProbe(string thumbprint) : IRdpCertificateProbe
    {
        public Task<RdpProbeResult> ProbeAsync(
            string host,
            int port,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RdpProbeResult(
                RdpProbeOutcome.CertificateObtained,
                thumbprint,
                "CN=srv01"));
    }

    private sealed class RecordingPrompt(RdpTrustAnswer answer) : IRdpCertificateTrustPrompt
    {
        private readonly List<string> _profileIdsAsked = [];

        public int Asked => _profileIdsAsked.Count;

        public IReadOnlyList<string> ProfileIdsAsked => _profileIdsAsked;

        public Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            _profileIdsAsked.Add(context.TrustKey?.Identity ?? string.Empty);
            return Task.FromResult(answer);
        }
    }
}
