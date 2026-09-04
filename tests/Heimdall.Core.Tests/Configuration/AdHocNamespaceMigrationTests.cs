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
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests.Configuration;

/// <summary>
/// Whether a profile saved before the quick-connect namespace was reserved is moved out of it,
/// and whether its approvals go with it.
/// </summary>
/// <remarks>
/// Reserving the namespace at the import stopped a new profile entering. It did nothing about the
/// profiles already in the file, which is what these measure - and nothing about their approvals,
/// which is the half that actually causes the harm: rename the profile and stop, and its approval
/// is still sitting under the identifier the palette mints for a typed destination.
/// </remarks>
public sealed class AdHocNamespaceMigrationTests
{
    private const string Reserved = "adhoc-rdp-prod.example";
    private const string Thumbprint = "SHA256:AA:BB:CC:DD:01";

    [Fact]
    public void AProfileInTheReservedNamespaceIsPlannedForRenaming()
    {
        List<ServerProfileDto> servers = [Profile(Reserved), Profile("ordinary")];

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => "fresh-1");

        Assert.Equal("fresh-1", Assert.Contains(Reserved, renames));
        Assert.DoesNotContain("ordinary", renames);
    }

    [Fact]
    public void AnInventoryWithNothingReservedPlansNothing()
        => Assert.Empty(
            AdHocNamespaceMigration.Plan([Profile("ordinary")], () => "fresh-1"));

    // The whole point. Renaming the profile without moving its approvals leaves the approval under
    // the identifier the palette mints for a destination typed by hand, with the profile no longer
    // even visible as its source.
    [Fact]
    public void TheProfilesApprovalsMoveWithItAndNothingIsLeftUnderTheOldIdentifier()
    {
        List<ServerProfileDto> servers = [Profile(Reserved)];
        Dictionary<string, List<RdpCertificateEntry>> trusted = new(StringComparer.Ordinal)
        {
            [Reserved] = [Entry(Thumbprint)],
        };

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => "fresh-1");

        Assert.True(AdHocNamespaceMigration.Apply(renames, servers, trusted));

        Assert.Equal("fresh-1", servers[0].Id);
        Assert.Equal(Thumbprint, Assert.Single(Assert.Contains("fresh-1", trusted)).Thumbprint);

        // What a quick connect to prod.example would read. It must find nothing.
        Assert.DoesNotContain(Reserved, trusted);
    }

    // A reserved key with no profile behind it is a typed destination's own approval, granted by
    // someone who typed that host and said yes. Removing it would re-ask about a machine they
    // accepted, so it is deliberately left where it is.
    [Fact]
    public void ATypedDestinationsOwnApprovalIsLeftAlone()
    {
        List<ServerProfileDto> servers = [Profile("ordinary")];
        Dictionary<string, List<RdpCertificateEntry>> trusted = new(StringComparer.Ordinal)
        {
            ["adhoc-rdp-typed.example"] = [Entry(Thumbprint)],
        };

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => "fresh-1");

        Assert.False(AdHocNamespaceMigration.Apply(renames, servers, trusted));
        Assert.Equal(Thumbprint, Assert.Single(Assert.Contains("adhoc-rdp-typed.example", trusted)).Thumbprint);
    }

    // The control that keeps the assertions above meaningful: an ordinary profile keeps both its
    // identifier and its approvals, so the migration measures the reservation and not a pass that
    // rewrites everything.
    [Fact]
    public void AnOrdinaryProfileKeepsItsIdentifierAndItsApprovals()
    {
        List<ServerProfileDto> servers = [Profile("ordinary"), Profile(Reserved)];
        Dictionary<string, List<RdpCertificateEntry>> trusted = new(StringComparer.Ordinal)
        {
            ["ordinary"] = [Entry(Thumbprint)],
        };

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => "fresh-1");

        Assert.True(AdHocNamespaceMigration.Apply(renames, servers, trusted));

        Assert.Equal("ordinary", servers[0].Id);
        Assert.Equal(Thumbprint, Assert.Single(Assert.Contains("ordinary", trusted)).Thumbprint);
    }

    // A reserved profile with no approvals is still moved: the identifier itself is what the
    // palette misclassifies, quite apart from any certificate.
    [Fact]
    public void AReservedProfileWithNoApprovalsIsStillMoved()
    {
        List<ServerProfileDto> servers = [Profile(Reserved)];
        Dictionary<string, List<RdpCertificateEntry>> trusted = new(StringComparer.Ordinal);

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => "fresh-1");

        Assert.True(AdHocNamespaceMigration.Apply(renames, servers, trusted));
        Assert.Equal("fresh-1", servers[0].Id);
        Assert.False(AdHocProfileIds.IsAdHoc(servers[0].Id));
    }

    [Fact]
    public void TwoReservedProfilesGetTwoDifferentIdentifiers()
    {
        List<ServerProfileDto> servers = [Profile(Reserved), Profile("adhoc-ssh-other.example")];
        int minted = 0;

        IReadOnlyDictionary<string, string> renames =
            AdHocNamespaceMigration.Plan(servers, () => $"fresh-{++minted}");

        Assert.Equal(2, renames.Count);
        Assert.Equal(2, renames.Values.Distinct(StringComparer.Ordinal).Count());
    }

    private static ServerProfileDto Profile(string id) => new()
    {
        Id = id,
        DisplayName = "Lab",
        ConnectionType = "RDP",
        RemoteServer = "lab.example",
        RemotePort = 3389,
    };

    private static RdpCertificateEntry Entry(string thumbprint) =>
        new(thumbprint, DateTimeOffset.UnixEpoch);
}
