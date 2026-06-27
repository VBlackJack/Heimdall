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

using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

// TrySaveTokenAsync now writes through CredentialProtector (vault-aware), so this
// class shares the static CredentialProtector state with the vault tests and must
// not run concurrently with them.
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class CommandLibrarySettingsServiceTests
{
    [Fact]
    public async Task TrySaveTokenAsync_WithPlaintext_PersistsEncryptedToken()
    {
        FakeConfigManager configManager = new();
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        bool saved = await service.TrySaveTokenAsync("secret-token");

        saved.Should().BeTrue();
        configManager.MergeSettingCallCount.Should().Be(1);
        configManager.Settings.CmdLibGitSyncToken.Should().NotBeNullOrEmpty();
        configManager.Settings.CmdLibGitSyncToken.Should().NotBe("secret-token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TrySaveTokenAsync_WithEmptyToken_ReturnsFalseAndDoesNotPersist(string? plaintext)
    {
        FakeConfigManager configManager = new();
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        bool saved = await service.TrySaveTokenAsync(plaintext!);

        saved.Should().BeFalse();
        configManager.MergeSettingCallCount.Should().Be(0);
        configManager.Settings.CmdLibGitSyncToken.Should().BeNull();
    }

    [Fact]
    public async Task TrySaveTokenAsync_WhenMergeFails_ReturnsFalseWithoutThrowing()
    {
        FakeConfigManager configManager = new()
        {
            ThrowOnMergeSetting = true
        };
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        bool saved = await service.TrySaveTokenAsync("secret-token");

        saved.Should().BeFalse();
        configManager.Settings.CmdLibGitSyncToken.Should().BeNull();
    }

    [Fact]
    public async Task TryClearTokenAsync_WhenMergeSucceeds_ClearsStoredToken()
    {
        FakeConfigManager configManager = new(new AppSettings
        {
            CmdLibGitSyncToken = "encrypted-token"
        });
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        bool cleared = await service.TryClearTokenAsync();

        cleared.Should().BeTrue();
        configManager.MergeSettingCallCount.Should().Be(1);
        configManager.Settings.CmdLibGitSyncToken.Should().BeNull();
    }

    [Fact]
    public async Task TryClearTokenAsync_WhenMergeFails_ReturnsFalseWithoutThrowing()
    {
        FakeConfigManager configManager = new(new AppSettings
        {
            CmdLibGitSyncToken = "encrypted-token"
        })
        {
            ThrowOnMergeSetting = true
        };
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        bool cleared = await service.TryClearTokenAsync();

        cleared.Should().BeFalse();
        configManager.Settings.CmdLibGitSyncToken.Should().Be("encrypted-token");
    }

    [Fact]
    public async Task GetSaveErrorStatusText_ReturnsLocalizedText()
    {
        FakeConfigManager configManager = new();
        CommandLibrarySettingsService service = await CreateServiceAsync(configManager);

        string status = service.GetSaveErrorStatusText();

        status.Should().Be("Save failed");
    }

    private static async Task<CommandLibrarySettingsService> CreateServiceAsync(FakeConfigManager configManager)
    {
        return new CommandLibrarySettingsService(
            configManager,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
    }

    private sealed class FakeConfigManager : IConfigManager
    {
        public FakeConfigManager()
            : this(new AppSettings())
        {
        }

        public FakeConfigManager(AppSettings settings)
        {
            Settings = settings;
        }

        public AppSettings Settings { get; private set; }

        public bool ThrowOnMergeSetting { get; init; }

        public int MergeSettingCallCount { get; private set; }

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://config/settings.json";

        public string ServersPath => "mem://config/servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);

        public Task SaveSettingsAsync(AppSettings settings)
        {
            Settings = settings;
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
            => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
            => Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeSettingCallCount++;
            if (ThrowOnMergeSetting)
            {
                throw new InvalidOperationException("Synthetic persistence failure.");
            }

            mutate(Settings);
            SettingsChanged?.Invoke(Settings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
            => Task.FromResult(new List<ServerProfileDto>());

        public Task SaveServersAsync(List<ServerProfileDto> servers)
            => Task.CompletedTask;
    }
}
