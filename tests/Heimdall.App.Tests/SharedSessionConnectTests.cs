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
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

/// <summary>
/// A shared session found, connected and edited through the one profile lookup, over a real
/// configuration manager, so what reaches the disk is what the tests read back.
/// </summary>
public sealed partial class SharedSessionConnectTests
{
    private const string LocalProfileId = "local-profile-1";

    // --- The lookup ---

    [Fact]
    public async Task FindAsync_LooksInServersJsonFirst_ThenTheSharedSources()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedLocalProfileAsync();
        var shadowed = new ServerProfileDto { Id = LocalProfileId, DisplayName = "from the shared source" };
        ProfileLookup lookup = new(
            fixture.ConfigManager,
            new StubSharedSessionProvider([shadowed, fixture.Shared.Profile], [fixture.Shared.Gateway]));

        Assert.Equal("Local profile", (await lookup.FindAsync(LocalProfileId))!.DisplayName);
        Assert.Equal(fixture.Shared.Profile.DisplayName, (await lookup.FindAsync(fixture.Shared.Profile.Id))!.DisplayName);
        Assert.Null(await lookup.FindAsync("nowhere"));
    }

    [Fact]
    public async Task GetAllAsync_ListsLocalThenShared_WithoutDuplicates()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedLocalProfileAsync();

        IReadOnlyList<ServerProfileDto> all = await fixture.Lookup.GetAllAsync();

        Assert.Equal(
            new[] { LocalProfileId, fixture.Shared.Profile.Id, fixture.Shared.DirectProfile.Id },
            all.Select(profile => profile.Id));
    }

    [Fact]
    public async Task EffectiveSettingsFor_LocalIsTheSameInstance_SharedIsACopyWithTheSharedGateways()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppSettings canonical = await fixture.ConfigManager.LoadSettingsAsync();
        int gatewaysBefore = canonical.SshGateways.Count;

        AppSettings forLocal = fixture.Lookup.EffectiveSettingsFor(new ServerProfileDto { Id = LocalProfileId }, canonical);
        AppSettings forShared = fixture.Lookup.EffectiveSettingsFor(fixture.Shared.Profile, canonical);

        Assert.Same(canonical, forLocal);
        Assert.NotSame(canonical, forShared);
        Assert.Contains(forShared.SshGateways, gateway => gateway.Id == fixture.Shared.Gateway.Id);
        Assert.Equal(gatewaysBefore, canonical.SshGateways.Count);
    }

    // --- Connect ---

    [Fact]
    public async Task RestoreServerAsync_SharedId_ConnectsTheSharedProfileWithTheSharedGateways()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        bool restored = await fixture.ViewModel.RestoreServerAsync(fixture.Shared.DirectProfile.Id, CancellationToken.None);

        Assert.False(restored, "the recording handler answers with a failure");
        (ServerProfileDto dispatched, AppSettings settings) = Assert.Single(fixture.Handler.Dispatched);
        Assert.Equal(fixture.Shared.DirectProfile.Id, dispatched.InventoryProfileId);
        Assert.Equal(fixture.Shared.DirectProfile.RemoteServer, dispatched.RemoteServer);
        Assert.Contains(settings.SshGateways, gateway => gateway.Id == fixture.Shared.Gateway.Id);
    }

    [Fact]
    public async Task SettingsSave_AfterASharedConnect_HoldsNoSharedGateway()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ViewModel.RestoreServerAsync(fixture.Shared.DirectProfile.Id, CancellationToken.None);
        AppSettings connectSettings = Assert.Single(fixture.Handler.Dispatched).Settings;

        await fixture.ConfigManager.SaveSettingsAsync(await fixture.ConfigManager.LoadSettingsAsync());

        string onDisk = await File.ReadAllTextAsync(fixture.ConfigManager.SettingsPath);
        Assert.DoesNotContain(SharedSessionIds.Prefix, onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(
            (await fixture.ConfigManager.LoadSettingsAsync()).SshGateways,
            gateway => SharedSessionIds.IsShared(gateway.Id));

        // Even handing the connection's own copy to the whole-object save is refused at the door.
        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => fixture.ConfigManager.SaveSettingsAsync(connectSettings));
        Assert.DoesNotContain(
            SharedSessionIds.Prefix,
            await File.ReadAllTextAsync(fixture.ConfigManager.SettingsPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedConnect_LocalFolderDefaults_NeverApply()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.ConfigManager.MergeSettingAsync(settings =>
            settings.GroupDefaults[SharedSessionTestData.SharedFolder] = new GroupDefaultsDto
            {
                SshUsername = "local-folder-user",
                SshKeyPath = @"C:\keys\local-folder.key",
            });

        await fixture.ViewModel.RestoreServerAsync(fixture.Shared.DirectProfile.Id, CancellationToken.None);

        // The shared session names no login and no key, so a local folder default would fill both.
        ServerProfileDto dispatched = Assert.Single(fixture.Handler.Dispatched).Server;
        Assert.Null(dispatched.SshUsername);
        Assert.Null(dispatched.SshKeyPath);
    }

    [Fact]
    public async Task BulkConnectPlan_SharedRowInTheTree_ResolvesThroughTheLookup()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        fixture.ViewModel.LoadServers([fixture.Shared.Profile.CloneFaithfully()], settings);
        ServerItemViewModel row = Assert.Single(fixture.ViewModel.Servers);

        ServerListViewModel.BulkConnectPlan plan =
            await fixture.ViewModel.PrepareBulkConnectPlanAsync([row], CancellationToken.None);

        ServerListViewModel.BulkConnectCandidate candidate = Assert.Single(plan.Candidates);
        Assert.Equal(fixture.Shared.Profile.Id, candidate.ServerDto.Id);
        Assert.Contains(plan.Settings.SshGateways, gateway => gateway.Id == fixture.Shared.Gateway.Id);
        Assert.Equal(0, plan.SkippedCount);
    }

    // --- Edit ---

    [Fact]
    public async Task EditServerById_SharedId_SaysTheSessionIsReadOnly()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;

        bool handled = await fixture.ViewModel.EditServerByIdAsync(fixture.Shared.Profile.Id, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(fixture.Localizer["StatusSharedSessionReadOnly"], Assert.Single(statuses));
    }

    [Fact]
    public async Task EditCommand_SharedRow_SaysTheSessionIsReadOnlyAndWritesNothing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        ServerItemViewModel row = (await fixture.ViewModel.FindSharedServerItemAsync(fixture.Shared.Profile.Id))!;

        await fixture.ViewModel.EditServerCommand.ExecuteAsync(row);

        Assert.Equal(fixture.Localizer["StatusSharedSessionReadOnly"], Assert.Single(statuses));
        Assert.Empty(await fixture.ConfigManager.LoadServersAsync());
    }

    [Fact]
    public async Task FindSharedServerItem_LocalOrUnknownId_IsNull()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.SeedLocalProfileAsync();

        Assert.Null(await fixture.ViewModel.FindSharedServerItemAsync(LocalProfileId));
        Assert.Null(await fixture.ViewModel.FindSharedServerItemAsync(SharedSessionIds.PrefixFor("nosource") + "x"));
        Assert.Equal(
            fixture.Shared.Profile.Id,
            (await fixture.ViewModel.FindSharedServerItemAsync(fixture.Shared.Profile.Id))!.Id);
    }

    /// <summary>Records the profile and settings every SSH connection is dispatched with.</summary>
    private sealed class RecordingSshHandler : IProtocolHandler
    {
        public List<(ServerProfileDto Server, AppSettings Settings)> Dispatched { get; } = [];

        public string Protocol => "SSH";

        public Task<ConnectionResult> ConnectAsync(
            ServerProfileDto server,
            AppSettings settings,
            CancellationToken ct,
            RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
        {
            Dispatched.Add((server.CloneFaithfully(), settings));
            return Task.FromResult(new ConnectionResult(false, "recorded", null));
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
            => Task.FromResult(
                new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public void Invoke(Action action) => action();

        public T Invoke<T>(Func<T> function) => function();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> function) => function();

        public bool CheckAccess() => true;
    }

    private sealed class UnusedRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RdpImportResult> ApplyAsync(
            RdpImportPreview preview,
            RdpImportSelection selection,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Tolerates the error notice a failed connection shows and refuses every other dialog: an
    /// edit of a shared session that opened the server dialog would fail the test here.
    /// </summary>
    private class RefusingDialogProxy : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowError))
            {
                return null;
            }

            throw new NotSupportedException($"unexpected dialog call: {targetMethod?.Name}");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _rootPath;

        private Fixture(
            string rootPath,
            ServerListViewModel viewModel,
            RecordingSshHandler handler,
            ConfigManager configManager,
            LocalizationManager localizer,
            SharedSessionTestData shared,
            ProfileLookup lookup)
        {
            _rootPath = rootPath;
            ViewModel = viewModel;
            Handler = handler;
            ConfigManager = configManager;
            Localizer = localizer;
            Shared = shared;
            Lookup = lookup;
        }

        public ServerListViewModel ViewModel { get; }

        public RecordingSshHandler Handler { get; }

        public ConfigManager ConfigManager { get; }

        public LocalizationManager Localizer { get; }

        public SharedSessionTestData Shared { get; }

        public ProfileLookup Lookup { get; }

        public static async Task<Fixture> CreateAsync(
            Func<SharedSessionTestData, ISharedSessionProvider>? providerFactory = null)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(), "heimdall-shared-connect", Guid.NewGuid().ToString("N"));
            var configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            var localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            SharedSessionTestData shared = SharedSessionTestData.Create();
            var lookup = new ProfileLookup(configManager, providerFactory?.Invoke(shared) ?? shared.Provider);
            var handler = new RecordingSshHandler();
            var connectionService = new ConnectionService(
                configManager, localizer, new NullTunnelService(), [handler]);

            var viewModel = new ServerListViewModel(
                configManager,
                localizer,
                new InlineUiDispatcher(),
                new ConnectionStateMachine(),
                connectionService,
                System.Reflection.DispatchProxy.Create<IDialogService, RefusingDialogProxy>(),
                new UnusedRdpImportService(),
                new PuttySessionImporter(new FakePuttySessionRegistrySource([]), configManager),
                new KnownHostsImporter(configManager, new HostKeyStore()),
                profileLookup: lookup);

            return new Fixture(rootPath, viewModel, handler, configManager, localizer, shared, lookup);
        }

        public Task SeedLocalProfileAsync() => ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = LocalProfileId,
                DisplayName = "Local profile",
                ConnectionType = "SSH",
                RemoteServer = "local01.example.test",
                SshPort = DefaultPorts.Ssh,
                UseDirectConnection = true,
            },
        ]);

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
            catch (IOException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
