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

using System.Text;
using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class ConfigManagerSchemaResilienceTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly ConfigManager _manager;

    public ConfigManagerSchemaResilienceTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "Heimdall.Schema.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "config"));
        _manager = new ConfigManager(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("inventory")]
    public async Task UnknownField_SurvivesLoadThenSave(string documentKind)
    {
        if (documentKind == "settings")
        {
            await WriteUtf8Async(
                _manager.SettingsPath,
                """
                {
                  "schemaVersion": 1,
                  "defaultTheme": "Original",
                  "futureSettingsField": {
                    "enabled": true,
                    "label": "preserve-me"
                  }
                }
                """);

            AppSettings settings = await _manager.LoadSettingsAsync();
            settings.DefaultTheme = "Changed";
            await _manager.SaveSettingsAsync(settings);

            using JsonDocument written = JsonDocument.Parse(
                await File.ReadAllTextAsync(_manager.SettingsPath));
            JsonElement unknown = written.RootElement.GetProperty("futureSettingsField");
            Assert.True(unknown.GetProperty("enabled").GetBoolean());
            Assert.Equal("preserve-me", unknown.GetProperty("label").GetString());
            return;
        }

        await WriteUtf8Async(
            _manager.ServersPath,
            """
            {
              "schemaVersion": 1,
              "futureInventoryField": {
                "format": "preserve-me"
              },
              "servers": [
                {
                  "id": "server-1",
                  "displayName": "Original",
                  "remoteServer": "server.example.test",
                  "futureProfileField": {
                    "mode": "preserve-me"
                  }
                }
              ]
            }
            """);

        List<ServerProfileDto> servers = await _manager.LoadServersAsync();
        servers[0].DisplayName = "Changed";
        await _manager.SaveServersAsync(servers);

        using JsonDocument inventory = JsonDocument.Parse(
            await File.ReadAllTextAsync(_manager.ServersPath));
        Assert.Equal(
            "preserve-me",
            inventory.RootElement
                .GetProperty("futureInventoryField")
                .GetProperty("format")
                .GetString());
        Assert.Equal(
            "preserve-me",
            inventory.RootElement
                .GetProperty("servers")[0]
                .GetProperty("futureProfileField")
                .GetProperty("mode")
                .GetString());
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("inventory")]
    public async Task NewerSchemaVersion_IsNotOverwrittenOnSave(string documentKind)
    {
        string path;
        if (documentKind == "settings")
        {
            path = _manager.SettingsPath;
            await WriteUtf8Async(
                path,
                """
                {
                  "schemaVersion": 2,
                  "defaultTheme": "Future",
                  "futureSettingsField": "preserve-me"
                }
                """);
            byte[] originalBytes = await File.ReadAllBytesAsync(path);
            AppSettings settings = await _manager.LoadSettingsAsync();
            settings.DefaultTheme = "Changed";

            ConfigurationSchemaVersionException exception =
                await Assert.ThrowsAsync<ConfigurationSchemaVersionException>(
                    () => _manager.SaveSettingsAsync(settings));

            Assert.Equal(2, exception.FoundVersion);
            Assert.Equal(AppSettings.CurrentSchemaVersion, exception.SupportedVersion);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
            return;
        }

        path = _manager.ServersPath;
        await WriteUtf8Async(
            path,
            """
            {
              "schemaVersion": 2,
              "futureInventoryField": "preserve-me",
              "servers": [
                {
                  "id": "server-1",
                  "displayName": "Future",
                  "remoteServer": "server.example.test"
                }
              ]
            }
            """);
        byte[] inventoryBytes = await File.ReadAllBytesAsync(path);
        List<ServerProfileDto> servers = await _manager.LoadServersAsync();
        servers[0].DisplayName = "Changed";

        ConfigurationSchemaVersionException inventoryException =
            await Assert.ThrowsAsync<ConfigurationSchemaVersionException>(
                () => _manager.SaveServersAsync(servers));

        Assert.Equal(2, inventoryException.FoundVersion);
        Assert.Equal(
            ServerInventoryDocument.CurrentSchemaVersion,
            inventoryException.SupportedVersion);
        Assert.Equal(inventoryBytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("inventory")]
    public async Task CurrentSchemaVersion_RoundTripsNormally(string documentKind)
    {
        if (documentKind == "settings")
        {
            await _manager.SaveSettingsAsync(new AppSettings { DefaultTheme = "Current" });

            AppSettings settings = await _manager.LoadSettingsAsync();
            using JsonDocument written = JsonDocument.Parse(
                await File.ReadAllTextAsync(_manager.SettingsPath));

            Assert.Equal("Current", settings.DefaultTheme);
            Assert.Equal(
                AppSettings.CurrentSchemaVersion,
                written.RootElement.GetProperty("schemaVersion").GetInt32());
            return;
        }

        await _manager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "server-1",
                DisplayName = "Current",
                RemoteServer = "server.example.test"
            }
        ]);

        List<ServerProfileDto> servers = await _manager.LoadServersAsync();
        using JsonDocument inventory = JsonDocument.Parse(
            await File.ReadAllTextAsync(_manager.ServersPath));

        Assert.Equal("Current", Assert.Single(servers).DisplayName);
        Assert.Equal(
            ServerInventoryDocument.CurrentSchemaVersion,
            inventory.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Theory]
    [InlineData("settings")]
    [InlineData("inventory")]
    public async Task VersionlessDocument_LoadsAsCurrentSchema(string documentKind)
    {
        if (documentKind == "settings")
        {
            await WriteUtf8Async(
                _manager.SettingsPath,
                """
                {
                  "defaultTheme": "Legacy"
                }
                """);

            AppSettings settings = await _manager.LoadSettingsAsync();

            Assert.Equal("Legacy", settings.DefaultTheme);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            return;
        }

        await WriteUtf8Async(
            _manager.ServersPath,
            """
            [
              {
                "id": "server-1",
                "displayName": "Legacy",
                "remoteServer": "server.example.test"
              }
            ]
            """);

        List<ServerProfileDto> servers = await _manager.LoadServersAsync();

        Assert.Equal("Legacy", Assert.Single(servers).DisplayName);
    }

    private static Task WriteUtf8Async(string path, string content) =>
        File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
}
