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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

/// <summary>
/// The configuration manager refuses an identifier from the shared session namespace arriving in
/// the server inventory or the gateway list, on every write door, and lets a resident stay.
/// </summary>
public sealed class ConfigManagerSharedIdentifierDoorTests : IDisposable
{
    private static readonly string SharedId = SharedSessionIds.PrefixFor("teamfile1") + "entry";

    private readonly string _root;
    private readonly ConfigManager _configManager;

    public ConfigManagerSharedIdentifierDoorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Heimdall.SharedDoor." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _configManager = new ConfigManager(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_configManager.SettingsPath)!);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task MutateServersAsync_SharedArrival_IsRefusedAndNothingIsWritten()
    {
        await _configManager.SaveServersAsync([Profile("local-1")]);
        string before = await File.ReadAllTextAsync(_configManager.ServersPath);

        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => _configManager.MutateServersAsync(servers =>
            {
                servers.Add(Profile(SharedId));
                return true;
            }));

        Assert.Equal(before, await File.ReadAllTextAsync(_configManager.ServersPath));
    }

    [Fact]
    public async Task MutateServersAsync_OrdinaryArrival_IsAccepted()
    {
        await _configManager.MutateServersAsync(servers =>
        {
            servers.Add(Profile("local-2"));
            return true;
        });

        Assert.Equal("local-2", Assert.Single(await _configManager.LoadServersAsync()).Id);
    }

    [Fact]
    public async Task SaveServersAsync_SharedArrival_IsRefused()
    {
        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => _configManager.SaveServersAsync([Profile(SharedId)]));

        Assert.Empty(await _configManager.LoadServersAsync());
    }

    [Fact]
    public async Task MutateServersAsync_SharedResident_DoesNotBlockOtherWrites()
    {
        // Written past the door, as a hand edit or an older build would have left it.
        await File.WriteAllTextAsync(
            _configManager.ServersPath,
            $$"""{ "SchemaVersion": 1, "Servers": [ { "Id": "{{SharedId}}", "DisplayName": "resident" } ] }""");

        await _configManager.MutateServersAsync(servers =>
        {
            servers.Add(Profile("local-3"));
            return true;
        });

        Assert.Equal(new[] { SharedId, "local-3" }, (await _configManager.LoadServersAsync()).Select(server => server.Id));
    }

    [Fact]
    public async Task MergeSettingAsync_SharedGatewayArrival_IsRefusedAndNothingIsWritten()
    {
        await _configManager.SaveSettingsAsync(new AppSettings());
        string before = await File.ReadAllTextAsync(_configManager.SettingsPath);

        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => _configManager.MergeSettingAsync(settings => settings.SshGateways.Add(Gateway(SharedId))));

        Assert.Equal(before, await File.ReadAllTextAsync(_configManager.SettingsPath));
    }

    [Fact]
    public async Task SaveSettingsAsync_SharedGatewayArrival_IsRefused()
    {
        var settings = new AppSettings();
        settings.SshGateways.Add(Gateway(SharedId));

        await Assert.ThrowsAsync<ConfigurationValidationException>(() => _configManager.SaveSettingsAsync(settings));
    }

    [Fact]
    public async Task MergeSettingAsync_OrdinaryGateway_IsAccepted()
    {
        await _configManager.MergeSettingAsync(settings => settings.SshGateways.Add(Gateway("local-gateway")));

        Assert.Equal("local-gateway", Assert.Single((await _configManager.LoadSettingsAsync()).SshGateways).Id);
    }

    [Fact]
    public async Task CommitMigrationAsync_SharedArrival_IsRefusedAndRolledBack()
    {
        await _configManager.SaveSettingsAsync(new AppSettings());
        await _configManager.SaveServersAsync([]);
        string settingsBefore = await File.ReadAllTextAsync(_configManager.SettingsPath);
        string serversBefore = await File.ReadAllTextAsync(_configManager.ServersPath);

        await Assert.ThrowsAsync<ConfigurationValidationException>(
            () => _configManager.CommitMigrationAsync(
                settings => settings.SshGateways.Add(Gateway(SharedId)),
                servers => servers.Add(Profile("local-4"))));

        Assert.Equal(settingsBefore, await File.ReadAllTextAsync(_configManager.SettingsPath));
        Assert.Equal(serversBefore, await File.ReadAllTextAsync(_configManager.ServersPath));
    }

    private static ServerProfileDto Profile(string id) =>
        new() { Id = id, DisplayName = id, ConnectionType = "SSH", RemoteServer = "srv.example.test" };

    private static SshGatewayDto Gateway(string id) =>
        new() { Id = id, Name = id, Host = "gw.example.test", User = "jdoe" };
}
