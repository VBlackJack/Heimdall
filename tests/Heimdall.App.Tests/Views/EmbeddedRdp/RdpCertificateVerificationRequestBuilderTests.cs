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
        Assert.Equal("profile-1", request.ProfileId);
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

    private static ServerProfileDto Profile(string? displayName) => new()
    {
        Id = "profile-1",
        DisplayName = displayName ?? string.Empty,
        RemoteServer = "dc-pool.example.com",
        RemotePort = 3389,
    };
}
