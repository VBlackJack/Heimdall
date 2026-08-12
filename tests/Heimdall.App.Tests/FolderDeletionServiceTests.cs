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
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class FolderDeletionServiceTests
{
    [Fact]
    public async Task DeleteAsync_DrainsMovesPurgesAndReloadsPersistedState()
    {
        RecordingConfigManager configManager = new()
        {
            Servers =
            [
                CreateServer("target", "Root/Ops"),
                CreateServer("child", "root/ops/Child"),
                CreateServer("tool", "ROOT/OPS", "TOOL:HOSTS"),
                CreateServer("ancestor", "Root"),
                CreateServer("prefix", "Root/Ops2")
            ],
            Settings = new AppSettings
            {
                DefaultTheme = "Vesper",
                EmptyGroups = ["Root", "Root/Ops", "ROOT/OPS/Empty", "Root/Ops2"],
                TreeExpandedNodes = ["Root", "Root/Ops", "root/ops/Child", "Root/Ops2"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Root"] = new() { SshUsername = "root-default" },
                    ["Root/Ops"] = new() { SshUsername = "ops-default" },
                    ["ROOT/OPS/Child"] = new() { SshUsername = "child-default" },
                    ["Root/Ops2"] = new() { SshUsername = "prefix-default" }
                }
            }
        };
        configManager.BeforeMerge = settings => settings.DefaultTheme = "Buffy";
        FolderDeletionService service = new(configManager);

        FolderDeletionResult result = await service.DeleteAsync(
            "root/ops",
            () =>
            {
                configManager.Calls.Add("drain");
                return Task.CompletedTask;
            });

        Assert.Equal(
            ["drain", "mutate-servers", "merge-settings", "load-settings", "load-servers"],
            configManager.Calls);
        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.Null(Server(configManager.Servers, "target").Group);
        Assert.Null(Server(configManager.Servers, "child").Group);
        Assert.Null(Server(configManager.Servers, "tool").Group);
        Assert.Equal("Root", Server(configManager.Servers, "ancestor").Group);
        Assert.Equal("Root/Ops2", Server(configManager.Servers, "prefix").Group);
        Assert.Equal(["Root", "Root/Ops2"], configManager.Settings.EmptyGroups);
        Assert.Equal(["Root", "Root/Ops2"], configManager.Settings.TreeExpandedNodes);
        Assert.Equal(
            ["Root", "Root/Ops2"],
            configManager.Settings.GroupDefaults.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal("Buffy", configManager.Settings.DefaultTheme);
        Assert.Equal(
            configManager.Servers.Select(server => (server.Id, server.Group)),
            result.Servers.Select(server => (server.Id, server.Group)));
        Assert.Equal("Buffy", result.Settings.DefaultTheme);
    }

    [Fact]
    public async Task DeleteAsync_PendingDrainCompletesBeforeInventoryMutation()
    {
        RecordingConfigManager configManager = new()
        {
            Servers = [CreateServer("target", "Ops")]
        };
        FolderDeletionService service = new(configManager);
        TaskCompletionSource drainStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseDrain = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<FolderDeletionResult> deletion = service.DeleteAsync(
            "Ops",
            async () =>
            {
                drainStarted.TrySetResult();
                await releaseDrain.Task;
            });

        await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(deletion.IsCompleted);
        Assert.Empty(configManager.Calls);
        Assert.Equal("Ops", Assert.Single(configManager.Servers).Group);

        releaseDrain.TrySetResult();
        await deletion;
        Assert.Null(Assert.Single(configManager.Servers).Group);
    }

    [Fact]
    public async Task DeleteAsync_SettingsMergeFails_KeepsRecoverableMetadataAndDoesNotProject()
    {
        RecordingConfigManager configManager = new()
        {
            FailSettingsMerge = true,
            Servers = [CreateServer("target", "Ops")],
            Settings = new AppSettings
            {
                EmptyGroups = ["Ops"],
                TreeExpandedNodes = ["Ops"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Ops"] = new() { SshUsername = "ops-default" }
                }
            }
        };
        FolderDeletionService service = new(configManager);

        await Assert.ThrowsAsync<IOException>(
            () => service.DeleteAsync(
                "Ops",
                () =>
                {
                    configManager.Calls.Add("drain");
                    return Task.CompletedTask;
                }));

        Assert.Equal(["drain", "mutate-servers", "merge-settings"], configManager.Calls);
        Assert.Null(Assert.Single(configManager.Servers).Group);
        Assert.Equal(["Ops"], configManager.Settings.EmptyGroups);
        Assert.Equal(["Ops"], configManager.Settings.TreeExpandedNodes);
        Assert.True(configManager.Settings.GroupDefaults.ContainsKey("Ops"));
    }

    private static ServerProfileDto CreateServer(
        string id,
        string group,
        string connectionType = "SSH")
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            Group = group,
            ConnectionType = connectionType,
            RemoteServer = $"{id}.example.test"
        };
    }

    private static ServerProfileDto Server(IEnumerable<ServerProfileDto> servers, string id)
    {
        return Assert.Single(
            servers,
            server => string.Equals(server.Id, id, StringComparison.Ordinal));
    }

    private sealed class RecordingConfigManager : IConfigManager
    {
        public string ConfigPath => "memory://config";

        public string SettingsPath => "memory://settings.json";

        public string ServersPath => "memory://servers.json";

        public AppSettings Settings { get; set; } = new();

        public List<ServerProfileDto> Servers { get; set; } = [];

        public List<string> Calls { get; } = [];

        public Action<AppSettings>? BeforeMerge { get; set; }

        public int MergeSettingCallCount { get; private set; }

        public bool FailSettingsMerge { get; init; }

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync()
        {
            Calls.Add("load-settings");
            return Task.FromResult(Clone(Settings));
        }

        public Task SaveSettingsAsync(AppSettings settings) =>
            throw new InvalidOperationException("Folder deletion must use one atomic settings merge.");

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            Calls.Add("merge-settings");
            MergeSettingCallCount++;
            if (FailSettingsMerge)
            {
                throw new IOException("Simulated settings merge failure.");
            }

            BeforeMerge?.Invoke(Settings);
            AppSettings candidate = Clone(Settings);
            mutate(candidate);
            Settings = candidate;
            SettingsChanged?.Invoke(Clone(Settings));
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
        {
            Calls.Add("load-servers");
            return Task.FromResult(Clone(Servers));
        }

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate)
        {
            Calls.Add("mutate-servers");
            List<ServerProfileDto> candidate = Clone(Servers);
            TResult result = mutate(candidate);
            Servers = candidate;
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers) =>
            throw new InvalidOperationException("Folder deletion must mutate the persisted inventory.");

        private static T Clone<T>(T value)
        {
            string json = JsonSerializer.Serialize(value);
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException("Failed to clone test configuration.");
        }
    }
}
