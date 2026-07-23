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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class GatewayOverviewBuilderTests
{
    [Fact]
    public void Build_GroupsResolvedSessionsUnderGateway()
    {
        SshGatewayDto gateway = CreateGateway("gw-1", "Bastion", "bastion.example.test", 2222, "ops");
        ServerProfileDto first = CreateServer("server-1", "Zulu", "gw-1", sortOrder: 2);
        ServerProfileDto second = CreateServer("server-2", "Alpha", "gw-1", sortOrder: 1);

        GatewayOverview overview = GatewayOverviewBuilder.Build([gateway], [first, second]);

        GatewayOverviewGatewayGroup group = Assert.Single(overview.Gateways);
        Assert.Equal("gw-1", group.GatewayId);
        Assert.Equal("Bastion", group.GatewayName);
        Assert.Equal("ops@bastion.example.test:2222", group.Endpoint);
        Assert.Equal(2, group.Sessions.Count);
        Assert.Equal(["Alpha", "Zulu"], group.Sessions.Select(session => session.DisplayName).ToArray());
        Assert.Empty(overview.MissingReferences);
        Assert.Equal(2, overview.RoutedSessionCount);
        Assert.Equal(0, overview.MissingReferenceCount);
    }

    [Fact]
    public void Build_ResolvesGatewayReferencesIgnoringCase()
    {
        SshGatewayDto gateway = CreateGateway("ABC123", "Bastion", "bastion.example.test", 2222, "ops");
        ServerProfileDto server = CreateServer("server-1", "App", "abc123", sortOrder: 1);

        GatewayOverview overview = GatewayOverviewBuilder.Build([gateway], [server]);

        GatewayOverviewGatewayGroup group = Assert.Single(overview.Gateways);
        Assert.Equal("ABC123", group.GatewayId);
        GatewayOverviewSession session = Assert.Single(group.Sessions);
        Assert.Equal("server-1", session.Id);
        Assert.Empty(overview.MissingReferences);
        Assert.Equal(1, overview.RoutedSessionCount);
        Assert.Equal(0, overview.MissingReferenceCount);
    }

    [Fact]
    public void Build_IncludesConfiguredGatewaysWithoutSessions()
    {
        SshGatewayDto gateway = CreateGateway("gw-unused", "Unused", "unused.example.test", 22, "ops");

        GatewayOverview overview = GatewayOverviewBuilder.Build([gateway], []);

        GatewayOverviewGatewayGroup group = Assert.Single(overview.Gateways);
        Assert.Equal("Unused", group.GatewayName);
        Assert.Empty(group.Sessions);
        Assert.Equal(1, overview.GatewayCount);
        Assert.Equal(0, overview.RoutedSessionCount);
    }

    [Fact]
    public void Build_ResolvesParentGatewayName()
    {
        SshGatewayDto root = CreateGateway("gw-root", "Root", "root.example.test", 22, "ops");
        SshGatewayDto child = CreateGateway("gw-child", "Child", "child.example.test", 22, "ops");
        child.ParentGatewayId = "gw-root";

        GatewayOverview overview = GatewayOverviewBuilder.Build([root, child], []);

        GatewayOverviewGatewayGroup childGroup = overview.Gateways.Single(group => group.GatewayId == "gw-child");
        Assert.Equal("Root", childGroup.ParentGatewayName);
    }

    [Fact]
    public void Build_GroupsMissingReferencesByGatewayId()
    {
        ServerProfileDto first = CreateServer("server-1", "App 1", "gw-missing", sortOrder: 2);
        ServerProfileDto second = CreateServer("server-2", "App 2", "gw-missing", sortOrder: 1);
        ServerProfileDto ignored = CreateServer("server-3", "Direct", gatewayId: null, sortOrder: 0);

        GatewayOverview overview = GatewayOverviewBuilder.Build([], [first, second, ignored]);

        Assert.Empty(overview.Gateways);
        GatewayOverviewMissingReferenceGroup missing = Assert.Single(overview.MissingReferences);
        Assert.Equal("gw-missing", missing.GatewayId);
        Assert.Equal(["App 2", "App 1"], missing.Sessions.Select(session => session.DisplayName).ToArray());
        Assert.Equal(0, overview.RoutedSessionCount);
        Assert.Equal(2, overview.MissingReferenceCount);
    }

    [Fact]
    public void Build_IgnoresGatewayReferencesOnIneligibleProtocols()
    {
        SshGatewayDto gateway = CreateGateway("gw-1", "Bastion", "bastion.example.test", 22, "ops");
        ServerProfileDto telnet = CreateServer("server-1", "Telnet", "gw-1", sortOrder: 1);
        telnet.ConnectionType = "TELNET";
        ServerProfileDto ftp = CreateServer("server-2", "FTP", "gw-missing", sortOrder: 2);
        ftp.ConnectionType = "FTP";

        GatewayOverview overview = GatewayOverviewBuilder.Build([gateway], [telnet, ftp]);

        Assert.Empty(Assert.Single(overview.Gateways).Sessions);
        Assert.Empty(overview.MissingReferences);
        Assert.Equal(0, overview.RoutedSessionCount);
        Assert.Equal(0, overview.MissingReferenceCount);
    }

    private static SshGatewayDto CreateGateway(
        string id,
        string name,
        string host,
        int port,
        string user)
    {
        return new SshGatewayDto
        {
            Id = id,
            Name = name,
            Host = host,
            Port = port,
            User = user
        };
    }

    private static ServerProfileDto CreateServer(
        string id,
        string displayName,
        string? gatewayId,
        int sortOrder)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = displayName,
            ConnectionType = "SSH",
            RemoteServer = $"{displayName.Replace(" ", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant()}.example.test",
            SshPort = 22,
            SshGatewayId = gatewayId,
            SortOrder = sortOrder
        };
    }
}
