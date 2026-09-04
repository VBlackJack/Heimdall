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
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Which owner the builder files a pane's approval under, and where that decision comes from.
/// </summary>
/// <remarks>
/// The scope comes from the mark the minting code left on the profile copy, never from the
/// identifier's text. The rows below hold the identifier constant across the two answers, so a
/// builder that decided by reading the identifier would give the same answer twice and fail
/// one of them.
/// </remarks>
public sealed class RdpCertificateVerificationRequestBuilderScopeTests
{
    private const string SharedId = "adhoc-rdp-prod.example";
    private static readonly RdpCertificateProbeTarget Target = new("prod.example", 3389);

    [Fact]
    public void AMarkedCopy_IsFiledUnderItsHost_AsATypedDestination()
    {
        ServerProfileDto typed = Profile(SharedId, "  PROD.Example ");
        typed.MarkAsTypedDestination();

        RdpCertificateVerificationRequest request =
            RdpCertificateVerificationRequestBuilder.Build(typed, Target, "pane-1");

        Assert.Equal(RdpTrustKey.ForTypedDestination("prod.example"), request.Key);
        Assert.Equal(RdpTrustScope.TypedDestination, request.Key.Scope);
        Assert.Equal("prod.example", request.Key.Identity);
    }

    [Fact]
    public void AnUnmarkedCopyWithTheSameIdentifier_IsFiledUnderTheProfile()
    {
        ServerProfileDto saved = Profile(SharedId, "prod.example");

        RdpCertificateVerificationRequest request =
            RdpCertificateVerificationRequestBuilder.Build(saved, Target, "pane-1");

        Assert.Equal(RdpTrustKey.ForProfile(SharedId), request.Key);
    }

    // The mark outranks the session identity: a typed destination that adopted a pane key is
    // still the typed destination, filed under its host, not under the inventory identifier the
    // key was minted over.
    [Fact]
    public void AMarkedCopyThatAdoptedASessionKey_IsStillFiledUnderItsHost()
    {
        ServerProfileDto typed = Profile(SharedId, "prod.example");
        typed.MarkAsTypedDestination();
        typed.AdoptSessionIdentity(SessionIdCodec.Create(SharedId));

        RdpCertificateVerificationRequest request =
            RdpCertificateVerificationRequestBuilder.Build(typed, Target, "pane-1");

        Assert.Equal(RdpTrustKey.ForTypedDestination("prod.example"), request.Key);
    }

    // Every runtime copy of a profile goes through CloneFaithfully - the reconnect, the
    // duplicate, the multi-monitor coercion. A mark that the clone dropped would file the
    // reconnected session under the profile scope and reopen the collision on the second
    // connection only, which is the kind of defect that survives a first look.
    [Fact]
    public void CloneFaithfully_KeepsTheMark()
    {
        ServerProfileDto typed = Profile(SharedId, "prod.example");
        typed.MarkAsTypedDestination();

        ServerProfileDto clone = typed.CloneFaithfully();

        Assert.True(clone.IsTypedDestination);
        Assert.Equal(
            RdpTrustKey.ForTypedDestination("prod.example"),
            RdpCertificateVerificationRequestBuilder.Build(clone, Target, "pane-1").Key);
    }

    // And the one copy that must NOT keep it: saving a typed destination as a profile goes
    // through the server dialog, which builds a new profile. A saved profile is a profile,
    // whatever it was saved from, and its approvals are its own.
    [Fact]
    public void TheServerDialogRoundTrip_DropsTheMark()
    {
        ServerProfileDto typed = Profile(SharedId, "prod.example");
        typed.MarkAsTypedDestination();

        ServerProfileDto saved = ServerDialogViewModel.FromDto(typed).ToDto();
        // The save path assigns the new profile its identifier after the dialog; the dialog
        // itself leaves it empty.
        saved.Id = Guid.NewGuid().ToString();

        Assert.False(saved.IsTypedDestination);
        Assert.Equal(RdpTrustScope.Profile, RdpCertificateVerificationRequestBuilder.Build(saved, Target, "pane-1").Key.Scope);
    }

    private static ServerProfileDto Profile(string id, string host) => new()
    {
        Id = id,
        DisplayName = "Production",
        RemoteServer = host,
        RemotePort = 3389,
        ConnectionType = "RDP",
    };
}
