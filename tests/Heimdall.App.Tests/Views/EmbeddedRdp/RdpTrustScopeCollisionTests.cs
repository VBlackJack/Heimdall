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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// A destination typed by hand and a saved profile holding its identifier are two owners of
/// certificate trust, measured on the wire.
/// </summary>
/// <remarks>
/// <para>Run end to end through the real <see cref="RdpCertificateVerifier"/>, the real
/// <see cref="RdpCertificateTrustStore"/> and the real request builder, because the key is only
/// a means: what matters is whether a certificate approved for one lets the other connect without
/// being asked. The reproduction is the collision the audit recorded: the palette mints
/// <c>adhoc-rdp-&lt;host&gt;</c> for a host typed by hand, and a profile imported before that
/// namespace was reserved can hold the very same string.</para>
/// <para>The typed destination is told apart from the profile by the mark the minting code
/// leaves on it, never by its identifier: the two profiles below carry the same identifier and
/// reach the same host, and only <see cref="ServerProfileDto.MarkAsTypedDestination"/> separates
/// them.</para>
/// </remarks>
public sealed class RdpTrustScopeCollisionTests
{
    private const string Host = "prod.example";
    private const string SharedId = "adhoc-rdp-prod.example";
    private const string Thumbprint =
        "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    [Fact]
    public async Task ATypedDestinationsApproval_DoesNotSilenceTheProfileHoldingItsIdentifier()
    {
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        // The user quick-connects to the host and approves its certificate for good.
        RdpVerificationOutcome typed = await verifier.VerifyAsync(TypedRequest(), CancellationToken.None);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, typed);
        Assert.Equal(1, prompt.Asked);

        // The saved profile that happens to hold the same identifier meets the same certificate.
        // It approved nothing, so it must be asked.
        RdpVerificationOutcome profile = await verifier.VerifyAsync(ProfileRequest(), CancellationToken.None);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, profile);
        Assert.Equal(2, prompt.Asked);
        Assert.Equal(
            [RdpTrustKey.ForTypedDestination(Host), RdpTrustKey.ForProfile(SharedId)],
            prompt.KeysAsked);
    }

    [Fact]
    public async Task AProfilesApproval_DoesNotSilenceTheTypedDestinationSharingItsIdentifier()
    {
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        RdpVerificationOutcome profile = await verifier.VerifyAsync(ProfileRequest(), CancellationToken.None);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, profile);
        Assert.Equal(1, prompt.Asked);

        RdpVerificationOutcome typed = await verifier.VerifyAsync(TypedRequest(), CancellationToken.None);
        Assert.Equal(RdpVerificationOutcome.TrustedByUser, typed);
        Assert.Equal(2, prompt.Asked);
    }

    // And the property that makes the split worth having for the typed destination itself: its
    // approval is found again by the host, however the identifier was minted the next time. The
    // server row mints a fresh identifier per launch, so keying on it would ask every time.
    [Fact]
    public async Task ATypedDestinationsApproval_IsFoundAgainByItsHost_WhateverTheNextIdentifier()
    {
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustPermanently);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        _ = await verifier.VerifyAsync(TypedRequest(id: "adhoc-0123456789abcdef"), CancellationToken.None);
        RdpVerificationOutcome again = await verifier.VerifyAsync(
            TypedRequest(id: "adhoc-fedcba9876543210", host: "PROD.example"),
            CancellationToken.None);

        Assert.Equal(RdpVerificationOutcome.TrustedByUser, again);
        Assert.Equal(1, prompt.Asked);
    }

    // The session half: "just this once" for the typed destination is not "just this once" for
    // the profile sharing its identifier, and the second connection - the one that opens a
    // session - is the one that asks.
    [Fact]
    public async Task ASessionOnlyApprovalByTheTypedDestination_DoesNotSilenceTheProfile()
    {
        RdpCertificateTrustStore store = new();
        RecordingPrompt prompt = new(RdpTrustAnswer.TrustForSession);
        RdpCertificateVerifier verifier = new(new StubProbe(Thumbprint), store, prompt);

        _ = await verifier.VerifyAsync(TypedRequest(), CancellationToken.None);
        _ = await verifier.VerifyAsync(ProfileRequest(), CancellationToken.None);

        Assert.Equal(2, prompt.Asked);
    }

    private static RdpCertificateVerificationRequest TypedRequest(string id = SharedId, string host = Host)
    {
        ServerProfileDto typed = new()
        {
            Id = id,
            DisplayName = host,
            RemoteServer = host,
            RemotePort = 3389,
            ConnectionType = "RDP",
        };
        typed.MarkAsTypedDestination();
        return RdpCertificateVerificationRequestBuilder.Build(
            typed,
            new RdpCertificateProbeTarget(host, 3389),
            "pane-typed");
    }

    private static RdpCertificateVerificationRequest ProfileRequest()
    {
        ServerProfileDto saved = new()
        {
            Id = SharedId,
            DisplayName = "Production",
            RemoteServer = Host,
            RemotePort = 3389,
            ConnectionType = "RDP",
        };
        return RdpCertificateVerificationRequestBuilder.Build(
            saved,
            new RdpCertificateProbeTarget(Host, 3389),
            "pane-profile");
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
                "CN=prod"));
    }

    private sealed class RecordingPrompt(RdpTrustAnswer answer) : IRdpCertificateTrustPrompt
    {
        private readonly List<RdpTrustKey> _keysAsked = [];

        public int Asked => _keysAsked.Count;

        public IReadOnlyList<RdpTrustKey> KeysAsked => _keysAsked;

        public Task<RdpTrustAnswer> AskAsync(
            RdpCertificatePromptContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            Assert.NotNull(context.TrustKey);
            _keysAsked.Add(context.TrustKey.Value);
            return Task.FromResult(answer);
        }
    }
}
