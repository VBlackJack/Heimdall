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

public sealed partial class FolderRenameServiceTests
{
    [Fact]
    public async Task RenameFolder_MigratesDescendantsGroupsEmptyGroupsDefaultsAndExpansion()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("root", "Prod"),
                CreateServer("child", "Prod/Linux"),
                CreateServer("other", "Other")
            ],
            Settings = new AppSettings
            {
                EmptyGroups = ["Prod/Empty", "Other/Empty"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" },
                    ["Prod/Linux"] = new() { SshUsername = "deploy" },
                    ["Other"] = new() { SshGatewayId = "gateway-other" }
                },
                TreeExpandedNodes = ["Prod", "Prod/Linux", "Other"]
            }
        };
        var service = new FolderRenameService(config);

        FolderRenameResult result = await service.RenameAsync("Prod", "Production");

        Assert.Equal(FolderRenameStatus.Renamed, result.Status);
        Assert.Equal("Production", result.NewPath);
        Assert.Equal(["settings", "servers", "settings"], config.PersistenceCalls);
        Assert.Equal(
            ["Production", "Production/Linux", "Other"],
            config.Servers.Select(server => server.Group));
        Assert.Equal(["Production/Empty", "Other/Empty"], config.Settings.EmptyGroups);
        Assert.Equal(
            ["Other", "Production", "Production/Linux"],
            config.Settings.GroupDefaults.Keys.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(
            ["Production", "Production/Linux", "Other"],
            config.Settings.TreeExpandedNodes);
        Assert.Equal(
            "gateway-prod",
            config.Settings.GroupDefaults["Production"].SshGatewayId);
        Assert.Equal(
            "deploy",
            config.Settings.GroupDefaults["Production/Linux"].SshUsername);
    }

    [Theory]
    [InlineData("Production/Linux")]
    [InlineData("Production\\Linux")]
    [InlineData("Production\u0001")]
    [InlineData("   ")]
    public async Task RenameFolder_SeparatorOrControlChar_RejectedWithoutSave(string invalidName)
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("root", "Prod")],
            Settings = new AppSettings
            {
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" }
                }
            }
        };
        var service = new FolderRenameService(config);

        FolderRenameResult result = await service.RenameAsync("Prod", invalidName);

        Assert.Equal(FolderRenameStatus.InvalidSegment, result.Status);
        Assert.Empty(config.PersistenceCalls);
        Assert.Equal("Prod", Assert.Single(config.Servers).Group);
        Assert.True(config.Settings.GroupDefaults.ContainsKey("Prod"));
    }

    [Fact]
    public async Task RenameFolder_CaseInsensitiveSiblingCollision_RejectedWithoutSave()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("source", "Root/Prod"),
                CreateServer("sibling-child", "Root/sTaGe/Linux")
            ]
        };
        var service = new FolderRenameService(config);

        FolderRenameResult result = await service.RenameAsync("Root/Prod", "Stage");

        Assert.Equal(FolderRenameStatus.SiblingCollision, result.Status);
        Assert.Empty(config.PersistenceCalls);
        Assert.Equal("Root/Prod", config.Servers[0].Group);
    }

    [Fact]
    public async Task RenameFolder_CaseOnlyRename_Applies()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("root", "Prod"), CreateServer("child", "Prod/Linux")],
            Settings = new AppSettings
            {
                EmptyGroups = ["Prod/Empty"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" }
                },
                TreeExpandedNodes = ["Prod", "Prod/Linux"]
            }
        };
        var service = new FolderRenameService(config);

        FolderRenameResult result = await service.RenameAsync("Prod", "PROD");

        Assert.Equal(FolderRenameStatus.Renamed, result.Status);
        Assert.Equal(["PROD", "PROD/Linux"], config.Servers.Select(server => server.Group));
        Assert.Equal(["PROD/Empty"], config.Settings.EmptyGroups);
        Assert.Equal(["PROD"], config.Settings.GroupDefaults.Keys);
        Assert.Equal(["PROD", "PROD/Linux"], config.Settings.TreeExpandedNodes);
    }

    [Fact]
    public async Task RenameFolder_PrefixSibling_NotRewritten()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                CreateServer("source", "Prod/Linux"),
                CreateServer("prefix-sibling", "Production2/Linux")
            ],
            Settings = new AppSettings
            {
                EmptyGroups = ["Prod/Empty", "Production2/Empty"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" },
                    ["Production2"] = new() { SshGatewayId = "gateway-prefix-sibling" }
                },
                TreeExpandedNodes = ["Prod", "Production2"]
            }
        };
        var service = new FolderRenameService(config);

        await service.RenameAsync("Prod", "Production");

        Assert.Equal("Production/Linux", config.Servers[0].Group);
        Assert.Equal("Production2/Linux", config.Servers[1].Group);
        Assert.Contains("Production2/Empty", config.Settings.EmptyGroups);
        Assert.True(config.Settings.GroupDefaults.ContainsKey("Production2"));
        Assert.Contains("Production2", config.Settings.TreeExpandedNodes);
    }

    [Fact]
    public async Task RenameFolder_InterruptedBetweenWrites_RecoversConsistentState()
    {
        var config = new FakeConfigManager
        {
            FailServerMutation = true,
            Servers = [CreateServer("source", "Prod/Linux")],
            Settings = new AppSettings
            {
                EmptyGroups = ["Prod/Empty"],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" }
                },
                TreeExpandedNodes = ["Prod", "Prod/Linux"]
            }
        };
        var service = new FolderRenameService(config);

        await Assert.ThrowsAsync<IOException>(
            () => service.RenameAsync("Prod", "Production"));

        Assert.Equal(["settings", "servers"], config.PersistenceCalls);
        Assert.Equal("Prod/Linux", Assert.Single(config.Servers).Group);
        Assert.True(config.Settings.GroupDefaults.ContainsKey("Prod"));
        Assert.True(config.Settings.GroupDefaults.ContainsKey("Production"));
        Assert.Contains("Prod", config.Settings.TreeExpandedNodes);
        Assert.Contains("Production", config.Settings.TreeExpandedNodes);
        Assert.Equal(
            "gateway-prod",
            GroupDefaultsDto.Resolve(
                Assert.Single(config.Servers).Group,
                config.Settings.GroupDefaults).SshGatewayId);
    }

    [Fact]
    public async Task RenameFolder_PreservesInheritedGatewayDefault()
    {
        var config = new FakeConfigManager
        {
            Servers = [CreateServer("source", "Prod/Linux")],
            Settings = new AppSettings
            {
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Prod"] = new() { SshGatewayId = "gateway-prod" }
                }
            }
        };
        var service = new FolderRenameService(config);

        await service.RenameAsync("Prod", "Production");

        ServerProfileDto server = Assert.Single(config.Servers);
        GroupDefaultsDto inherited = GroupDefaultsDto.Resolve(
            server.Group,
            config.Settings.GroupDefaults);
        Assert.Equal("Production/Linux", server.Group);
        Assert.Equal("gateway-prod", inherited.SshGatewayId);
        Assert.False(config.Settings.GroupDefaults.ContainsKey("Prod"));
    }

    private static ServerProfileDto CreateServer(string id, string group)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            Group = group,
            RemoteServer = $"{id}.example.test"
        };
    }

    private sealed class FakeConfigManager : IConfigManager
    {
        public string ConfigPath => "memory://config";

        public string SettingsPath => "memory://settings.json";

        public string ServersPath => "memory://servers.json";

        public AppSettings Settings { get; set; } = new();

        public List<ServerProfileDto> Servers { get; set; } = [];

        public List<string> PersistenceCalls { get; } = [];

        public bool FailServerMutation { get; init; }

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Clone(Settings));

        public Task SaveSettingsAsync(AppSettings settings)
        {
            PersistenceCalls.Add("settings");
            Settings = Clone(settings);
            SettingsChanged?.Invoke(Clone(Settings));
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            PersistenceCalls.Add("settings");
            AppSettings candidate = Clone(Settings);
            mutate(candidate);
            Settings = candidate;
            SettingsChanged?.Invoke(Clone(Settings));
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult(Clone(Servers));

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate)
        {
            PersistenceCalls.Add("servers");
            if (FailServerMutation)
            {
                throw new IOException("Simulated inventory interruption.");
            }

            List<ServerProfileDto> candidate = Clone(Servers);
            TResult result = mutate(candidate);
            Servers = candidate;
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            PersistenceCalls.Add("servers");
            Servers = Clone(servers);
            return Task.CompletedTask;
        }

        private static T Clone<T>(T value)
        {
            string json = JsonSerializer.Serialize(value);
            return JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidOperationException("Failed to clone test configuration.");
        }
    }
}
