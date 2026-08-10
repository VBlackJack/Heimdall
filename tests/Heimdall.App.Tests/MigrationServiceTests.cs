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
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// Tests for <see cref="MigrationService"/> — legacy Heimdall (PowerShell)
/// installation detection and import flow.
/// </summary>
public class MigrationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyPath;
    private readonly string _newBasePath;
    private readonly ConfigManager _configManager;
    private readonly LocalizationManager _localizer;
    private readonly MigrationService _service;

    public MigrationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"heimdall-migration-test-{Guid.NewGuid():N}");
        _legacyPath = Path.Combine(_root, "legacy");
        _newBasePath = Path.Combine(_root, "new");
        Directory.CreateDirectory(Path.Combine(_legacyPath, "config"));
        Directory.CreateDirectory(Path.Combine(_newBasePath, "config"));

        _configManager = new ConfigManager(_newBasePath);
        _localizer = new LocalizationManager();
        _service = new MigrationService(_configManager, _localizer);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
        GC.SuppressFinalize(this);
    }

    private void WriteLegacyFile(string relative, string contents)
    {
        var path = Path.Combine(_legacyPath, relative);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents);
    }

    // ── DetectLegacyInstallation ─────────────────────────────────────────

    [Fact]
    public void DetectLegacyInstallation_Returns_True_When_Both_Files_Exist()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        Assert.True(MigrationService.DetectLegacyInstallation(_legacyPath));
    }

    [Fact]
    public void DetectLegacyInstallation_Returns_False_When_Directory_Missing()
    {
        var bogus = Path.Combine(_root, "nonexistent");
        Assert.False(MigrationService.DetectLegacyInstallation(bogus));
    }

    [Fact]
    public void DetectLegacyInstallation_Returns_False_When_Settings_File_Missing()
    {
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");
        // settings.json deliberately not created
        Assert.False(MigrationService.DetectLegacyInstallation(_legacyPath));
    }

    [Fact]
    public void DetectLegacyInstallation_Returns_False_When_Servers_File_Missing()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        // servers.json deliberately not created
        Assert.False(MigrationService.DetectLegacyInstallation(_legacyPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DetectLegacyInstallation_Returns_False_For_NullOrWhitespace(string? path)
    {
        Assert.False(MigrationService.DetectLegacyInstallation(path!));
    }

    // ── ImportFromLegacyAsync ────────────────────────────────────────────

    [Fact]
    public async Task ImportFromLegacyAsync_Throws_For_NullOrEmpty_Path()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.ImportFromLegacyAsync(""));
    }

    [Fact]
    public async Task ImportFromLegacyAsync_Returns_Failure_When_Files_Missing()
    {
        // Legacy directory exists but contains no config files.
        var result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_Imports_Valid_Settings_File()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"),
            """
            {
              "DefaultResolutionWidth": 1920,
              "DefaultResolutionHeight": 1080,
              "FullScreen": true,
              "DefaultLocale": "fr",
              "DefaultTheme": "DraculaPro",
              "EnableLogging": true
            }
            """);
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        var result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);
        Assert.True(result.SettingsImported);
        Assert.Empty(result.Warnings);

        // Round-trip: load the freshly written settings and verify the mapped values.
        var settings = await _configManager.LoadSettingsAsync();
        Assert.Equal(1920, settings.DefaultResolutionWidth);
        Assert.Equal(1080, settings.DefaultResolutionHeight);
        Assert.True(settings.FullScreen);
        Assert.Equal("fr", settings.DefaultLocale);
        Assert.Equal("DraculaPro", settings.DefaultTheme);
        Assert.True(settings.EnableLogging);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_Imports_Server_Inventory()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        WriteLegacyFile(Path.Combine("config", "servers.json"),
            """
            [
              {
                "Id": "srv-001",
                "DisplayName": "Test Box",
                "RemoteServer": "10.0.0.1",
                "RemotePort": 3389,
                "ConnectionType": "RDP"
              },
              {
                "Id": "srv-002",
                "DisplayName": "SSH Box",
                "RemoteServer": "10.0.0.2",
                "RemotePort": 22,
                "ConnectionType": "SSH"
              }
            ]
            """);

        var result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);
        Assert.Equal(2, result.ServersImported);
        Assert.Empty(result.Warnings);

        var servers = await _configManager.LoadServersAsync();
        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, s => s.Id == "srv-001" && s.DisplayName == "Test Box");
        Assert.Contains(servers, s => s.Id == "srv-002" && s.ConnectionType == "SSH");
    }

    [Fact]
    public async Task ImportFromLegacyAsync_PreservesEncryptedSshPasswordAndMacAddressExactly()
    {
        const string LegacySshPassword = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA|opaque==";
        const string LegacyMacAddress = "AA:bb:CC:dd:EE:fF";

        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        WriteLegacyFile(Path.Combine("config", "servers.json"),
            $$"""
            [
              {
                "Id": "srv-sensitive-fields",
                "DisplayName": "Legacy SSH",
                "RemoteServer": "10.0.0.3",
                "ConnectionType": "SSH",
                "SshPasswordEncrypted": "{{LegacySshPassword}}",
                "MacAddress": "{{LegacyMacAddress}}"
              }
            ]
            """);

        MigrationResult result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);
        Assert.Equal(1, result.ServersImported);
        Assert.Empty(result.Warnings);

        List<ServerProfileDto> servers = await _configManager.LoadServersAsync();
        ServerProfileDto imported = Assert.Single(servers);
        Assert.Equal(LegacySshPassword, imported.SshPasswordEncrypted);
        Assert.Equal(LegacyMacAddress, imported.MacAddress);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_Empty_Server_Array_Reports_Zero_Imported()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        var result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);
        Assert.True(result.SettingsImported);
        Assert.Equal(0, result.ServersImported);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_Malformed_Settings_Returns_Failure()
    {
        WriteLegacyFile(Path.Combine("config", "settings.json"), "{ not valid json");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        var result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
