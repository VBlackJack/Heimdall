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
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed class ServerListSelectionTests
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
    public async Task SearchFilter_PurgesInvisibleSelectionsAndKeepsVisiblePrimary()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        fixture.ViewModel.SearchText = "Beta";
        await WaitForVisibleServerIdsAsync(fixture.ViewModel, "beta");

        AssertSelection(fixture.ViewModel, "beta");
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task SearchFilter_ClearsSelectionWhenNothingRemainsVisible()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        fixture.ViewModel.SearchText = "does-not-exist";
        await WaitForVisibleServerIdsAsync(fixture.ViewModel);

        Assert.Empty(fixture.ViewModel.SelectedItems);
        Assert.Null(fixture.ViewModel.SelectedServer);
        Assert.False(fixture.ViewModel.HasSelection);
    }

    [Fact]
    public async Task SearchFilter_DebouncesNonEmptyTextAndAppliesLatestTerm()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"),
            CreateServer("gamma", "Gamma Node", "ops"));

        fixture.ViewModel.SearchText = "Alpha";
        fixture.ViewModel.SearchText = "Gamma";

        await Task.Delay(150);

        Assert.Equal(3, fixture.ViewModel.Servers.Count);

        await WaitForVisibleServerIdsAsync(fixture.ViewModel, "gamma");
    }

    [Fact]
    public async Task SearchFilter_ClearingTextAppliesImmediately()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha Node", "ops"),
            CreateServer("beta", "Beta Node", "ops"));

        fixture.ViewModel.SearchText = "Beta";
        await WaitForVisibleServerIdsAsync(fixture.ViewModel, "beta");

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

    private static async Task WaitForVisibleServerIdsAsync(ServerListViewModel viewModel, params string[] expectedIds)
    {
        var sortedExpected = SortIds(expectedIds);
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (SortIds(viewModel.Servers.Select(server => server.Id)).SequenceEqual(sortedExpected))
            {
                return;
            }

            await Task.Delay(25);
        }

        AssertVisibleServerIds(viewModel, expectedIds);
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
            ConfigManager configManager,
            ServerListViewModel viewModel,
            ConnectionStateMachine stateMachine,
            RecentConnectionTracker recentConnections)
        {
            _rootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            StateMachine = stateMachine;
            RecentConnections = recentConnections;
        }

        public ConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public ConnectionStateMachine StateMachine { get; }

        public RecentConnectionTracker RecentConnections { get; }

        public static async Task<ServerListSelectionFixture> CreateAsync(
            IEnumerable<IProtocolHandler>? protocolHandlers = null)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "heimdall-b65-selection", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            var stateMachine = new ConnectionStateMachine();
            var connectionService = new ConnectionService(
                configManager,
                localizer,
                new NullTunnelService(),
                protocolHandlers ?? Array.Empty<IProtocolHandler>());
            var dialogService = new DialogServiceStub();
            var puttyImporter = new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager);
            var knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            var uiDispatcher = new FakeUiDispatcher();
            var recentConnections = new RecentConnectionTracker();

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter,
                recentConnections);

            return new ServerListSelectionFixture(
                rootPath,
                configManager,
                viewModel,
                stateMachine,
                recentConnections);
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
            var folder = FindFolder(ViewModel.GroupedServers, path);
            Assert.NotNull(folder);
            folder!.IsExpanded = false;
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();

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
