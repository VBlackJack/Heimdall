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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class TunnelsViewModelTests
{
    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_NoActiveTab_UsesAppDefault()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = false };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.True(vm.IsPanelOpen);
        Assert.Equal(0, config.LoadServersCallCount);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_AdHocTabWithoutManualOverride_UsesAppDefault()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateAdHocTab("profile-1");
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.False(vm.IsPanelOpen);
        Assert.Equal(0, config.LoadServersCallCount);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_ManualOverrideTrue_UsesManualOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1", manualOverride: true);
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: false)];
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.True(vm.IsPanelOpen);
        Assert.Equal(0, config.LoadServersCallCount);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_ManualOverrideFalse_UsesManualOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = false };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1", manualOverride: false);
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: true)];
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.False(vm.IsPanelOpen);
        Assert.Equal(0, config.LoadServersCallCount);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_SavedProfileExpandedTrue_UsesProfileOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1");
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: true)];
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.True(vm.IsPanelOpen);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_SavedProfileExpandedFalse_UsesProfileOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = false };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1");
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: false)];
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.False(vm.IsPanelOpen);
    }

    [Fact]
    public async Task ResolveAndApplyPanelStateAsync_SavedProfileExpandedNull_UsesAppDefault()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = false };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1");
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);

        await vm.ResolveAndApplyPanelStateAsync();

        Assert.True(vm.IsPanelOpen);
    }

    [Fact]
    public async Task TogglePanelCommand_SavedProfileTab_PersistsProfileOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1");
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);
        await vm.ResolveAndApplyPanelStateAsync();

        await vm.TogglePanelCommand.ExecuteAsync(null);

        Assert.Equal(1, config.SaveServersCallCount);
        Assert.True(config.Servers.Single().TunnelsPanelExpanded);
    }

    [Fact]
    public async Task TogglePanelCommand_SavedProfileTab_SetsTabManualOverride()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var tab = CreateSavedTab("profile-1");
        host.Connection.ActiveSession = tab;
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);

        await vm.TogglePanelCommand.ExecuteAsync(null);

        Assert.True(tab.TunnelsPanelManualOverride);
    }

    [Fact]
    public async Task TogglePanelCommand_AdHocTab_SetsTabManualOverrideOnly()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var tab = CreateAdHocTab("profile-1");
        host.Connection.ActiveSession = tab;
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);

        await vm.TogglePanelCommand.ExecuteAsync(null);

        Assert.True(tab.TunnelsPanelManualOverride);
        Assert.Equal(0, config.SaveServersCallCount);
    }

    [Fact]
    public async Task TogglePanelCommand_SavedProfileMissing_FallsBackToTabLocalOnly()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var tab = CreateSavedTab("profile-missing");
        host.Connection.ActiveSession = tab;
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);

        await vm.TogglePanelCommand.ExecuteAsync(null);

        Assert.True(tab.TunnelsPanelManualOverride);
        Assert.Equal(0, config.SaveServersCallCount);
    }

    [Fact]
    public async Task TogglePanelCommand_NoActiveTab_UpdatesPanelStateOnly()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        using var vm = CreateViewModel(host, config);
        await vm.ResolveAndApplyPanelStateAsync();

        await vm.TogglePanelCommand.ExecuteAsync(null);

        Assert.True(vm.IsPanelOpen);
        Assert.Null(host.Connection.ActiveSession);
        Assert.Equal(0, config.SaveServersCallCount);
    }

    [Fact]
    public async Task ActiveSessionChanged_SavedProfilesWithDifferentOverrides_UpdatesPanelState()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var first = CreateSavedTab("profile-1");
        var second = CreateSavedTab("profile-2");
        config.Servers =
        [
            CreateServer("profile-1", tunnelsPanelExpanded: false),
            CreateServer("profile-2", tunnelsPanelExpanded: true)
        ];
        using var vm = CreateViewModel(host, config);

        host.Connection.ActiveSession = first;
        await WaitUntilAsync(() => !vm.IsPanelOpen);
        host.Connection.ActiveSession = second;
        await WaitUntilAsync(() => vm.IsPanelOpen);

        Assert.True(vm.IsPanelOpen);
    }

    [Fact]
    public async Task SettingsChanged_NoManualOverrideOrProfileValue_UpdatesPanelState()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        host.Connection.ActiveSession = CreateSavedTab("profile-1");
        config.Servers = [CreateServer("profile-1", tunnelsPanelExpanded: null)];
        using var vm = CreateViewModel(host, config);
        await vm.ResolveAndApplyPanelStateAsync();

        config.RaiseSettingsChanged(new AppSettings { CollapseTunnelsPanelByDefault = false });

        await WaitUntilAsync(() => vm.IsPanelOpen);
        Assert.True(vm.IsPanelOpen);
    }

    [Fact]
    public void TunnelOpened_PanelClosed_DoesNotOpenPanelAndRefreshesList()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        using var tunnelManager = new TunnelManager();
        using var vm = CreateViewModel(host, config, tunnelManager);
        vm.IsPanelOpen = false;
        var info = new TunnelInfo("gateway", 50123, "target.internal", 3389, DateTime.UtcNow, true);

        var registered = tunnelManager.TryRegisterExternalTunnel(info, new TestDisposable(), () => true);

        Assert.True(registered);
        Assert.False(vm.IsPanelOpen);
        Assert.Equal(1, vm.Count);
        Assert.Single(vm.List);
    }

    [Fact]
    public void TunnelOpened_UpdatesBadgeStateForAllTabs()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var stateMachine = new ConnectionStateMachine();
        using var tunnelManager = new TunnelManager();
        var tunnelTab = CreateSavedTab("server-1");
        var noTunnelTab = CreateSavedTab("server-2");
        host.Connection.ActiveSessions.Add(tunnelTab);
        host.Connection.ActiveSessions.Add(noTunnelTab);
        using var vm = CreateViewModel(host, config, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo("server-1", 50124, processId: 1234);

        RegisterTunnel(tunnelManager, 50124, isAlive: true);

        Assert.Equal(TunnelBadgeState.Healthy, tunnelTab.TunnelBadgeState);
        Assert.Equal(TunnelBadgeState.Hidden, noTunnelTab.TunnelBadgeState);
    }

    [Fact]
    public void TunnelClosed_UpdatesBadgeStateForAllTabs()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var stateMachine = new ConnectionStateMachine();
        using var tunnelManager = new TunnelManager();
        var tab = CreateSavedTab("server-1");
        host.Connection.ActiveSessions.Add(tab);
        using var vm = CreateViewModel(host, config, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo("server-1", 50125, processId: 1234);
        RegisterTunnel(tunnelManager, 50125, isAlive: true);
        Assert.Equal(TunnelBadgeState.Healthy, tab.TunnelBadgeState);

        tunnelManager.ForceCloseTunnel(50125);

        Assert.Equal(TunnelBadgeState.Unhealthy, tab.TunnelBadgeState);
    }

    [Fact]
    public void ActiveSessionsAdded_NewTabComputesInitialBadgeState()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var stateMachine = new ConnectionStateMachine();
        using var tunnelManager = new TunnelManager();
        using var vm = CreateViewModel(host, config, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo("server-1", 50126, processId: 1234);
        RegisterTunnel(tunnelManager, 50126, isAlive: true);
        var tab = CreateSavedTab("server-1");

        host.Connection.ActiveSessions.Add(tab);

        Assert.Equal(TunnelBadgeState.Healthy, tab.TunnelBadgeState);
    }

    [Fact]
    public void ActiveSessionsRemoved_TabIsUnsubscribed()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var stateMachine = new ConnectionStateMachine();
        using var tunnelManager = new TunnelManager();
        var tab = CreateSavedTab("server-1");
        host.Connection.ActiveSessions.Add(tab);
        using var vm = CreateViewModel(host, config, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo("server-1", 50127, processId: 1234);
        RegisterTunnel(tunnelManager, 50127, isAlive: true);
        Assert.Equal(TunnelBadgeState.Healthy, tab.TunnelBadgeState);

        host.Connection.ActiveSessions.Remove(tab);
        tab.TunnelBadgeState = TunnelBadgeState.Hidden;
        tunnelManager.ForceCloseTunnel(50127);
        tab.RootContent = new SessionPaneModel { ServerId = "server-1" };

        Assert.Equal(TunnelBadgeState.Hidden, tab.TunnelBadgeState);
    }

    [Fact]
    public void TabRootContentChanged_RecomputesBadgeState()
    {
        var settings = new AppSettings { CollapseTunnelsPanelByDefault = true };
        var host = new TestTunnelsHost(settings);
        var config = new FakeConfigManager(settings);
        var stateMachine = new ConnectionStateMachine();
        using var tunnelManager = new TunnelManager();
        var tab = new SessionTabViewModel();
        host.Connection.ActiveSessions.Add(tab);
        using var vm = CreateViewModel(host, config, tunnelManager, stateMachine);
        stateMachine.SetTunnelInfo("server-1", 50128, processId: 1234);
        RegisterTunnel(tunnelManager, 50128, isAlive: true);
        Assert.Equal(TunnelBadgeState.Hidden, tab.TunnelBadgeState);

        tab.RootContent = new SplitContainerModel
        {
            First = new SessionPaneModel { ServerId = "server-1" },
            Second = new SessionPaneModel { ServerId = "" },
            Orientation = SplitOrientation.Vertical
        };

        Assert.Equal(TunnelBadgeState.Healthy, tab.TunnelBadgeState);
    }

    private static TunnelsViewModel CreateViewModel(
        TestTunnelsHost host,
        FakeConfigManager config,
        TunnelManager? tunnelManager = null,
        ConnectionStateMachine? stateMachine = null)
    {
        return new TunnelsViewModel(
            host,
            new LocalizationManager(),
            tunnelManager ?? new TunnelManager(),
            stateMachine ?? new ConnectionStateMachine(),
            new HostKeyStore(),
            RejectingHostKeyVerifier.Instance,
            config);
    }

    private static SessionTabViewModel CreateSavedTab(string profileId, bool? manualOverride = null)
    {
        return new SessionTabViewModel
        {
            ServerId = profileId,
            OriginalServerId = profileId,
            TunnelsPanelManualOverride = manualOverride
        };
    }

    private static SessionTabViewModel CreateAdHocTab(string profileId)
    {
        var tab = new SessionTabViewModel
        {
            OriginalServerId = profileId
        };
        tab.MarkAsAdHoc(CreateServer(profileId, tunnelsPanelExpanded: null));
        return tab;
    }

    private static ServerProfileDto CreateServer(string id, bool? tunnelsPanelExpanded)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.com",
            ConnectionType = "RDP",
            TunnelsPanelExpanded = tunnelsPanelExpanded
        };
    }

    private static void RegisterTunnel(TunnelManager tunnelManager, int localPort, bool isAlive)
    {
        var info = new TunnelInfo("gateway", localPort, "target.internal", 3389, DateTime.UtcNow, isAlive);
        Assert.True(tunnelManager.TryRegisterExternalTunnel(info, new TestDisposable(), () => isAlive));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private sealed class TestTunnelsHost(AppSettings settings) : ITunnelsHost
    {
        public ConnectionViewModel Connection { get; } = new(new LocalizationManager(), null!, null!, new PaneCloseArbiter());

        public AppSettings Settings { get; private set; } = settings;

        public AppSettings? CurrentSettings => Settings;

        public string StatusText { get; set; } = string.Empty;

        public void ApplySettings(AppSettings settings)
        {
            Settings = settings;
        }
    }

    private sealed class FakeConfigManager(AppSettings settings) : IConfigManager
    {
        public List<ServerProfileDto> Servers { get; set; } = [];

        public int LoadServersCallCount { get; private set; }

        public int SaveServersCallCount { get; private set; }

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged;

        private AppSettings Settings { get; set; } = CloneSettings(settings);

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(CloneSettings(Settings));

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Settings = CloneSettings(settings);
            SettingsChanged?.Invoke(CloneSettings(Settings));
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            mutate(Settings);
            SettingsChanged?.Invoke(CloneSettings(Settings));
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
        {
            LoadServersCallCount++;
            return Task.FromResult(CloneServers(Servers));
        }

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
        {
            LoadServersCallCount++;
            List<ServerProfileDto> servers = CloneServers(Servers);
            string originalJson = System.Text.Json.JsonSerializer.Serialize(servers);
            TResult result = mutate(servers);
            string mutatedJson = System.Text.Json.JsonSerializer.Serialize(servers);
            if (string.Equals(originalJson, mutatedJson, StringComparison.Ordinal))
            {
                return Task.FromResult(result);
            }

            SaveServersCallCount++;
            Servers = CloneServers(servers);
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            SaveServersCallCount++;
            Servers = CloneServers(servers);
            return Task.CompletedTask;
        }

        public void RaiseSettingsChanged(AppSettings settings)
        {
            Settings = CloneSettings(settings);
            SettingsChanged?.Invoke(CloneSettings(Settings));
        }

        private static AppSettings CloneSettings(AppSettings settings)
        {
            return new AppSettings
            {
                CollapseTunnelsPanelByDefault = settings.CollapseTunnelsPanelByDefault
            };
        }

        private static List<ServerProfileDto> CloneServers(IEnumerable<ServerProfileDto> servers)
        {
            return servers.Select(server => new ServerProfileDto
            {
                Id = server.Id,
                DisplayName = server.DisplayName,
                RemoteServer = server.RemoteServer,
                ConnectionType = server.ConnectionType,
                TunnelsPanelExpanded = server.TunnelsPanelExpanded
            }).ToList();
        }
    }

    private sealed class TestDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
