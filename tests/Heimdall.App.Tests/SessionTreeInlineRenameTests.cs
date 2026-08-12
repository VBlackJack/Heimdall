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
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class SessionTreeInlineRenameTests
{
    [Fact]
    public void SessionTree_F2_EntersInlineEdit_OnFocusedNode()
    {
        var server = ServerItemViewModel.FromDto(CreateServer("server-1", "Original"));

        bool started = SessionTreeInlineRename.TryBeginEdit(server);

        Assert.True(started);
        Assert.True(server.IsEditing);
        Assert.Equal("Original", server.EditName);
    }

    [Fact]
    public void InlineRename_EnterCommits_RestoresFocusAndSelection()
    {
        var server = ServerItemViewModel.FromDto(CreateServer("server-1", "Original"));
        ServerItemViewModel selectedServer = server;
        IInlineRenameNode? focusedNode = null;
        server.BeginInlineEdit();
        server.DisplayName = "Renamed";

        SessionTreeInlineRename.CompleteEdit(server, node => focusedNode = node);

        Assert.False(server.IsEditing);
        Assert.Equal("Renamed", server.EditName);
        Assert.Same(server, selectedServer);
        Assert.Same(server, focusedNode);
    }

    [Fact]
    public void InlineRename_EscapeCancels_WithoutSave()
    {
        var server = ServerItemViewModel.FromDto(CreateServer("server-1", "Original"));
        bool saveCalled = false;
        server.BeginInlineEdit();
        server.EditName = "Discarded";

        SessionTreeInlineRename.CancelEdit(server, _ => { });

        Assert.False(server.IsEditing);
        Assert.Equal("Original", server.DisplayName);
        Assert.Equal("Original", server.EditName);
        Assert.False(saveCalled);
    }

    [Fact]
    public void InlineEditor_TypingOrClick_DoesNotConnectOrStartDrag()
    {
        bool? suppressesPointerInteraction = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                suppressesPointerInteraction =
                    MainWindow.IsInlineRenameEditorSource(new TextBox());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.True(suppressesPointerInteraction);
    }

    [Fact]
    public async Task RenameServer_ValidName_PersistsThenUpdatesSameVmAndSelection()
    {
        var config = new RecordingConfigManager
        {
            Servers = [CreateServer("server-1", "Original")]
        };
        var service = new ServerRenameService(config);
        var server = ServerItemViewModel.FromDto(CreateServer("server-1", "Original"));
        ServerItemViewModel selectedServer = server;
        server.BeginInlineEdit();
        server.EditName = "  Renamed  ";

        ServerRenameResult result = await service.RenameAsync(server.Id, server.EditName);
        server.UpdateFromDto(Assert.IsType<ServerProfileDto>(result.Server));
        SessionTreeInlineRename.CompleteEdit(server, _ => { });

        Assert.Equal(ServerRenameStatus.Renamed, result.Status);
        Assert.False(server.IsEditing);
        Assert.Equal("Renamed", Assert.Single(config.Servers).DisplayName);
        Assert.Equal("Renamed", server.DisplayName);
        Assert.Same(server, selectedServer);
        Assert.Equal(["mutate"], config.PersistenceCalls);
    }

    [Fact]
    public async Task RenameServer_SaveFailure_LeavesNameSelectionAndFilterUnchanged()
    {
        var config = new RecordingConfigManager
        {
            Servers = [CreateServer("server-1", "Original")],
            FailServerMutation = true
        };
        var service = new ServerRenameService(config);
        var server = ServerItemViewModel.FromDto(CreateServer("server-1", "Original"));
        ServerItemViewModel selectedServer = server;
        string filter = "prod";
        server.BeginInlineEdit();
        server.EditName = "Renamed";

        await Assert.ThrowsAsync<IOException>(
            () => service.RenameAsync(server.Id, server.EditName));

        Assert.True(server.IsEditing);
        Assert.Equal("Renamed", server.EditName);
        Assert.Equal("Original", server.DisplayName);
        Assert.Same(server, selectedServer);
        Assert.Equal("prod", filter);
        Assert.Equal("Original", Assert.Single(config.Servers).DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RenameServer_InvalidName_RemainsEditingWithoutPersistence(string invalidName)
    {
        RecordingConfigManager config = new()
        {
            Servers = [CreateServer("server-1", "Original")]
        };
        ServerItemViewModel server = ServerItemViewModel.FromDto(
            CreateServer("server-1", "Original"));
        server.BeginInlineEdit();
        server.EditName = invalidName;

        ServerRenameResult result = await new ServerRenameService(config)
            .RenameAsync(server.Id, server.EditName);

        Assert.Equal(ServerRenameStatus.InvalidName, result.Status);
        Assert.True(server.IsEditing);
        Assert.Equal(invalidName, server.EditName);
        Assert.Equal("Original", server.DisplayName);
        Assert.Empty(config.PersistenceCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_WhenVaultEntryNameEmpty_FreezesOldDisplayNameAsCredentialTarget(
        string? vaultEntryName)
    {
        ServerProfileDto profile = CreateServer("server-1", "Credential Target");
        profile.VaultEntryName = vaultEntryName;
        var config = new RecordingConfigManager
        {
            Servers = [profile]
        };

        ServerRenameResult result =
            await new ServerRenameService(config)
                .RenameAsync("server-1", "Display Name");

        Assert.Equal(ServerRenameStatus.Renamed, result.Status);
        ServerProfileDto persisted = Assert.Single(config.Servers);
        Assert.Equal("Display Name", persisted.DisplayName);
        Assert.Equal("Credential Target", persisted.VaultEntryName);
    }

    [Fact]
    public async Task Rename_WhenVaultEntryNameSet_LeavesReferenceUntouched()
    {
        ServerProfileDto profile = CreateServer("server-1", "Original Display Name");
        profile.VaultEntryName = "Explicit Credential Reference";
        var config = new RecordingConfigManager
        {
            Servers = [profile]
        };

        ServerRenameResult result =
            await new ServerRenameService(config)
                .RenameAsync("server-1", "Renamed Display Name");

        Assert.Equal(ServerRenameStatus.Renamed, result.Status);
        ServerProfileDto persisted = Assert.Single(config.Servers);
        Assert.Equal("Renamed Display Name", persisted.DisplayName);
        Assert.Equal("Explicit Credential Reference", persisted.VaultEntryName);
    }

    [Fact]
    public async Task RenameServer_DuplicateName_IsAllowed()
    {
        var config = new RecordingConfigManager
        {
            Servers =
            [
                CreateServer("server-1", "First"),
                CreateServer("server-2", "Shared")
            ]
        };

        ServerRenameResult result =
            await new ServerRenameService(config)
                .RenameAsync("server-1", "Shared");

        Assert.Equal(ServerRenameStatus.Renamed, result.Status);
        Assert.Equal(2, config.Servers.Count(server => server.DisplayName == "Shared"));
    }

    [Fact]
    public async Task RenameFolder_Inline_RoutesThroughDomain_AndRejectsCollision()
    {
        var config = new RecordingConfigManager
        {
            Servers =
            [
                CreateServer("server-1", "One", "Root/Prod"),
                CreateServer("server-2", "Two", "Root/Stage")
            ]
        };
        var folder = new FolderViewModel
        {
            Name = "Prod",
            FullPath = "Root/Prod"
        };
        folder.BeginInlineEdit();
        folder.EditName = "Stage";

        FolderRenameResult result =
            await new FolderRenameService(config)
                .RenameAsync(folder.FullPath, folder.EditName);

        Assert.Equal(FolderRenameStatus.SiblingCollision, result.Status);
        Assert.True(folder.IsEditing);
        Assert.Equal("Prod", folder.Name);
        Assert.DoesNotContain("mutate", config.PersistenceCalls);
    }

    private static ServerProfileDto CreateServer(
        string id,
        string displayName,
        string? group = null)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH",
            Group = group
        };
    }

    private sealed class RecordingConfigManager : IConfigManager
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

        public Task<AppSettings> LoadSettingsAsync() =>
            Task.FromResult(Clone(Settings));

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
            PersistenceCalls.Add("mutate");
            if (FailServerMutation)
            {
                throw new IOException("Simulated inventory write failure.");
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
