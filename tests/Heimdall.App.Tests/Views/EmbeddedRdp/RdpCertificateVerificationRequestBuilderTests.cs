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
/// What a pane asks the verifier for, and in particular which surface it says must ask.
/// </summary>
public sealed class RdpCertificateVerificationRequestBuilderTests
{
    [Fact]
    public void TheRequestCarriesTheScopeOfThePaneThatBuiltIt()
    {
        // Lose this field and every question is refused, because the presenter has no surface
        // to put it on. Keep it and route it to the wrong pane and the user approves a
        // certificate for a machine they were not looking at. It is the one field here whose
        // value is a security property, which is why it is measured rather than read.
        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            Profile(displayName: "Production"),
            new RdpCertificateProbeTarget("127.0.0.1", 53211),
            "pane-7");

        Assert.Equal("pane-7", request.PromptScopeId);
    }

    [Fact]
    public void TheRequestProbesTheTargetItWasGiven_NotTheProfilesOwnAddress()
    {
        // The tunnel case: the certificate that matters is the one answering at the local end
        // of the tunnel, which is not the address the profile names.
        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            Profile(displayName: "Production"),
            new RdpCertificateProbeTarget("127.0.0.1", 53211),
            "pane-7");

        Assert.Equal("127.0.0.1", request.Host);
        Assert.Equal(53211, request.Port);
    }

    [Fact]
    public void TheProfileIsNamedAsTheUserNamedIt()
    {
        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            Profile(displayName: "Production"),
            new RdpCertificateProbeTarget("dc-pool.example.com", 3389),
            "pane-7");

        Assert.Equal("Production", request.ProfileName);
        Assert.Equal("profile-1", request.Key.Identity);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnnamedProfileIsCalledByItsAddress(string? displayName)
    {
        // A question headed by an empty string names nothing at all.
        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            Profile(displayName),
            new RdpCertificateProbeTarget("dc-pool.example.com", 3389),
            "pane-7");

        Assert.Equal("dc-pool.example.com", request.ProfileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ARequestWithNoScopeIsRefusedAtTheSource(string? scopeId)
    {
        // Rather than built and refused later by the presenter, where the reason would be a
        // log line at connect time instead of a defect at the call site.
        _ = Assert.ThrowsAny<ArgumentException>(
            () => RdpCertificateVerificationRequestBuilder.Build(
                Profile("Production"),
                new RdpCertificateProbeTarget("dc-pool.example.com", 3389),
                scopeId!));
    }

    [Fact]
    public void ASplitPanesApprovalIsFiledUnderTheProfile_NotUnderThePane()
    {
        // The pane runs on a copy of the profile whose Id has been replaced by a session-scoped
        // state key. Filing the approval under that key stores it where nothing will ever look
        // again: the key dies with the pane, and the next connection asks about the same
        // certificate as if it had never been approved.
        ServerProfileDto paneScoped = Profile("Production");
        paneScoped.AdoptSessionIdentity(SessionIdCodec.Create("profile-1"));

        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            paneScoped,
            new RdpCertificateProbeTarget("127.0.0.1", 53211),
            "pane-7");

        Assert.NotEqual("profile-1", paneScoped.Id);
        Assert.Equal("profile-1", request.Key.Identity);
    }

    [Fact]
    public void TwoPanesOfOneProfileAskAboutOneCertificateUnderOneIdentity()
    {
        // The coalescing scope is this identity - PaneRdpCertificateTrustPrompt.BuildKey passes
        // the profile as the scope - so two panes carrying two pane-scoped keys were two
        // questions about one certificate on one profile, and the user answered twice.
        ServerProfileDto firstPane = Profile("Production");
        firstPane.AdoptSessionIdentity(SessionIdCodec.Create("profile-1"));
        ServerProfileDto secondPane = Profile("Production");
        secondPane.AdoptSessionIdentity(SessionIdCodec.Create("profile-1"));

        RdpCertificateVerificationRequest first = RdpCertificateVerificationRequestBuilder.Build(
            firstPane,
            new RdpCertificateProbeTarget("127.0.0.1", 53211),
            "pane-7");
        RdpCertificateVerificationRequest second = RdpCertificateVerificationRequestBuilder.Build(
            secondPane,
            new RdpCertificateProbeTarget("127.0.0.1", 53212),
            "pane-8");

        Assert.NotEqual(firstPane.Id, secondPane.Id);
        Assert.Equal("profile-1", first.Key.Identity);
        Assert.Equal(first.Key.Identity, second.Key.Identity);
    }

    [Theory]
    [InlineData("profile-1")]
    [InlineData("7c9f0f2e-9a1c-4f1e-9b39-0c1d2e3f4a5b")]
    [InlineData("profile_1")]
    [InlineData("profile_zzzzzzzz")]
    public void AnInventoryProfileIsFiledUnderItself(string inventoryProfileId)
    {
        // Nothing is stripped from an identifier that was never minted for a session, including
        // one whose underscore is part of the name the user chose.
        ServerProfileDto server = Profile("Production");
        server.Id = inventoryProfileId;

        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            server,
            new RdpCertificateProbeTarget("dc-pool.example.com", 3389),
            "pane-7");

        Assert.Equal(inventoryProfileId, request.Key.Identity);
    }

    [Theory]
    [InlineData("prod_deadbeef")]
    [InlineData("prod_00000000")]
    [InlineData("PROD_DEADBEEF")]
    public void AnImportedProfileKeepsItsOwnTrustSetEvenWithAMintedShapedId(string importedId)
    {
        // The defect this file exists to keep out. An import preserves the identifier its file
        // carried, so an underscore and eight hexadecimal characters is a perfectly ordinary
        // profile identifier. Inverting the mint on it decoded "prod_deadbeef" to "prod" and
        // filed the approval the user gave for one machine into the trust set of an unrelated
        // profile, which then opened sessions on that certificate without asking anybody.
        ServerProfileDto imported = Profile("Lab");
        imported.Id = importedId;

        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            imported,
            new RdpCertificateProbeTarget("lab01", 3389),
            "pane-7");

        Assert.Equal(importedId, request.Key.Identity);
    }

    [Fact]
    public void AMintedIdentifierIsFiledUnderTheProfileItWasMintedFor()
    {
        // And the other half of the same rule, which telling imports apart must not cost: a key
        // minted for a pane belongs to the profile it was minted for. Lose this and a split
        // pane's approval is filed under an identifier that dies with the pane, so the
        // certificate is asked about again every time the split is recreated.
        //
        // Adopted rather than assigned, which is the whole distinction: the resulting string is
        // indistinguishable from the imported identifiers above, and only the act of adopting it
        // says which profile this copy belongs to. A bare assignment leaves the pane owning its
        // own key - the safe direction, and the reason this must be spelled deliberately.
        ServerProfileDto paneScoped = Profile("Production");
        paneScoped.Id = "prod";
        paneScoped.AdoptSessionIdentity(SessionIdCodec.Create("prod"));

        RdpCertificateVerificationRequest request = RdpCertificateVerificationRequestBuilder.Build(
            paneScoped,
            new RdpCertificateProbeTarget("127.0.0.1", 53211),
            "pane-7");

        Assert.Equal("prod", request.Key.Identity);
    }

    private static ServerProfileDto Profile(string? displayName) => new()
    {
        Id = "profile-1",
        DisplayName = displayName ?? string.Empty,
        RemoteServer = "dc-pool.example.com",
        RemotePort = 3389,
    };
}
