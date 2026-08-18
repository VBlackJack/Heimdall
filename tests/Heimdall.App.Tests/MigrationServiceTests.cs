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
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;

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

    // The hole this lot closes. The pre-write failure above was already covered; a failure AFTER the
    // settings write was not, and it left settings durably replaced, the runtime snapshot published and
    // SettingsImported true on a migration reported as failed.
    [Fact]
    public async Task ImportFromLegacyAsync_ServerWriteFailsAfterSettingsWrite_RestoresEverything()
    {
        AppSettings currentSettings = new()
        {
            DefaultResolutionWidth = 1600,
            DefaultLocale = "en",
            DefaultTheme = "Slate"
        };
        await _configManager.SaveSettingsAsync(currentSettings);
        byte[] originalSettingsBytes = await File.ReadAllBytesAsync(_configManager.SettingsPath);
        AppSettings? baselineRuntime = _configManager.CurrentSettings;
        Assert.NotNull(baselineRuntime);

        WriteLegacyFile(Path.Combine("config", "settings.json"),
            """
            {
              "DefaultResolutionWidth": 1920,
              "DefaultLocale": "fr",
              "DefaultTheme": "Dracula"
            }
            """);
        WriteLegacyFile(Path.Combine("config", "servers.json"),
            """[{ "Name": "srv", "Host": "h", "Protocol": "SSH" }]""");

        // The target inventory declares a schema this build refuses for writing, so the servers write
        // fails only AFTER the settings write has already landed.
        await File.WriteAllTextAsync(
            _configManager.ServersPath,
            """{ "SchemaVersion": 99999, "Servers": [] }""");
        byte[] originalServersBytes = await File.ReadAllBytesAsync(_configManager.ServersPath);

        int settingsChangedCount = 0;
        _configManager.SettingsChanged += _ => settingsChangedCount++;

        MigrationResult result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.False(result.Success);
        Assert.False(result.SettingsImported);

        // Both files byte-identical to their baselines.
        Assert.Equal(originalSettingsBytes, await File.ReadAllBytesAsync(_configManager.SettingsPath));
        Assert.Equal(originalServersBytes, await File.ReadAllBytesAsync(_configManager.ServersPath));

        // No candidate ever reached the runtime, and nobody was told about one.
        Assert.Equal(0, settingsChangedCount);
        AppSettings? runtimeAfter = _configManager.CurrentSettings;
        Assert.NotNull(runtimeAfter);
        Assert.Equal(baselineRuntime!.DefaultResolutionWidth, runtimeAfter!.DefaultResolutionWidth);
        Assert.Equal(baselineRuntime.DefaultLocale, runtimeAfter.DefaultLocale);
        Assert.Equal(baselineRuntime.DefaultTheme, runtimeAfter.DefaultTheme);
    }

    // An empty legacy inventory is not "nothing to do": the existing non-empty path replaces the
    // inventory wholesale, so it must empty a populated target.
    [Fact]
    public async Task ImportFromLegacyAsync_EmptyLegacyInventory_EmptiesAPopulatedTarget()
    {
        await _configManager.MutateServersAsync(inventory =>
        {
            inventory.Add(new ServerProfileDto { Id = "existing", DisplayName = "existing" });
            return inventory.Count;
        });
        Assert.Single(await _configManager.LoadServersAsync());

        WriteLegacyFile(Path.Combine("config", "settings.json"), """{ "DefaultLocale": "fr" }""");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        MigrationResult result = await _service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);
        Assert.Empty(await _configManager.LoadServersAsync());
    }

    // A configuration manager that cannot commit both files as one unit must make the import refuse
    // before touching anything, and that refusal belongs in the result the caller already handles.
    [Fact]
    public async Task ImportFromLegacyAsync_ConfigManagerCannotCommitAtomically_RefusesWithoutWriting()
    {
        NonTransactionalConfigManager configManager = new();
        MigrationService service = new(configManager, new LocalizationManager());

        WriteLegacyFile(Path.Combine("config", "settings.json"), """{ "DefaultLocale": "fr" }""");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        MigrationResult result = await service.ImportFromLegacyAsync(_legacyPath);

        Assert.False(result.Success);
        Assert.False(result.SettingsImported);
        Assert.NotNull(result.Error);

        // No fallback to two independent writes.
        Assert.Equal(0, configManager.SaveSettingsCalls);
        Assert.Equal(0, configManager.MutateServersCalls);
    }

    // The legacy values must be applied to the state the commit provides, not to anything read before
    // it. If the migration captured settings up front, a host key trusted while it was running would be
    // silently discarded when the stale snapshot was written back.
    [Fact]
    public async Task ImportFromLegacyAsync_AppliesLegacyValuesToTheFreshCommitTarget()
    {
        FreshTargetConfigManager configManager = new();
        MigrationService service = new(configManager, new LocalizationManager());

        WriteLegacyFile(Path.Combine("config", "settings.json"),
            """{ "DefaultLocale": "fr", "DefaultTheme": "Dracula" }""");
        WriteLegacyFile(Path.Combine("config", "servers.json"), "[]");

        MigrationResult result = await service.ImportFromLegacyAsync(_legacyPath);

        Assert.True(result.Success);

        // The legacy values landed...
        Assert.Equal("fr", configManager.Committed.DefaultLocale);

        // ...and the trust decision that only exists on the fresh object survived.
        Assert.True(configManager.Committed.TrustedHostKeysV2.ContainsKey("sentinel.example"));
        Assert.Equal(
            "SHA256:SENTINEL",
            configManager.Committed.TrustedHostKeysV2["sentinel.example"].Fingerprint);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_MalformedServers_DoesNotReplaceCurrentSettings()
    {
        AppSettings currentSettings = new()
        {
            DefaultResolutionWidth = 1600,
            DefaultLocale = "en",
            DefaultTheme = "Slate"
        };
        await _configManager.SaveSettingsAsync(currentSettings);
        byte[] originalSettingsBytes = await File.ReadAllBytesAsync(_configManager.SettingsPath);

        WriteLegacyFile(Path.Combine("config", "settings.json"),
            """
            {
              "DefaultResolutionWidth": 1920,
              "DefaultLocale": "fr",
              "DefaultTheme": "Dracula"
            }
            """);
        WriteLegacyFile(Path.Combine("config", "servers.json"), "{ not valid json");

        int settingsChangedCount = 0;
        _configManager.SettingsChanged += _ => settingsChangedCount++;

        MigrationResult result = await _service.ImportFromLegacyAsync(_legacyPath);
        byte[] persistedSettingsBytes = await File.ReadAllBytesAsync(_configManager.SettingsPath);
        AppSettings persistedSettings = await _configManager.LoadSettingsAsync();

        Assert.False(result.Success);
        Assert.False(result.SettingsImported);
        Assert.Equal(0, settingsChangedCount);
        Assert.Equal(originalSettingsBytes, persistedSettingsBytes);
        Assert.Equal(1600, persistedSettings.DefaultResolutionWidth);
        Assert.Equal("en", persistedSettings.DefaultLocale);
        Assert.Equal("Slate", persistedSettings.DefaultTheme);
    }

    [Fact]
    public async Task ImportFromLegacyAsync_MixedInventoryReportsSafeRejectedProfileIdentity()
    {
        const string FakeSecret = "DO-NOT-DISPLAY-FAKE-SECRET";

        WriteLegacyFile(Path.Combine("config", "settings.json"), "{}");
        WriteLegacyFile(Path.Combine("config", "servers.json"),
            $$"""
            [
              {
                "Id": "valid-profile",
                "DisplayName": "Valid profile",
                "RemoteServer": "valid.example.test",
                "RemotePort": 3389,
                "ConnectionType": "RDP"
              },
              {
                "Id": "rejected-profile",
                "DisplayName": "Rejected\r\nprofile XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
                "RemoteServer": "rejected.example.test",
                "RemotePort": 999999999999999999999999999999,
                "ConnectionType": "SSH",
                "SshPasswordEncrypted": "{{FakeSecret}}"
              }
            ]
            """);

        MigrationResult result = await _service.ImportFromLegacyAsync(_legacyPath);
        List<ServerProfileDto> persistedServers = await _configManager.LoadServersAsync();
        MigrationWarning warning = Assert.Single(result.Warnings);
        string serializedWarning = JsonSerializer.Serialize(warning);

        Assert.True(result.Success);
        Assert.Equal(2, result.ServersExamined);
        Assert.Equal(1, result.ServersImported);
        Assert.Equal(1, result.ServersSkipped);
        ServerProfileDto persistedServer = Assert.Single(persistedServers);
        Assert.Equal("valid-profile", persistedServer.Id);
        Assert.Equal(2, warning.Index);
        Assert.NotNull(warning.Identity);
        Assert.StartsWith("Rejected profile", warning.Identity, StringComparison.Ordinal);
        Assert.Equal(64, warning.Identity.Length);
        Assert.DoesNotContain('\r', warning.Identity);
        Assert.DoesNotContain('\n', warning.Identity);
        Assert.Equal(MigrationWarningReason.InvalidLegacyField, warning.Reason);
        Assert.DoesNotContain(FakeSecret, serializedWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("RemotePort", serializedWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", serializedWarning, StringComparison.OrdinalIgnoreCase);
    }
}
/// <summary>
/// A configuration manager that does not carry the transactional capability.
/// </summary>
/// <remarks>
/// Deliberately minimal and local: it exists to prove the import refuses rather than falling back to
/// two independent writes, and it counts those writes so the refusal cannot be mistaken for silence.
/// </remarks>
internal sealed class NonTransactionalConfigManager : IConfigManager
{
    private AppSettings _settings = new();
    private List<ServerProfileDto> _servers = [];

    public int SaveSettingsCalls { get; private set; }

    public int MutateServersCalls { get; private set; }

    public string ConfigPath => "mem://config";

    public string SettingsPath => "mem://settings.json";

    public string ServersPath => "mem://servers.json";

    public event Action<AppSettings>? SettingsChanged;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(_settings);

    public AppSettings? CurrentSettings => _settings;

    public Task SaveSettingsAsync(AppSettings settings)
    {
        SaveSettingsCalls++;
        _settings = settings;
        SettingsChanged?.Invoke(settings);
        return Task.CompletedTask;
    }

    public Task MergeSettingAsync(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        mutate(_settings);
        return Task.CompletedTask;
    }

    public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(_servers);

    public Task SaveServersAsync(List<ServerProfileDto> servers)
    {
        _servers = servers;
        return Task.CompletedTask;
    }

    public Task<bool> MergeHostKeyAsync(string host, string fingerprint) => Task.FromResult(true);

    public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> hostKeys)
        => Task.FromResult(0);

    public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        MutateServersCalls++;
        return Task.FromResult(mutate(_servers));
    }
}
/// <summary>
/// A transactional configuration manager whose committed state is deliberately fresher than what
/// <see cref="LoadSettingsAsync"/> reports.
/// </summary>
/// <remarks>
/// This is what makes the fresh-target guarantee measurable. The migration must map the legacy values
/// onto the object handed to the commit, not onto anything it read beforehand: a trust decision
/// persisted while the migration was running lives only in the fresh object, and a snapshot captured
/// earlier would silently drop it.
/// </remarks>
internal sealed class FreshTargetConfigManager : IConfigManager, IConfigTransactionalWriter
{
    /// <summary>What a pre-transaction read sees: no trusted key yet.</summary>
    private readonly AppSettings _staleView = new() { DefaultLocale = "en" };

