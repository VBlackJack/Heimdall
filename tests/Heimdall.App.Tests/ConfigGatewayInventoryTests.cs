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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The inventory the tools share reads the configuration as it is, and follows every save.
/// </summary>
/// <remarks>
/// Against the real configuration manager on a temporary root, wired the way the session manager
/// wires it: the event from the abstraction, the snapshot from the manager's published settings.
/// </remarks>
public sealed class ConfigGatewayInventoryTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "heimdall-gateway-inventory", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_rootPath, recursive: true);
        }
        catch
        {
            // Test cleanup.
        }
    }

    [Fact]
    public async Task Current_ReadsThePublishedSettingsOnEveryCall()
    {
        ConfigManager configManager = await CreateConfigManagerAsync();
        using ConfigGatewayInventory inventory = Wire(configManager);

        Assert.Empty(inventory.Current);

        await configManager.SaveSettingsAsync(GatewaySettings("gw-1", "Paris"));

        Assert.Equal("gw-1", Assert.Single(inventory.Current).Id);
    }

    [Fact]
    public async Task Changed_CarriesTheGatewaysOfTheSaveThatRaisedIt()
    {
        ConfigManager configManager = await CreateConfigManagerAsync();
        using ConfigGatewayInventory inventory = Wire(configManager);
        List<IReadOnlyList<SshGatewayDto>> announced = [];
        inventory.Changed += announced.Add;

        await configManager.SaveSettingsAsync(GatewaySettings("gw-1", "Paris"));
        await configManager.SaveSettingsAsync(new AppSettings());

        Assert.Equal(2, announced.Count);
        Assert.Equal("Paris", Assert.Single(announced[0]).Name);
        Assert.Empty(announced[1]);
    }

    [Fact]
    public async Task Dispose_ReleasesTheSettingsSubscription()
    {
        ConfigManager configManager = await CreateConfigManagerAsync();
        ConfigGatewayInventory inventory = Wire(configManager);
        int announced = 0;
        inventory.Changed += _ => announced++;

        inventory.Dispose();
        await configManager.SaveSettingsAsync(GatewaySettings("gw-1", "Paris"));

        Assert.Equal(0, announced);
    }

    private static ConfigGatewayInventory Wire(ConfigManager configManager)
        => new(configManager, () => configManager.CurrentSettings);

    private async Task<ConfigManager> CreateConfigManagerAsync()
    {
        ConfigManager configManager = new(_rootPath);
        await configManager.InitializeAsync();
        return configManager;
    }

    private static AppSettings GatewaySettings(string id, string name)
    {
        AppSettings settings = new();
        settings.SshGateways.Add(new SshGatewayDto
        {
            Id = id,
            Name = name,
            Host = "127.0.0.1",
            Port = 22,
            User = "bastion"
        });

        return settings;
    }
}
