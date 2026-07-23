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

using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.Tests.Vault;

namespace Heimdall.Core.Tests;

[Collection(CredentialProtectorStaticCollection.Name)]
[SupportedOSPlatform("windows")]
public class ConfigManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ConfigManager _manager;

    public ConfigManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _manager = new ConfigManager(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    // ── Constructor / Path computation ─────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullOrWhiteSpace()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigManager(null!));
        Assert.Throws<ArgumentException>(() => new ConfigManager(""));
        Assert.Throws<ArgumentException>(() => new ConfigManager("   "));
    }

    [Fact]
    public void Paths_AreCorrectlyComputed()
    {
        Assert.Equal(Path.Combine(_tempDir, "config"), _manager.ConfigPath);
        Assert.Equal(Path.Combine(_tempDir, "config", "settings.json"), _manager.SettingsPath);
        Assert.Equal(Path.Combine(_tempDir, "config", "servers.json"), _manager.ServersPath);
    }

    [Fact]
    public void DataRoot_ResolvesToLocalAppData_AppFolder()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppConstants.ApplicationFolderName);

        Assert.Equal(expected, ApplicationDataPathResolver.Resolve());
    }

    // ── InitializeAsync ────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CreatesConfigAndLogsDirectories()
    {
        await _manager.InitializeAsync();

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "config")));
        Assert.True(Directory.Exists(
            ApplicationDataPathResolver.GetLogsDirectory(_manager.ConfigPath)));
    }

    [Fact]
    public async Task ConfigManager_WritesRuntimeFilesUnderDataRoot_NotInstallRoot()
    {
        string installRoot = Path.Combine(_tempDir, "install");
        string dataRoot = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(installRoot);
        var manager = new ConfigManager(installRoot, dataRoot);

        await manager.InitializeAsync();
        await manager.SaveSettingsAsync(new AppSettings { DefaultLocale = "fr" });
        await manager.SaveServersAsync([]);

        Assert.Equal(dataRoot, manager.ConfigPath);
        Assert.Equal(Path.Combine(dataRoot, "settings.json"), manager.SettingsPath);
        Assert.Equal(Path.Combine(dataRoot, "servers.json"), manager.ServersPath);
        Assert.True(File.Exists(manager.SettingsPath));
        Assert.True(File.Exists(manager.ServersPath));
        Assert.False(File.Exists(Path.Combine(installRoot, "config", "settings.json")));
        Assert.False(File.Exists(Path.Combine(installRoot, "config", "servers.json")));
    }

    [Fact]
    public async Task ConfigManager_ResolvesDefaultsFromInstallRoot()
    {
        string installRoot = Path.Combine(_tempDir, "install-default-resolution");
        string dataRoot = Path.Combine(_tempDir, "data-default-resolution");
        string bundledConfigPath = Path.Combine(installRoot, "config");
        Directory.CreateDirectory(bundledConfigPath);
        await File.WriteAllTextAsync(
            Path.Combine(bundledConfigPath, "settings.default.json"),
            """{"defaultLocale":"fr","defaultTheme":"Blade"}""",
            new UTF8Encoding(false));

        var manager = new ConfigManager(installRoot, dataRoot);
        await manager.InitializeAsync();

        AppSettings settings = await manager.LoadSettingsAsync();
        Assert.Equal("fr", settings.DefaultLocale);
        Assert.Equal("Blade", settings.DefaultTheme);
    }

    [Fact]
    public async Task ConfigManager_Initialize_DoesNotCreateDirectoriesUnderInstallRoot()
    {
        string installRoot = Path.Combine(_tempDir, "missing-install-root");
        string dataRoot = Path.Combine(_tempDir, "isolated-data-root");
        var manager = new ConfigManager(installRoot, dataRoot);

        await manager.InitializeAsync();

        Assert.False(Directory.Exists(installRoot));
        Assert.True(Directory.Exists(dataRoot));
        Assert.True(Directory.Exists(
            ApplicationDataPathResolver.GetLogsDirectory(dataRoot)));
    }

    [Fact]
    public async Task ConfigManager_FirstRun_CopiesBundledDefaultsIntoDataRoot()
    {
        string installRoot = Path.Combine(_tempDir, "install-first-run");
        string dataRoot = Path.Combine(_tempDir, "data-first-run");
        string bundledConfigPath = Path.Combine(installRoot, "config");
        Directory.CreateDirectory(bundledConfigPath);
        const string SettingsJson = """{"defaultLocale":"fr"}""";
        const string ServersJson = "[]";
        await File.WriteAllTextAsync(
            Path.Combine(bundledConfigPath, "settings.default.json"),
            SettingsJson,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(bundledConfigPath, "servers.default.json"),
            ServersJson,
            new UTF8Encoding(false));

        var manager = new ConfigManager(installRoot, dataRoot);
        await manager.InitializeAsync();

        Assert.Equal(SettingsJson, await File.ReadAllTextAsync(manager.SettingsPath));
        Assert.Equal(ServersJson, await File.ReadAllTextAsync(manager.ServersPath));
        Assert.False(File.Exists(Path.Combine(installRoot, "config", "settings.json")));
        Assert.False(File.Exists(Path.Combine(installRoot, "config", "servers.json")));
    }

    [Fact]
    public async Task Migration_MovesLegacyRuntimeFiles_Idempotent_DoesNotOverwriteNewer()
    {
        string installRoot = Path.Combine(_tempDir, "legacy-install");
        string dataRoot = Path.Combine(_tempDir, "migrated-data");
        string legacyConfigPath = Path.Combine(installRoot, "config");
        Directory.CreateDirectory(legacyConfigPath);
        const string LegacySettings = """{"defaultLocale":"fr"}""";
        const string LegacyServers = """[{"id":"legacy","displayName":"Legacy"}]""";
        await File.WriteAllTextAsync(
            Path.Combine(legacyConfigPath, "settings.json"),
            LegacySettings,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(legacyConfigPath, "servers.json"),
            LegacyServers,
            new UTF8Encoding(false));

        var manager = new ConfigManager(installRoot, dataRoot);
        await manager.InitializeAsync();

        Assert.Equal(LegacySettings, await File.ReadAllTextAsync(manager.SettingsPath));
        Assert.Equal(LegacyServers, await File.ReadAllTextAsync(manager.ServersPath));

        const string NewerSettings = """{"defaultLocale":"en","defaultTheme":"Matrix"}""";
        const string NewerServers = """[{"id":"newer","displayName":"Newer"}]""";
        await File.WriteAllTextAsync(
            manager.SettingsPath,
            NewerSettings,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            manager.ServersPath,
            NewerServers,
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(legacyConfigPath, "settings.json"),
            """{"defaultLocale":"de"}""",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(legacyConfigPath, "servers.json"),
            "[]",
            new UTF8Encoding(false));

        await manager.InitializeAsync();

        Assert.Equal(NewerSettings, await File.ReadAllTextAsync(manager.SettingsPath));
        Assert.Equal(NewerServers, await File.ReadAllTextAsync(manager.ServersPath));
    }

    [Fact]
    public async Task InitializeAsync_CreatesDefaultSettingsFile_WhenMissing()
    {
        await _manager.InitializeAsync();

        Assert.True(File.Exists(_manager.SettingsPath));
    }

    [Fact]
    public async Task InitializeAsync_CreatesDefaultServersFile_WhenMissing()
    {
        await _manager.InitializeAsync();

        Assert.True(File.Exists(_manager.ServersPath));
    }

    [Fact]
    public async Task InitializeAsync_CopiesDefaultSettings_WhenDefaultFileExists()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var defaultSettings = new AppSettings { DefaultLocale = "fr", DefaultTheme = "Blade" };
        var json = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "settings.default.json"), json, new UTF8Encoding(false));

        await _manager.InitializeAsync();

        var loaded = await _manager.LoadSettingsAsync();
        Assert.Equal("fr", loaded.DefaultLocale);
        Assert.Equal("Blade", loaded.DefaultTheme);
    }

    [Fact]
    public async Task InitializeAsync_CopiesDefaultServers_WhenDefaultFileExists()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var servers = new List<ServerProfileDto>
        {
            new() { Id = "srv-1", DisplayName = "Test Server", RemoteServer = "10.0.0.1" }
        };
        var json = JsonSerializer.Serialize(servers, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(
            Path.Combine(configDir, "servers.default.json"), json, new UTF8Encoding(false));

        await _manager.InitializeAsync();

        var loaded = await _manager.LoadServersAsync();
        Assert.Single(loaded);
        Assert.Equal("srv-1", loaded[0].Id);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotOverwrite_ExistingRuntimeFiles()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        // Create runtime settings with a custom locale
        var existingSettings = new AppSettings { DefaultLocale = "de" };
        var json = JsonSerializer.Serialize(existingSettings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(_manager.SettingsPath, json, new UTF8Encoding(false));

        await _manager.InitializeAsync();

        var loaded = await _manager.LoadSettingsAsync();
        Assert.Equal("de", loaded.DefaultLocale);
    }

    // ── LoadSettingsAsync ──────────────────────────────────────────────

    [Fact]
    public async Task LoadSettingsAsync_ReturnsDefaults_WhenFileMissing()
    {
        var settings = await _manager.LoadSettingsAsync();

        Assert.NotNull(settings);
        Assert.Equal("en", settings.DefaultLocale);
        Assert.Equal("Drakul", settings.DefaultTheme);
        Assert.Equal(1920, settings.DefaultResolutionWidth);
        Assert.Equal(1080, settings.DefaultResolutionHeight);
    }

    [Fact]
    public async Task LoadSettingsAsync_DeserializesValidJson()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = """
        {
            "defaultLocale": "fr",
            "defaultTheme": "Buffy",
            "defaultResolutionWidth": 2560,
            "defaultResolutionHeight": 1440,
            "fullScreen": false,
            "maxEmbeddedSessions": 5
        }
        """;
        await File.WriteAllTextAsync(_manager.SettingsPath, json, new UTF8Encoding(false));

        var settings = await _manager.LoadSettingsAsync();

        Assert.Equal("fr", settings.DefaultLocale);
        Assert.Equal("Buffy", settings.DefaultTheme);
        Assert.Equal(2560, settings.DefaultResolutionWidth);
        Assert.Equal(1440, settings.DefaultResolutionHeight);
        Assert.False(settings.FullScreen);
        Assert.Equal(5, settings.MaxEmbeddedSessions);
    }

    [Fact]
    public async Task LoadSettingsAsync_HandlesCorruptedJson_Gracefully()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        await File.WriteAllTextAsync(_manager.SettingsPath, "{ NOT VALID JSON !!!", new UTF8Encoding(false));

        await Assert.ThrowsAsync<JsonException>(() => _manager.LoadSettingsAsync());
    }

    [Fact]
    public async Task LoadSettingsAsync_HandlesEmptyJsonObject()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        await File.WriteAllTextAsync(_manager.SettingsPath, "{}", new UTF8Encoding(false));

        var settings = await _manager.LoadSettingsAsync();

        Assert.NotNull(settings);
        // Defaults should be applied by AppSettings constructor
        Assert.Equal("en", settings.DefaultLocale);
    }

    [Fact]
    public void CollapseTunnelsPanelByDefault_DefaultIsTrue()
    {
        var settings = new AppSettings();

        Assert.True(settings.CollapseTunnelsPanelByDefault);
    }

    // ── SaveSettingsAsync / round-trip ──────────────────────────────────

    [Fact]
    public async Task SaveSettingsAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.SaveSettingsAsync(null!));
    }

    [Fact]
    public async Task SaveSettingsAsync_RoundTrips_WithLoadSettingsAsync()
    {
        var original = new AppSettings
        {
            DefaultLocale = "fr",
            DefaultTheme = "Morbius",
            DefaultResolutionWidth = 3840,
            DefaultResolutionHeight = 2160,
            FullScreen = false,
            AdminMode = false,
            MaxEmbeddedSessions = 20,
            TunnelEstablishmentDelayMs = 5000,
            EnableLogging = false,
            SshDefaultMode = "Embedded",
            RdpDefaultMode = "Embedded"
        };

        await _manager.SaveSettingsAsync(original);
        var loaded = await _manager.LoadSettingsAsync();

        Assert.Equal(original.DefaultLocale, loaded.DefaultLocale);
        Assert.Equal(original.DefaultTheme, loaded.DefaultTheme);
        Assert.Equal(original.DefaultResolutionWidth, loaded.DefaultResolutionWidth);
        Assert.Equal(original.DefaultResolutionHeight, loaded.DefaultResolutionHeight);
        Assert.Equal(original.FullScreen, loaded.FullScreen);
        Assert.Equal(original.AdminMode, loaded.AdminMode);
        Assert.Equal(original.MaxEmbeddedSessions, loaded.MaxEmbeddedSessions);
        Assert.Equal(original.TunnelEstablishmentDelayMs, loaded.TunnelEstablishmentDelayMs);
        Assert.Equal(original.EnableLogging, loaded.EnableLogging);
        Assert.Equal(original.SshDefaultMode, loaded.SshDefaultMode);
        Assert.Equal(original.RdpDefaultMode, loaded.RdpDefaultMode);
    }

    [Fact]
    public async Task CollapseTunnelsPanelByDefault_RoundTrip_PreservesFalse()
    {
        var original = new AppSettings
        {
            CollapseTunnelsPanelByDefault = false
        };

        await _manager.SaveSettingsAsync(original);
        var loaded = await _manager.LoadSettingsAsync();

        Assert.False(loaded.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public async Task SaveSettingsAsync_WritesUtf8NoBom()
    {
        await _manager.SaveSettingsAsync(new AppSettings());

        var bytes = await File.ReadAllBytesAsync(_manager.SettingsPath);

        // UTF-8 BOM is EF BB BF — verify it is absent
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "File should not contain UTF-8 BOM");
    }

    [Fact]
    public async Task SaveSettingsAsync_PreservesCollections()
    {
        var original = new AppSettings
        {
            TreeExpandedNodes = new List<string> { "node-1", "node-2" },
            EmptyGroups = new List<string> { "proj1|Infra" },
            TrustedHostKeys = new Dictionary<string, string>
            {
                ["server1:22"] = "SHA256:abc123"
            }
        };

        await _manager.SaveSettingsAsync(original);
        var loaded = await _manager.LoadSettingsAsync();

        Assert.Equal(2, loaded.TreeExpandedNodes.Count);
        Assert.Contains("node-1", loaded.TreeExpandedNodes);
        Assert.Single(loaded.EmptyGroups);
        Assert.Equal("proj1|Infra", loaded.EmptyGroups[0]);
        Assert.Single(loaded.TrustedHostKeys);
        Assert.Equal("SHA256:abc123", loaded.TrustedHostKeys["server1:22"]);
    }

    [Fact]
    public async Task SaveSettingsAsync_WritesTrustedHostKeysV2Schema()
    {
        var settings = new AppSettings
        {
            TrustedHostKeysV2 = new Dictionary<string, HostKeyEntry>
            {
                ["server1:22"] = new(
                    "SHA256:abc123",
                    DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
                    DateTimeOffset.Parse("2026-04-24T10:16:00Z"),
                    "ssh-ed25519",
                    HostKeySource.UserConfirmed)
            }
        };

        await _manager.SaveSettingsAsync(settings);

        var json = await File.ReadAllTextAsync(_manager.SettingsPath);
        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement
            .GetProperty("trustedHostKeysV2")
            .GetProperty("server1:22");

        Assert.Equal("SHA256:abc123", entry.GetProperty("fingerprint").GetString());
        Assert.Equal("ssh-ed25519", entry.GetProperty("algorithm").GetString());
        Assert.Equal("UserConfirmed", entry.GetProperty("source").GetString());
    }

    [Fact]
    public async Task SaveSettingsAsync_WritesTrustedFtpsCertificatesSchema()
    {
        var settings = new AppSettings
        {
            TrustedFtpsCertificates = new Dictionary<string, FtpsCertificateEntry>
            {
                ["ftps.example.com:21"] = new(
                    "SHA256:AA:BB",
                    DateTimeOffset.Parse("2026-04-24T10:15:00Z"),
                    DateTimeOffset.Parse("2026-04-24T10:16:00Z"),
                    "CN=ftps.example.com",
                    "CN=Test CA",
                    DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
                    DateTimeOffset.Parse("2027-04-01T00:00:00Z"),
                    FtpsCertificateSource.UserConfirmed)
                {
                    ValidationErrors = "self-signed"
                }
            }
        };

        await _manager.SaveSettingsAsync(settings);

        var json = await File.ReadAllTextAsync(_manager.SettingsPath);
        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement
            .GetProperty("trustedFtpsCertificates")
            .GetProperty("ftps.example.com:21");

        Assert.Equal("SHA256:AA:BB", entry.GetProperty("fingerprint").GetString());
        Assert.Equal("CN=ftps.example.com", entry.GetProperty("subject").GetString());
        Assert.Equal("CN=Test CA", entry.GetProperty("issuer").GetString());
        Assert.Equal("UserConfirmed", entry.GetProperty("source").GetString());
        Assert.Equal("self-signed", entry.GetProperty("validationErrors").GetString());
    }

    [Fact]
    public async Task LoadSettingsAsync_MigratesLegacyTrustedHostKeysToV2()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        await File.WriteAllTextAsync(
            _manager.SettingsPath,
            """
            {
              "trustedHostKeys": {
                "legacy.example.com:22": "SHA256:legacy"
              }
            }
            """);

        var loaded = await _manager.LoadSettingsAsync();

        var entry = Assert.Single(loaded.TrustedHostKeysV2);
        Assert.Equal("legacy.example.com:22", entry.Key);
        Assert.Equal("SHA256:legacy", entry.Value.Fingerprint);
        Assert.Equal(DateTimeOffset.MinValue, entry.Value.FirstSeen);
        Assert.Equal(DateTimeOffset.MinValue, entry.Value.LastSeen);
        Assert.Equal("unknown", entry.Value.Algorithm);
        Assert.Equal(HostKeySource.Unknown, entry.Value.Source);
        Assert.Equal("SHA256:legacy", loaded.TrustedHostKeys["legacy.example.com:22"]);
    }

    [Fact]
    public async Task MergeHostKeyAsync_WritesV2AndPreservesLegacyView()
    {
        var merged = await _manager.MergeHostKeyAsync("server1:22", "SHA256:abc123");

        var loaded = await _manager.LoadSettingsAsync();
        Assert.True(merged);
        Assert.Equal("SHA256:abc123", loaded.TrustedHostKeys["server1:22"]);

        var entry = loaded.TrustedHostKeysV2["server1:22"];
        Assert.Equal("SHA256:abc123", entry.Fingerprint);
        Assert.Equal("unknown", entry.Algorithm);
        Assert.Equal(HostKeySource.UserConfirmed, entry.Source);
        Assert.True(entry.FirstSeen > DateTimeOffset.MinValue);
        Assert.True(entry.LastSeen > DateTimeOffset.MinValue);
    }

    // ── LoadServersAsync ───────────────────────────────────────────────

    [Fact]
    public async Task LoadServersAsync_ReturnsEmptyList_WhenFileMissing()
    {
        var servers = await _manager.LoadServersAsync();

        Assert.NotNull(servers);
        Assert.Empty(servers);
    }

    [Fact]
    public async Task MutateServersAsync_HoldsLockAcrossLoadMutateWrite()
    {
        await _manager.SaveServersAsync([]);
        using var firstMutationEntered = new ManualResetEventSlim();
        using var releaseFirstMutation = new ManualResetEventSlim();
        var secondMutationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> firstMutation = Task.Run(() => _manager.MutateServersAsync(servers =>
        {
            firstMutationEntered.Set();
            Assert.True(releaseFirstMutation.Wait(TimeSpan.FromSeconds(5)));
            servers.Add(new ServerProfileDto
            {
                Id = "first",
                DisplayName = "First",
                RemoteServer = "first.example.test"
            });
            return true;
        }));

        Assert.True(firstMutationEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<bool> secondMutation = _manager.MutateServersAsync(servers =>
        {
            secondMutationEntered.TrySetResult();
            Assert.Contains(servers, server => server.Id == "first");
            return true;
        });

        await Task.Delay(100);
        Assert.False(secondMutationEntered.Task.IsCompleted);

        releaseFirstMutation.Set();
        await Task.WhenAll(firstMutation, secondMutation);
        Assert.True(secondMutationEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task ConcurrentInventoryMutations_BothDeltasSurvive()
    {
        await _manager.SaveServersAsync([]);
        using var firstMutationEntered = new ManualResetEventSlim();
        using var releaseFirstMutation = new ManualResetEventSlim();

        Task<string> firstMutation = Task.Run(() => _manager.MutateServersAsync(servers =>
        {
            firstMutationEntered.Set();
            Assert.True(releaseFirstMutation.Wait(TimeSpan.FromSeconds(5)));
            servers.Add(new ServerProfileDto
            {
                Id = "alpha",
                DisplayName = "Alpha",
                RemoteServer = "alpha.example.test"
            });
            return "alpha";
        }));

        Assert.True(firstMutationEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<string> secondMutation = _manager.MutateServersAsync(servers =>
        {
            servers.Add(new ServerProfileDto
            {
                Id = "beta",
                DisplayName = "Beta",
                RemoteServer = "beta.example.test"
            });
            return "beta";
        });

        releaseFirstMutation.Set();
        await Task.WhenAll(firstMutation, secondMutation);

        string[] persistedIds = (await _manager.LoadServersAsync())
            .Select(server => server.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["alpha", "beta"], persistedIds);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Explicit Credential Reference", false)]
    public async Task BulkRename_WhenProfilesChangedInOneMutation_PreservesCredentialTargets(
        string? vaultEntryName,
        bool freezesOldDisplayName)
    {
        await _manager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "alpha",
                DisplayName = "Alpha",
                RemoteServer = "alpha.example.test",
                VaultEntryName = vaultEntryName
            },
            new ServerProfileDto
            {
                Id = "beta",
                DisplayName = "Beta",
                RemoteServer = "beta.example.test",
                VaultEntryName = vaultEntryName
            }
        ]);

        await _manager.MutateServersAsync(servers =>
        {
            foreach (ServerProfileDto server in servers)
            {
                server.DisplayName += " Renamed";
            }

            return servers.Count;
        });

        List<ServerProfileDto> persisted = await _manager.LoadServersAsync();
        Assert.Equal(
            freezesOldDisplayName ? "Alpha" : vaultEntryName,
            persisted.Single(server => server.Id == "alpha").VaultEntryName);
        Assert.Equal(
            freezesOldDisplayName ? "Beta" : vaultEntryName,
            persisted.Single(server => server.Id == "beta").VaultEntryName);
    }

    [Theory]
    [InlineData(null, "Original Display Name")]
    [InlineData("Explicit Credential Reference", "Explicit Credential Reference")]
    public async Task SaveServersAsync_ProgrammaticRename_PreservesCredentialTarget(
        string? vaultEntryName,
        string expectedCredentialTarget)
    {
        var original = new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Original Display Name",
            RemoteServer = "server.example.test",
            VaultEntryName = vaultEntryName
        };
        await _manager.SaveServersAsync([original]);

        var renamed = new ServerProfileDto
        {
            Id = original.Id,
            DisplayName = "Renamed Display Name",
            RemoteServer = original.RemoteServer,
            VaultEntryName = vaultEntryName
        };
        await _manager.SaveServersAsync([renamed]);

        ServerProfileDto persisted = Assert.Single(await _manager.LoadServersAsync());
        Assert.Equal("Renamed Display Name", persisted.DisplayName);
        Assert.Equal(expectedCredentialTarget, persisted.VaultEntryName);
    }

    [Fact]
    public async Task LoadServersAsync_DeserializesValidJson()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = """
        [
            {
                "id": "srv-1",
                "displayName": "Production",
                "remoteServer": "10.0.0.1",
                "remotePort": 3389,
                "connectionType": "RDP"
            },
            {
                "id": "srv-2",
                "displayName": "Dev SSH",
                "remoteServer": "10.0.0.2",
                "sshPort": 22,
                "connectionType": "SSH"
            }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, json, new UTF8Encoding(false));

        var servers = await _manager.LoadServersAsync();

        Assert.Equal(2, servers.Count);
        Assert.Equal("srv-1", servers[0].Id);
        Assert.Equal("Production", servers[0].DisplayName);
        Assert.Equal("RDP", servers[0].ConnectionType);
        Assert.Equal("srv-2", servers[1].Id);
        Assert.Equal("SSH", servers[1].ConnectionType);
    }

    [Fact]
    public async Task LoadServersAsync_MigratesLegacyFixedResolutionToFixedMode()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = """
        [
          {
            "id": "srv-fixed",
            "displayName": "Fixed",
            "remoteServer": "10.0.0.1",
            "rdpFixedResolutionWidth": 1920,
            "rdpFixedResolutionHeight": 1080
          }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, json, new UTF8Encoding(false));

        var servers = await _manager.LoadServersAsync();

        Assert.Single(servers);
        Assert.Equal(RdpResolutionMode.Fixed, servers[0].RdpResolutionMode);
        Assert.Equal(1920, servers[0].RdpFixedWidth);
        Assert.Equal(1080, servers[0].RdpFixedHeight);
    }

    [Fact]
    public async Task LoadServersAsync_ExplicitResolutionModeWinsOverLegacyFixedResolution()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = """
        [
          {
            "id": "srv-fit",
            "displayName": "Fit",
            "remoteServer": "10.0.0.1",
            "rdpResolutionMode": "FitWindow",
            "rdpFixedResolutionWidth": 1920,
            "rdpFixedResolutionHeight": 1080
          }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, json, new UTF8Encoding(false));

        var servers = await _manager.LoadServersAsync();

        Assert.Single(servers);
        Assert.Equal(RdpResolutionMode.FitWindow, servers[0].RdpResolutionMode);
    }

    [Fact]
    public async Task SaveServersAsync_RoundTripsLegacyMultimonAsResolutionMode()
    {
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = """
        [
          {
            "id": "srv-multimon",
            "displayName": "Legacy Multimon",
            "remoteServer": "10.0.0.1",
            "rdpMultiMonitor": true
          }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, json, new UTF8Encoding(false));

        var servers = await _manager.LoadServersAsync();
        await _manager.SaveServersAsync(servers);
        var reloaded = await _manager.LoadServersAsync();

        Assert.Single(reloaded);
        Assert.Equal(RdpResolutionMode.Multimon, reloaded[0].RdpResolutionMode);
        Assert.True(reloaded[0].RdpMultiMonitor);
    }

    [Fact]
    public async Task SaveServersAsync_WritesResolutionProfileAndBackfillsMultimonBool()
    {
        var servers = new List<ServerProfileDto>
        {
            new()
            {
                Id = "srv-multi",
                DisplayName = "Multi",
                RemoteServer = "10.0.0.1",
                RdpResolutionMode = RdpResolutionMode.Multimon,
                RdpFixedWidth = 2560,
                RdpFixedHeight = 1440,
                RdpInitialSmartSizing = false,
                RdpResizeEnableDelayMs = 3000
            }
        };

        await _manager.SaveServersAsync(servers);
        var json = await File.ReadAllTextAsync(_manager.ServersPath, new UTF8Encoding(false));
        var loaded = await _manager.LoadServersAsync();

        Assert.Contains("\"rdpResolutionMode\": \"Multimon\"", json);
        Assert.Contains("\"rdpFixedResolutionWidth\": 2560", json);
        Assert.Contains("\"rdpFixedResolutionHeight\": 1440", json);
        Assert.True(loaded[0].RdpMultiMonitor);
        Assert.False(loaded[0].RdpInitialSmartSizing);
        Assert.Equal(3000, loaded[0].RdpResizeEnableDelayMs);
    }

    [Fact]
    public async Task LoadServersAsync_LegacySshKeyPassword_DoesNotAutoMigrateToKeyPassphrase()
    {
        CredentialProtector.Initialize(null);
        var legacySecret = CredentialProtector.Protect("legacy-secret");
        var configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        var json = $$"""
        [
            {
                "id": "srv-legacy",
                "displayName": "Legacy SSH",
                "remoteServer": "10.0.0.2",
                "sshPort": 22,
                "connectionType": "SSH",
                "sshKeyPath": "C:\\keys\\legacy.pem",
                "sshPasswordEncrypted": "{{legacySecret}}"
            }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, json, new UTF8Encoding(false));

        var servers = await _manager.LoadServersAsync();
        var reloadedJson = await File.ReadAllTextAsync(_manager.ServersPath, new UTF8Encoding(false));

        var server = Assert.Single(servers);
        Assert.False(server.HasSshKeyPassphraseEncryptedField);
        Assert.True(server.UsesLegacySshCredentialMapping);
        Assert.Null(server.SshKeyPassphraseEncrypted);
        Assert.DoesNotContain("sshKeyPassphraseEncrypted", reloadedJson);
    }

    // ── SaveServersAsync / round-trip ──────────────────────────────────

    [Fact]
    public async Task SaveServersAsync_ThrowsOnNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _manager.SaveServersAsync(null!));
    }

    [Fact]
    public async Task SaveServersAsync_RoundTrips_WithLoadServersAsync()
    {
        var original = new List<ServerProfileDto>
        {
            new()
            {
                Id = "srv-1",
                DisplayName = "Web Server",
                RemoteServer = "10.0.0.1",
                RemotePort = 3389,
                ConnectionType = "RDP",
                RdpMode = "Embedded",
                Group = "Production",
                IsFavorite = true
            },
            new()
            {
                Id = "srv-2",
                DisplayName = "DB Server",
                RemoteServer = "10.0.0.2",
                SshPort = 2222,
                ConnectionType = "SSH",
                SshMode = "Embedded",
                SshAgentForwarding = true,
                SshUsername = "admin"
            }
        };

        await _manager.SaveServersAsync(original);
        var loaded = await _manager.LoadServersAsync();

        Assert.Equal(2, loaded.Count);

        Assert.Equal("srv-1", loaded[0].Id);
        Assert.Equal("Web Server", loaded[0].DisplayName);
        Assert.Equal("10.0.0.1", loaded[0].RemoteServer);
        Assert.Equal(3389, loaded[0].RemotePort);
        Assert.Equal("RDP", loaded[0].ConnectionType);
        Assert.Equal("Embedded", loaded[0].RdpMode);
        Assert.Equal("Production", loaded[0].Group);
        Assert.True(loaded[0].IsFavorite);

        Assert.Equal("srv-2", loaded[1].Id);
        Assert.Equal("DB Server", loaded[1].DisplayName);
        Assert.Equal(2222, loaded[1].SshPort);
        Assert.Equal("SSH", loaded[1].ConnectionType);
        Assert.Equal("Embedded", loaded[1].SshMode);
        Assert.True(loaded[1].SshAgentForwarding);
        Assert.Equal("admin", loaded[1].SshUsername);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveServersAsync_ThenLoadServersAsync_PreservesTunnelsPanelExpanded(bool? expanded)
    {
        var original = new List<ServerProfileDto>
        {
            new()
            {
                Id = "srv-tunnels",
                DisplayName = "Tunnels State",
                RemoteServer = "10.0.0.1",
                TunnelsPanelExpanded = expanded
            }
        };

        await _manager.SaveServersAsync(original);
        var loaded = await _manager.LoadServersAsync();

        var server = Assert.Single(loaded);
        Assert.Equal(expanded, server.TunnelsPanelExpanded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveServersAsync_ThenLoadServersAsync_PreservesSessionLoggingOverride(bool? sessionLoggingOverride)
    {
        var original = new List<ServerProfileDto>
        {
            new()
            {
                Id = "srv-logging",
                DisplayName = "Logging Override",
                RemoteServer = "10.0.0.1",
                SessionLoggingOverride = sessionLoggingOverride
            }
        };

        await _manager.SaveServersAsync(original);
        var loaded = await _manager.LoadServersAsync();

        var server = Assert.Single(loaded);
        Assert.Equal(sessionLoggingOverride, server.SessionLoggingOverride);
    }

    [Fact]
    public async Task LoadServersAsync_MissingSessionLoggingOverride_DefaultsNull()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        const string Json = """
        [
            {
                "id": "srv-logging-legacy",
                "displayName": "Legacy Logging",
                "remoteServer": "10.0.0.1",
                "connectionType": "SSH"
            }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, Json, new UTF8Encoding(false));

        List<ServerProfileDto> loaded = await _manager.LoadServersAsync();

        ServerProfileDto server = Assert.Single(loaded);
        Assert.Null(server.SessionLoggingOverride);
    }

    [Fact]
    public async Task SaveServersAsync_ThenLoadServersAsync_PreservesExecutionConfirmed()
    {
        List<ServerProfileDto> original = new()
        {
            new()
            {
                Id = "srv-exec-trusted",
                DisplayName = "Trusted Local Shell",
                RemoteServer = "localhost",
                ConnectionType = "LOCAL",
                LocalShellExecutable = "pwsh.exe",
                ExecutionConfirmed = true
            }
        };

        await _manager.SaveServersAsync(original);
        List<ServerProfileDto> loaded = await _manager.LoadServersAsync();

        ServerProfileDto server = Assert.Single(loaded);
        Assert.True(server.ExecutionConfirmed);
    }

    [Fact]
    public async Task LoadServersAsync_MissingExecutionConfirmed_DefaultsFalse()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);

        const string Json = """
        [
            {
                "id": "srv-exec-legacy",
                "displayName": "Legacy Local Shell",
                "remoteServer": "localhost",
                "connectionType": "LOCAL",
                "localShellExecutable": "pwsh.exe"
            }
        ]
        """;
        await File.WriteAllTextAsync(_manager.ServersPath, Json, new UTF8Encoding(false));

        List<ServerProfileDto> loaded = await _manager.LoadServersAsync();

        ServerProfileDto server = Assert.Single(loaded);
        Assert.False(server.ExecutionConfirmed);
    }

    [Fact]
    public async Task SaveServersAsync_RoundTrips_SshKeyPassphraseEncrypted()
    {
        CredentialProtector.Initialize(null);
        var protectedPassphrase = CredentialProtector.Protect("key-passphrase");
        var original = new List<ServerProfileDto>
        {
            new()
            {
                Id = "srv-key",
                DisplayName = "Key SSH",
                RemoteServer = "10.0.0.5",
                SshPort = 22,
                ConnectionType = "SSH",
                SshMode = "Embedded",
                SshUsername = "admin",
                SshKeyPath = @"C:\keys\id_rsa",
                SshPasswordEncrypted = CredentialProtector.Protect("login-password"),
                SshKeyPassphraseEncrypted = protectedPassphrase
            }
        };

        await _manager.SaveServersAsync(original);
        var loaded = await _manager.LoadServersAsync();

        var server = Assert.Single(loaded);
        Assert.True(server.HasSshKeyPassphraseEncryptedField);
        Assert.False(server.UsesLegacySshCredentialMapping);
        Assert.Equal("key-passphrase", CredentialProtector.Unprotect(server.SshKeyPassphraseEncrypted));
    }

    [Fact]
    public async Task SaveServersAsync_RoundTrips_EmptyList()
    {
        await _manager.SaveServersAsync(new List<ServerProfileDto>());
        var loaded = await _manager.LoadServersAsync();

        Assert.NotNull(loaded);
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task SaveServersAsync_WritesUtf8NoBom()
    {
        await _manager.SaveServersAsync(new List<ServerProfileDto>());

        var bytes = await File.ReadAllBytesAsync(_manager.ServersPath);

        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "File should not contain UTF-8 BOM");
    }

    [Fact]
    public async Task Settings_StaleLoad_DoesNotOverwriteNewerSave()
    {
        await _manager.SaveSettingsAsync(new AppSettings { DefaultTheme = "Old" });
        await _manager.SaveServersAsync([]);
        using var lockHolderEntered = new ManualResetEventSlim();
        using var releaseLockHolder = new ManualResetEventSlim();
        Task lockHolder = Task.Run(() => _manager.MutateServersAsync(servers =>
        {
            lockHolderEntered.Set();
            Assert.True(releaseLockHolder.Wait(TimeSpan.FromSeconds(5)));
            return servers.Count;
        }));
        Assert.True(lockHolderEntered.Wait(TimeSpan.FromSeconds(5)));

        var newerSettings = new AppSettings { DefaultTheme = "New" };
        Task newerSave = _manager.SaveSettingsAsync(newerSettings);
        Task<AppSettings> potentiallyStaleLoad = _manager.LoadSettingsAsync();

        await Task.Delay(100);
        Assert.False(newerSave.IsCompleted);
        Assert.False(potentiallyStaleLoad.IsCompleted);

        releaseLockHolder.Set();

        await Task.WhenAll(lockHolder, potentiallyStaleLoad, newerSave);

        AppSettings persisted = await _manager.LoadSettingsAsync();
        Assert.Equal("New", persisted.DefaultTheme);
        Assert.Equal("New", _manager.CurrentSettings?.DefaultTheme);
    }

    // ── File ACL enforcement ───────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_AppliesFileAcls()
    {
        await _manager.InitializeAsync();

        // Verify files exist and are accessible (ACL allows current user)
        Assert.True(File.Exists(_manager.SettingsPath));
        Assert.True(File.Exists(_manager.ServersPath));

        // Verify we can still read/write after ACL enforcement
        var settings = await _manager.LoadSettingsAsync();
        Assert.NotNull(settings);
    }

    [Fact]
    public async Task SaveSettingsAsync_ReappliesAcl_AfterWrite()
    {
        await _manager.InitializeAsync();

        // Save new settings (should reapply ACL)
        var settings = new AppSettings { DefaultLocale = "fr" };
        await _manager.SaveSettingsAsync(settings);

        // Verify file is still accessible
        var reloaded = await _manager.LoadSettingsAsync();
        Assert.Equal("fr", reloaded.DefaultLocale);
    }
}