    /// <summary>The state the commit actually mutates, carrying a key trusted in the meantime.</summary>
    public AppSettings Committed { get; } = new()
    {
        DefaultLocale = "en",
        TrustedHostKeysV2 =
        {
            ["sentinel.example"] = new HostKeyEntry(
                "SHA256:SENTINEL",
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                "ssh-ed25519",
                HostKeySource.Unknown),
        },
    };

    public List<ServerProfileDto> Servers { get; } = [];

    public string ConfigPath => "mem://config";

    public string SettingsPath => "mem://settings.json";

    public string ServersPath => "mem://servers.json";

    public event Action<AppSettings>? SettingsChanged;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(_staleView);

    public AppSettings? CurrentSettings => Committed;

    public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;

    public Task MergeSettingAsync(Action<AppSettings> mutate) => Task.CompletedTask;

    public Task<bool> MergeHostKeyAsync(string host, string fingerprint) => Task.FromResult(true);

    public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> hostKeys)
        => Task.FromResult(0);

    public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(Servers);

    public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;

    public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return Task.FromResult(mutate(Servers));
    }

    public Task CommitMigrationAsync(
        Action<AppSettings> applySettingsMutation,
        Action<List<ServerProfileDto>> applyServersMutation)
    {
        ArgumentNullException.ThrowIfNull(applySettingsMutation);
        ArgumentNullException.ThrowIfNull(applyServersMutation);

        // The mutation is handed the fresh object, exactly as the real manager does under its lock.
        applySettingsMutation(Committed);
        applyServersMutation(Servers);
        SettingsChanged?.Invoke(Committed);
        return Task.CompletedTask;
    }
}
