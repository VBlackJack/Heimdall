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

using System.Diagnostics;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed partial class ServerListSelectionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SelectSingle_ReplacesExistingMultiSelection()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        AssertSelection(fixture.ViewModel, "alpha");
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ToggleSelection_AddsItemAndUpdatesPrimary()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ToggleSelection_RemovingPrimaryFallsBackToLastRemaining()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        AssertSelection(fixture.ViewModel, "alpha");
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ExtendSelectionTo_WithoutAnchorBehavesLikeSingleSelect()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.ExtendSelectionTo(fixture.ServerById("beta"));

        AssertSelection(fixture.ViewModel, "beta");
    }

    [Fact]
    public async Task ExtendSelectionTo_UsesVisibleLeafOrderAndKeepsAnchorFixed()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops", "ops/a", "ops/b", "ops/c", "ops/d"),
            CreateServer("alpha", "Alpha", "ops/a"),
            CreateServer("beta", "Beta", "ops/b"),
            CreateServer("gamma", "Gamma", "ops/c"),
            CreateServer("delta", "Delta", "ops/d"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        fixture.ViewModel.ExtendSelectionTo(fixture.ServerById("delta"));

        AssertSelection(fixture.ViewModel, "beta", "delta", "gamma");
        Assert.Equal("delta", fixture.ViewModel.SelectedServer?.Id);

        fixture.ViewModel.ExtendSelectionTo(fixture.ServerById("alpha"));

        AssertSelection(fixture.ViewModel, "alpha", "beta");
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ExtendSelectionTo_IgnoresCollapsedLeavesOutsideVisibleOrder()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("root", "root/visible"),
            CreateServer("anchor", "root/visible/anchor", "root/visible"),
            CreateServer("target", "root/visible/target", "root/visible"),
            CreateServer("hidden", "root/hidden", "root/hidden"));

        fixture.CollapseGroup("root/hidden");
        fixture.ViewModel.SelectSingle(fixture.ServerById("anchor"));

        fixture.ViewModel.ExtendSelectionTo(fixture.ServerById("target"));

        AssertSelection(fixture.ViewModel, "anchor", "target");
        Assert.DoesNotContain(fixture.ServerById("hidden"), fixture.ViewModel.SelectedItems);
    }

    [Fact]
    public async Task CollapseGroup_PurgesHiddenSelectionAndKeepsVisiblePrimaryAnchorAndBulkContext()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("root", "root/hidden", "root/visible"),
            CreateServer("hidden", "Hidden", "root/hidden"),
            CreateServer("visible-a", "Visible A", "root/visible"),
            CreateServer("visible-b", "Visible B", "root/visible"));
        ServerItemViewModel hidden = fixture.ServerById("hidden");
        ServerItemViewModel visibleA = fixture.ServerById("visible-a");
        ServerItemViewModel visibleB = fixture.ServerById("visible-b");
        fixture.ViewModel.SelectSingle(hidden);
        fixture.ViewModel.ToggleSelection(visibleA);
        fixture.ViewModel.ToggleSelection(visibleB);

        fixture.CollapseGroup("root/hidden");

        AssertSelection(fixture.ViewModel, "visible-a", "visible-b");
        Assert.False(hidden.IsSelected);
        Assert.Same(visibleB, fixture.ViewModel.SelectedServer);
        BulkSelectionContext bulkContext = Assert.IsType<BulkSelectionContext>(
            fixture.ViewModel.CreateBulkSelectionContext());
        Assert.Equal(
            ["visible-a", "visible-b"],
            bulkContext.Items.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Same(visibleB, bulkContext.Primary);

        fixture.ViewModel.ExtendSelectionTo(visibleA);

        AssertSelection(fixture.ViewModel, "visible-a", "visible-b");
        Assert.Same(visibleA, fixture.ViewModel.SelectedServer);
    }

    [Fact]
    public async Task SearchFilter_PurgesInvisibleSelectionsAndKeepsVisiblePrimary()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.ViewModel.SearchText = "Beta";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        AssertVisibleServerIds(fixture.ViewModel, "beta");
        AssertSelection(fixture.ViewModel, "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task SearchFilter_ClearsSelectionWhenNothingRemainsVisible()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        fixture.ViewModel.SearchText = "does-not-exist";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        AssertVisibleServerIds(fixture.ViewModel);
        Assert.Empty(fixture.ViewModel.SelectedItems);
        Assert.Null(fixture.ViewModel.SelectedServer);
        Assert.False(fixture.ViewModel.HasSelection);
    }

    [Fact]
    public async Task SearchFilter_DebouncesNonEmptyTextAndAppliesLatestTerm()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));

        fixture.ViewModel.SearchText = "Alpha";
        fixture.ViewModel.SearchText = "Gamma";

        Assert.Equal(3, fixture.ViewModel.Servers.Count);
        Assert.True(fixture.ViewModel.IsFilterPending);

        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        AssertVisibleServerIds(fixture.ViewModel, "gamma");
        Assert.False(fixture.ViewModel.IsFilterPending);
    }

    [Fact]
    public async Task SearchFilter_ClearingTextAppliesImmediately()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"));

        fixture.ViewModel.SearchText = "Beta";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel, "beta");

        fixture.ViewModel.SearchText = "";

        AssertVisibleServerIds(fixture.ViewModel, "alpha", "beta");
    }

    [Fact]
    public async Task SelectedServerSetter_SelectsSingleItem()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.ViewModel.SelectedServer = fixture.ServerById("alpha");

        AssertSelection(fixture.ViewModel, "alpha");
    }

    [Fact]
    public async Task SelectedServerSetter_NullClearsSelection()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        fixture.ViewModel.SelectedServer = null;

        Assert.Empty(fixture.ViewModel.SelectedItems);
        Assert.Null(fixture.ViewModel.SelectedServer);
    }

    [Fact]
    public async Task ToggleSelection_UpdatesSelectionCount()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "ops/alpha", "ops"),
            CreateServer("beta", "ops/beta", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        Assert.Equal(2, fixture.ViewModel.SelectionCount);
        Assert.True(fixture.ViewModel.HasSelection);
    }

    [Fact]
    public async Task ConnectEmbeddedCommand_PassesForceEmbeddedOverrideWithoutMutatingProfile()
    {
        var handler = new CapturingRdpProtocolHandler(new ConnectionResult(
            true,
            null,
            new RdpSessionResult(
                CreateRdpServer("rdp-01", "RDP 01", "ops", "External"))));
        await using var fixture = await ServerListSelectionFixture.CreateAsync([handler]);
        var server = CreateRdpServer("rdp-01", "RDP 01", "ops", "External");
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), server);

        await fixture.ViewModel.ConnectEmbeddedCommand.ExecuteAsync(fixture.ServerById("rdp-01"));

        Assert.Equal(RdpModeOverride.ForceEmbedded, handler.LastRdpModeOverride);
        var stored = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("External", stored.RdpMode);
    }

    [Fact]
    public async Task ConnectExternalCommand_PassesForceExternalOverrideWithoutMutatingProfile()
    {
        var handler = new CapturingRdpProtocolHandler(new ConnectionResult(true, null, null));
        await using var fixture = await ServerListSelectionFixture.CreateAsync([handler]);
        var server = CreateRdpServer("rdp-02", "RDP 02", "ops", "Embedded");
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), server);

        await fixture.ViewModel.ConnectExternalCommand.ExecuteAsync(fixture.ServerById("rdp-02"));

        Assert.Equal(RdpModeOverride.ForceExternal, handler.LastRdpModeOverride);
        var stored = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("Embedded", stored.RdpMode);
    }

    [Fact]
    public async Task SavedProfile_OnConnectedTransition_TreeShowsConnected()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"));
        string sessionId = SessionIdCodec.Create("alpha");

        TransitionSshSessionToConnected(fixture.StateMachine, sessionId);

        var server = fixture.ServerById("alpha");
        Assert.Equal(ConnectionState.Connected.ToString(), server.ConnectionState);
        Assert.True(server.IsActiveSession);
    }

    [Fact]
    public void ConnectedSet_ExcludesErrorInitializingAndDisconnecting()
    {
        ConnectionState[] expected =
        [
            ConnectionState.Connected,
            ConnectionState.LaunchedExternalClient,
            ConnectionState.RemoteSessionHandedOff
        ];

        Assert.Equal(
            expected.OrderBy(state => state),
            ConnectionStateSets.Connected.OrderBy(state => state));
        Assert.DoesNotContain(ConnectionState.Error, ConnectionStateSets.Connected.AsEnumerable());
        Assert.DoesNotContain(ConnectionState.Initializing, ConnectionStateSets.Connected.AsEnumerable());
        Assert.DoesNotContain(ConnectionState.Disconnecting, ConnectionStateSets.Connected.AsEnumerable());
    }

    [Fact]
    public void IsActiveSession_MatchesConnectedSet()
    {
        var server = new ServerItemViewModel();

        foreach (ConnectionState state in Enum.GetValues<ConnectionState>())
        {
            server.ConnectionState = state.ToString();

            Assert.Equal(ConnectionStateSets.IsConnected(state), server.IsActiveSession);
        }
    }

    [Fact]
    public async Task MultipleSessionsSameProfile_OneDisconnects_ProfileStaysConnected()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"));
        string firstSessionId = SessionIdCodec.Create("alpha");
        string secondSessionId = SessionIdCodec.Create("alpha");
        TransitionSshSessionToConnected(fixture.StateMachine, firstSessionId);
        TransitionSshSessionToConnected(fixture.StateMachine, secondSessionId);

        fixture.StateMachine.Reset(firstSessionId);

        var server = fixture.ServerById("alpha");
        Assert.True(server.IsActiveSession);
        Assert.True(ConnectionStateSets.IsConnected(server.ConnectionState));
    }

    [Fact]
    public async Task LastSessionTeardown_ProfileBecomesDisconnected_AndAggregationEntryRemoved()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"));
        string firstSessionId = SessionIdCodec.Create("alpha");
        string secondSessionId = SessionIdCodec.Create("alpha");
        TransitionSshSessionToConnected(fixture.StateMachine, firstSessionId);
        TransitionSshSessionToConnected(fixture.StateMachine, secondSessionId);

        fixture.StateMachine.Reset(firstSessionId);
        fixture.StateMachine.Reset(secondSessionId);

        var server = fixture.ServerById("alpha");
        Assert.Equal(ConnectionState.Disconnected.ToString(), server.ConnectionState);
        Assert.False(server.IsActiveSession);
        Assert.Equal(0, fixture.ViewModel.ActiveSessionAggregationEntryCount);
    }

    [Fact]
    public async Task RecentConnections_RecordedOnConnectOfSavedProfile()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"));
        string sessionId = SessionIdCodec.Create("alpha");

        TransitionSshSessionToConnected(fixture.StateMachine, sessionId);

        RecentConnectionEntry recent = Assert.Single(fixture.RecentConnections.GetRecents(10));
        Assert.Equal("alpha.example.com", recent.Host);
        Assert.Equal("SSH", recent.Protocol);
    }

    [Fact]
    public void FilterSpec_AllFacets_ComposeWithAndSemantics()
    {
        var server = ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = "alpha",
                DisplayName = "Alpha Production",
                RemoteServer = "alpha.example.com",
                ConnectionType = "SSH",
                SshUsername = "operator",
                Tags = "critical",
                IsFavorite = true,
                ProjectId = "atlas",
                Origin = ProfileOrigin.Manual
            },
            new ProjectDto { Id = "atlas", Name = "Atlas" },
            ConnectionState.Connected.ToString());

        Assert.True(ServerFilterSpec.Create(
            "critical",
            ["ssh"],
            favoritesOnly: true,
            connectedOnly: true,
            "atlas").Matches(server));
        Assert.False(ServerFilterSpec.Create(
            "missing",
            ["ssh"],
            favoritesOnly: true,
            connectedOnly: true,
            "atlas").Matches(server));
        Assert.False(ServerFilterSpec.Create(
            "critical",
            ["rdp"],
            favoritesOnly: true,
            connectedOnly: true,
            "atlas").Matches(server));
        Assert.False(ServerFilterSpec.Create(
            "critical",
            ["ssh"],
            favoritesOnly: true,
            connectedOnly: true,
            "other").Matches(server));

        server.IsFavorite = false;
        Assert.False(ServerFilterSpec.Create(
            "critical",
            ["ssh"],
            favoritesOnly: true,
            connectedOnly: true,
            "atlas").Matches(server));

        server.IsFavorite = true;
        server.ConnectionState = ConnectionState.Disconnected.ToString();
        Assert.False(ServerFilterSpec.Create(
            "critical",
            ["ssh"],
            favoritesOnly: true,
            connectedOnly: true,
            "atlas").Matches(server));
    }

    [Fact]
    public void ConnectedFilter_ExcludesErrorInitializingAndDisconnecting()
    {
        var server = ServerItemViewModel.FromDto(CreateServer("alpha", "Alpha", "ops"));
        ServerFilterSpec connectedOnly = ServerFilterSpec.Create(null, connectedOnly: true);

        foreach (ConnectionState excluded in new[]
                 {
                     ConnectionState.Error,
                     ConnectionState.Initializing,
                     ConnectionState.Disconnecting
                 })
        {
            server.ConnectionState = excluded.ToString();
            Assert.False(connectedOnly.Matches(server));
        }

        foreach (ConnectionState included in ConnectionStateSets.Connected)
        {
            server.ConnectionState = included.ToString();
            Assert.True(connectedOnly.Matches(server));
        }
    }

    [Fact]
    public async Task ConnectionStateCrossesBoundary_RefreshesConnectedMembershipOnce()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"));
        fixture.ViewModel.ConnectedFilterEnabled = true;
        int stableBuildCount = fixture.ViewModel.StableTreeBuildCount;
        int initialPassCount = fixture.ViewModel.FilterPassApplicationCount;

        TransitionSshSessionToConnected(
            fixture.StateMachine,
            SessionIdCodec.Create("alpha"));

        AssertVisibleServerIds(fixture.ViewModel, "alpha");
        Assert.Equal(1, fixture.ViewModel.ConnectedMembershipRefreshCount);
        Assert.Equal(initialPassCount + 1, fixture.ViewModel.FilterPassApplicationCount);
        Assert.Equal(stableBuildCount, fixture.ViewModel.StableTreeBuildCount);
    }

    [Fact]
    public async Task FilteredTree_HidesEmptyFolders_AndKeepsAncestorCounts()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups(
            "root",
            "root/matching",
            "root/hidden",
            "root/empty");
        settings.EmptyGroups.Add("root/empty");
        ServerProfileDto matching = CreateServer(
            "alpha",
            "Alpha",
            "root/matching");
        matching.IsFavorite = true;
        fixture.LoadServers(
            settings,
            matching,
            CreateServer("beta", "Beta", "root/hidden"));

        fixture.ViewModel.FavoriteFilterEnabled = true;

        FolderViewModel root = Assert.Single(fixture.ViewModel.GroupedServers);
        Assert.Equal("root", root.FullPath);
        FolderViewModel matchingFolder = Assert.Single(root.SubFolders);
        Assert.Equal("root/matching", matchingFolder.FullPath);
        Assert.Equal(1, matchingFolder.ServerCount);
        Assert.Equal(1, root.ServerCount);
        Assert.DoesNotContain(root.SubFolders, folder => folder.FullPath == "root/hidden");
        Assert.DoesNotContain(root.SubFolders, folder => folder.FullPath == "root/empty");
        Assert.False(fixture.ViewModel.ShowNoGroupDropZone);
    }

    [Fact]
    public async Task Search_WinRmUsername_Matches()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        var winRm = new ServerProfileDto
        {
            Id = "winrm",
            DisplayName = "Windows Host",
            RemoteServer = "windows.example.com",
            ConnectionType = "WINRM",
            WinRmUsername = "codex24-user",
            Group = "ops",
            Origin = ProfileOrigin.Manual
        };
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            winRm,
            CreateServer("ssh", "SSH Host", "ops"));

        fixture.ViewModel.SearchText = "codex24-user";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        AssertVisibleServerIds(fixture.ViewModel, "winrm");
    }

    [Theory]
    [InlineData("SSH", "ssh-user")]
    [InlineData("SFTP", "ssh-user")]
    [InlineData("RDP", "rdp-user")]
    [InlineData("WINRM", "winrm-user")]
    [InlineData("FTP", "ftp-user")]
    [InlineData("TELNET", "")]
    [InlineData("VNC", "")]
    [InlineData("CITRIX", "")]
    [InlineData("LOCAL", "")]
    public void SearchUsernameProjection_UsesProtocolSpecificField(
        string protocol,
        string expectedUsername)
    {
        var dto = new ServerProfileDto
        {
            Id = protocol,
            DisplayName = $"{protocol} Host",
            ConnectionType = protocol,
            RemoteServer = "host.example.com",
            SshUsername = "ssh-user",
            RdpUsername = "rdp-user",
            WinRmUsername = "winrm-user",
            FtpUsername = "ftp-user",
            TelnetUsername = "telnet-user",
            Origin = ProfileOrigin.Manual
        };

        ServerItemViewModel server = ServerItemViewModel.FromDto(dto);

        Assert.Equal(expectedUsername, server.Username);
        if (expectedUsername.Length > 0)
        {
            Assert.Contains(
                ServerItemViewModel.NormalizeSearchTerm(expectedUsername),
                server.NormalizedSearchText,
                StringComparison.Ordinal);
        }
        else if (string.Equals(protocol, "TELNET", StringComparison.Ordinal))
        {
            Assert.DoesNotContain(
                ServerItemViewModel.NormalizeSearchTerm("telnet-user"),
                server.NormalizedSearchText,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SearchDebounce_LatestWins_ClearImmediate_DisposedPassIgnored()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));
        int initialPassCount = fixture.ViewModel.FilterPassApplicationCount;

        fixture.ViewModel.SearchText = "alpha";
        fixture.ViewModel.SearchText = "gamma";

        Assert.Equal(initialPassCount, fixture.ViewModel.FilterPassApplicationCount);
        Assert.True(fixture.ViewModel.IsFilterPending);
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel, "gamma");
        Assert.Equal(initialPassCount + 1, fixture.ViewModel.FilterPassApplicationCount);

        fixture.ViewModel.SearchText = "";

        AssertVisibleServerIds(fixture.ViewModel, "alpha", "beta", "gamma");
        Assert.False(fixture.ViewModel.IsFilterPending);
        Assert.Equal(initialPassCount + 2, fixture.ViewModel.FilterPassApplicationCount);

        fixture.ViewModel.SearchText = "beta";
        int passCountBeforeDispose = fixture.ViewModel.FilterPassApplicationCount;
        fixture.ViewModel.Dispose();
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);

        Assert.Equal(passCountBeforeDispose, fixture.ViewModel.FilterPassApplicationCount);
    }

    [Fact]
    public async Task ResultCount_ReflectsAppliedPass_NotPreviousTerm()
    {
        var timeProvider = new FakeTimeProvider();
        await using var fixture = await ServerListSelectionFixture.CreateAsync(timeProvider: timeProvider);
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));

        fixture.ViewModel.SearchText = "alpha";
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel, "alpha");
        Assert.Equal("1 / 3 sessions", fixture.ViewModel.FilterResultCountText);
        Assert.True(fixture.ViewModel.HasAppliedFilterResult);

        fixture.ViewModel.SearchText = "does-not-exist";

        Assert.True(fixture.ViewModel.IsFilterPending);
        Assert.False(fixture.ViewModel.HasAppliedFilterResult);
        timeProvider.Advance(ServerListViewModel.SearchFilterDebounceDelay);
        AssertVisibleServerIds(fixture.ViewModel);
        Assert.Equal("0 / 3 sessions", fixture.ViewModel.FilterResultCountText);
        Assert.True(fixture.ViewModel.HasAppliedFilterResult);
    }

    [Fact]
    public async Task Filter_ReusesStableNodes_DoesNotRebuildWholeGraph()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto alpha = CreateServer("alpha", "Alpha", "ops");
        alpha.IsFavorite = true;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            alpha,
            CreateServer("beta", "Beta", "ops"));
        FolderViewModel originalFolder = Assert.Single(fixture.ViewModel.GroupedServers);
        ServerItemViewModel originalAlpha = fixture.ServerById("alpha");
        int stableBuildCount = fixture.ViewModel.StableTreeBuildCount;
        Assert.False(fixture.ViewModel.HasActiveFacetFilter);

        fixture.ViewModel.FavoriteFilterEnabled = true;

        Assert.True(fixture.ViewModel.HasActiveFacetFilter);
        Assert.Same(originalFolder, Assert.Single(fixture.ViewModel.GroupedServers));
        Assert.Same(originalAlpha, Assert.Single(fixture.ViewModel.Servers));
        Assert.Equal(stableBuildCount, fixture.ViewModel.StableTreeBuildCount);

        fixture.ViewModel.FavoriteFilterEnabled = false;

        Assert.False(fixture.ViewModel.HasActiveFacetFilter);
        Assert.Same(originalFolder, Assert.Single(fixture.ViewModel.GroupedServers));
        Assert.Same(originalAlpha, fixture.ServerById("alpha"));
        Assert.Equal(stableBuildCount, fixture.ViewModel.StableTreeBuildCount);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(5000)]
    public async Task FilterPass_Benchmark_ReportsInventoryScale(int inventorySize)
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto[] servers = Enumerable.Range(0, inventorySize)
            .Select(index =>
            {
                ServerProfileDto server = CreateServer(
                    $"server-{index:D5}",
                    $"Benchmark Server {index:D5}",
                    $"benchmark/group-{index % 50:D2}",
                    index);
                server.IsFavorite = index % 10 == 0;
                return server;
            })
            .ToArray();
        fixture.LoadServers(
            fixture.ExpandGroups("benchmark"),
            servers);

        var wallClock = Stopwatch.StartNew();
        fixture.ViewModel.FavoriteFilterEnabled = true;
        wallClock.Stop();

        output.WriteLine(
            "Inventory={0}; applied-pass={1:F3} ms; wall-clock={2:F3} ms; results={3}",
            inventorySize,
            fixture.ViewModel.LastFilterPassDuration.TotalMilliseconds,
            wallClock.Elapsed.TotalMilliseconds,
            fixture.ViewModel.FilteredCount);
        Assert.Equal((inventorySize + 9) / 10, fixture.ViewModel.FilteredCount);
        Assert.True(
            wallClock.Elapsed < TimeSpan.FromSeconds(2),
            $"Filtering {inventorySize} sessions took {wallClock.Elapsed.TotalMilliseconds:F3} ms.");
    }

    private static void TransitionSshSessionToConnected(ConnectionStateMachine stateMachine, string sessionId)
    {
        Assert.True(stateMachine.TryTransition(sessionId, ConnectionState.Initializing));
        Assert.True(stateMachine.TryTransition(sessionId, ConnectionState.ValidatingConfig));
        Assert.True(stateMachine.TryTransition(sessionId, ConnectionState.LaunchingSsh));
        Assert.True(stateMachine.TryTransition(sessionId, ConnectionState.Connected));
    }

    private static ServerProfileDto CreateServer(string id, string displayName, string group, int sortOrder = 0) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.com",
            ConnectionType = "SSH",
            Group = group,
            SortOrder = sortOrder,
            Origin = ProfileOrigin.Manual
        };

    private static ServerProfileDto CreateRdpServer(
        string id,
        string displayName,
        string group,
        string rdpMode) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.com",
            RemotePort = 3389,
            ConnectionType = "RDP",
            Group = group,
            RdpMode = rdpMode,
            Origin = ProfileOrigin.Manual
        };

    private static void AssertSelection(ServerListViewModel viewModel, params string[] expectedIds)
    {
        var actualIds = viewModel.SelectedItems
            .Select(item => item.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var sortedExpected = expectedIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(sortedExpected, actualIds);

        foreach (var selected in viewModel.SelectedItems)
        {
            Assert.True(selected.IsSelected);
        }
    }

    private static void AssertVisibleServerIds(ServerListViewModel viewModel, params string[] expectedIds)
    {
        Assert.Equal(SortIds(expectedIds), SortIds(viewModel.Servers.Select(server => server.Id)));
    }

    private static string[] SortIds(IEnumerable<string> ids) =>
        ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private sealed class ServerListSelectionFixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private ServerListSelectionFixture(
            string rootPath,
            IConfigManager configManager,
            ServerListViewModel viewModel,
            ConnectionStateMachine stateMachine,
            RecentConnectionTracker recentConnections,
            SessionHealthMonitor? healthMonitor)
        {
            _rootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            StateMachine = stateMachine;
            RecentConnections = recentConnections;
            HealthMonitor = healthMonitor;
        }

        public IConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public ConnectionStateMachine StateMachine { get; }

        public RecentConnectionTracker RecentConnections { get; }

        public SessionHealthMonitor? HealthMonitor { get; }

        public static async Task<ServerListSelectionFixture> CreateAsync(
            IEnumerable<IProtocolHandler>? protocolHandlers = null,
            bool withHealthMonitor = false,
            IConfigManager? configManager = null,
            TimeProvider? timeProvider = null)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "heimdall-b65-selection", Guid.NewGuid().ToString("N"));
            IConfigManager actualConfigManager = configManager ?? new ConfigManager(rootPath);
            await actualConfigManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                actualConfigManager,
                localizer,
                new NullTunnelService(),
                protocolHandlers ?? Array.Empty<IProtocolHandler>());
            var dialogService = new DialogServiceStub();
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), actualConfigManager);
            var knownHostsImporter = new KnownHostsImporter(actualConfigManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();
            var recentConnections = new RecentConnectionTracker();
            SessionHealthMonitor? healthMonitor = withHealthMonitor
                ? new SessionHealthMonitor(actualConfigManager, new FixtureHealthProbe())
                : null;

            var viewModel = new ServerListViewModel(
                actualConfigManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter,
                recentConnections,
                healthMonitor: healthMonitor,
                timeProvider: timeProvider);

            return new ServerListSelectionFixture(
                rootPath,
                actualConfigManager,
                viewModel,
                stateMachine,
                recentConnections,
                healthMonitor);
        }

        public AppSettings ExpandGroups(params string[] groups)
        {
            var settings = new AppSettings();
            foreach (var group in groups)
            {
                settings.TreeExpandedNodes.Add(group);
            }

            return settings;
        }

        public void LoadServers(AppSettings settings, params ServerProfileDto[] servers)
        {
            ViewModel.LoadServers(servers.ToList(), settings);
        }

        public async Task LoadServersAsync(AppSettings settings, params ServerProfileDto[] servers)
        {
            await ConfigManager.SaveSettingsAsync(settings);
            await ConfigManager.SaveServersAsync(servers.ToList());
            ViewModel.LoadServers(servers.ToList(), settings);
        }

        public ServerItemViewModel ServerById(string id) =>
            Assert.Single(ViewModel.Servers, server => string.Equals(server.Id, id, StringComparison.Ordinal));

        public void CollapseGroup(string path)
        {
            FolderByPath(path).IsExpanded = false;
        }

        public FolderViewModel FolderByPath(string path) =>
            Assert.IsType<FolderViewModel>(FindFolder(ViewModel.GroupedServers, path));

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            HealthMonitor?.Dispose();

            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }

        private static FolderViewModel? FindFolder(IEnumerable<FolderViewModel> folders, string path)
        {
            foreach (var folder in folders)
            {
                if (string.Equals(folder.FullPath, path, StringComparison.Ordinal))
                {
                    return folder;
                }

                var nested = FindFolder(folder.SubFolders, path);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult((true, false, server.RemoteServer, remotePort, (string?)null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }

    private sealed class CapturingRdpProtocolHandler(ConnectionResult result) : IProtocolHandler
    {
        public string Protocol => "RDP";

        public RdpModeOverride LastRdpModeOverride { get; private set; } = RdpModeOverride.UseProfile;

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            LastRdpModeOverride = rdpModeOverride;
            return Task.FromResult(result);
        }
    }

    private sealed class DialogServiceStub : IDialogService
    {
        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(false);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
