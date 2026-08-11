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
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;

namespace Heimdall.App.Tests;

public sealed class ProfileImportServiceTests
{
    [Fact]
    public async Task ImportFromPathAsync_Rdp_UsesRichPreviewAndImportsSelection()
    {
        using var fixture = new ProfileImportFixture();
        var path = await fixture.WriteTextAsync("Workstation.rdp", "full address:s:rdp.example.com");

        var result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.ImportedCount);
        Assert.Single(servers);
        Assert.Equal("Workstation", servers[0].DisplayName);
        Assert.NotNull(fixture.Dialog.LastRdpImportViewModel);
        Assert.Equal("Import .rdp files", fixture.Dialog.LastRdpImportViewModel.DialogTitle);
    }

    [Fact]
    public async Task ImportFromPathAsync_Json_UsesPreviewAndImportsSelection()
    {
        using var fixture = new ProfileImportFixture();
        var path = await fixture.WriteJsonAsync("servers.json",
        [
            new ServerProfileDto
            {
                Id = "json-import",
                DisplayName = "Json SSH",
                ConnectionType = "SSH",
                RemoteServer = "ssh.example.com",
                SshPort = 22
            }
        ]);

        var result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.ImportedCount);
        Assert.Single(servers);
        Assert.Equal("Json SSH", servers[0].DisplayName);
        Assert.NotNull(fixture.Dialog.LastRdpImportViewModel);
        Assert.Equal("Import profiles", fixture.Dialog.LastRdpImportViewModel.DialogTitle);
    }

    [Theory]
    [InlineData("rdp", "RDP")]
    [InlineData("ssh", "SSH")]
    [InlineData("sftp", "SFTP")]
    [InlineData("vnc", "VNC")]
    [InlineData("telnet", "Telnet")]
    [InlineData("ftp", "FTP")]
    [InlineData("citrix", "Citrix")]
    [InlineData("local", "Local")]
    [InlineData("winrm", "WINRM")]
    public async Task ImportFromPathAsync_Json_CanonicalizesConnectionTypeForDialog(
        string importedConnectionType,
        string expectedConnectionType)
    {
        using ProfileImportFixture fixture = new();
        string path = await fixture.WriteJsonAsync("servers.json",
        [
            new ServerProfileDto
            {
                Id = "json-import",
                DisplayName = "Imported",
                ConnectionType = importedConnectionType,
                RemoteServer = "host.example.com"
            }
        ]);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        Assert.True(result.HasChanges);
        ServerProfileDto stored = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(expectedConnectionType, stored.ConnectionType);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonV2Envelope_PersistsGatewayAndKeepsReference()
    {
        using var fixture = new ProfileImportFixture();
        ProfileConfigDocument document = new()
        {
            Servers =
            [
                new ServerProfileDto
                {
                    Id = "json-v2-import",
                    DisplayName = "Json V2 SSH",
                    ConnectionType = "SSH",
                    RemoteServer = "ssh.example.com",
                    SshPort = 22,
                    SshGatewayId = "gateway-v2"
                }
            ],
            Gateways =
            [
                new SshGatewayDto
                {
                    Id = "gateway-v2",
                    Name = "Bastion",
                    Host = "bastion.example.com",
                    Port = 22,
                    User = "ops"
                }
            ]
        };
        var path = await fixture.WriteTextAsync(
            "servers-v2.json",
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            }));

        var result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.ImportedCount);
        Assert.Equal(1, result.GatewayCreatedCount);
        Assert.Equal(0, result.GatewayMergedCount);
        Assert.Equal(0, result.GatewayOrphanCount);
        ServerProfileDto server = Assert.Single(servers);
        Assert.Equal("Json V2 SSH", server.DisplayName);
        Assert.Equal("gateway-v2", server.SshGatewayId);
        SshGatewayDto gateway = Assert.Single(settings.SshGateways);
        Assert.Equal("gateway-v2", gateway.Id);
        Assert.Equal("bastion.example.com", gateway.Host);
        Assert.Contains("SSH gateways: 1 created, 0 merged, 0 orphan reference(s).", Assert.Single(fixture.Dialog.InfoCalls).Message, StringComparison.Ordinal);
        Assert.Contains("Gateway passwords and key passphrases are not included", result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonV2Envelope_ReusesExistingGatewayByIdentityAndRemapsServer()
    {
        using var fixture = new ProfileImportFixture();
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        settings.SshGateways.Add(new SshGatewayDto
        {
            Id = "existing-gateway",
            Name = "Existing Bastion",
            Host = "bastion.example.com",
            Port = 22,
            User = "ops",
            KeyPath = @"C:\existing\id_ed25519"
        });
        await fixture.ConfigManager.SaveSettingsAsync(settings);
        ProfileConfigDocument document = new()
        {
            Servers =
            [
                new ServerProfileDto
                {
                    Id = "json-v2-merge",
                    DisplayName = "Json V2 Merge",
                    ConnectionType = "SSH",
                    RemoteServer = "ssh.example.com",
                    SshPort = 22,
                    SshGatewayId = "imported-gateway"
                }
            ],
            Gateways =
            [
                new SshGatewayDto
                {
                    Id = "imported-gateway",
                    Name = "Imported Bastion",
                    Host = "BASTION.example.com",
                    Port = 22,
                    User = "OPS",
                    KeyPath = @"C:\imported\id_ed25519"
                }
            ]
        };
        string path = await fixture.WriteConfigDocumentAsync("servers-v2-merge.json", document);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        AppSettings reloadedSettings = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(0, result.GatewayCreatedCount);
        Assert.Equal(1, result.GatewayMergedCount);
        Assert.Equal(0, result.GatewayOrphanCount);
        Assert.Equal("existing-gateway", Assert.Single(servers).SshGatewayId);
        SshGatewayDto gateway = Assert.Single(reloadedSettings.SshGateways);
        Assert.Equal("existing-gateway", gateway.Id);
        Assert.Equal(@"C:\existing\id_ed25519", gateway.KeyPath);
        Assert.Contains("SSH gateways: 0 created, 1 merged, 0 orphan reference(s).", result.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonLegacyGatewayReference_WarnsAboutOrphan()
    {
        using var fixture = new ProfileImportFixture();
        string path = await fixture.WriteJsonAsync("servers-v1-orphan.json",
        [
            new ServerProfileDto
            {
                Id = "json-v1-orphan",
                DisplayName = "Json V1 Orphan",
                ConnectionType = "SSH",
                RemoteServer = "ssh.example.com",
                SshPort = 22,
                SshGatewayId = "missing-gateway"
            }
        ]);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.True(result.HasChanges);
        Assert.Equal(1, result.GatewayOrphanCount);
        Assert.Equal("missing-gateway", Assert.Single(servers).SshGatewayId);
        string warning = Assert.Single(fixture.Dialog.WarningCalls).Message;
        Assert.Contains("1 orphan reference", warning, StringComparison.Ordinal);
        Assert.Contains("recreate/reassign", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadJsonConfigDocument_V1Array_ReturnsSchemaOneServers()
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new ServerProfileDto
            {
                Id = "v1-server",
                DisplayName = "V1 Server",
                RemoteServer = "v1.example.com"
            }
        });

        ProfileConfigDocument document = ProfileImportService.ReadJsonConfigDocument(json);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Empty(document.Gateways);
        Assert.Equal("v1-server", Assert.Single(document.Servers).Id);
    }

    [Fact]
    public void ReadJsonConfigDocument_V2Envelope_ReturnsGatewaysWithoutSecrets()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "servers": [
                {
                  "id": "v2-server",
                  "displayName": "V2 Server",
                  "remoteServer": "v2.example.com",
                  "sshGatewayId": "v2-gateway"
                }
              ],
              "gateways": [
                {
                  "id": "v2-gateway",
                  "name": "V2 Gateway",
                  "host": "bastion.example.com",
                  "port": 22,
                  "user": "ops",
                  "sshPasswordEncrypted": "secret",
                  "sshKeyPassphraseEncrypted": "key-secret"
                }
              ]
            }
            """;

        ProfileConfigDocument document = ProfileImportService.ReadJsonConfigDocument(json);

        Assert.Equal(ProfileConfigDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal("v2-server", Assert.Single(document.Servers).Id);
        SshGatewayDto gateway = Assert.Single(document.Gateways);
        Assert.Equal("v2-gateway", gateway.Id);
        Assert.Null(gateway.SshPasswordEncrypted);
        Assert.Null(gateway.SshKeyPassphraseEncrypted);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonOversized_ReturnsFailureBeforePreviewOrPersistence()
    {
        using ProfileImportFixture fixture = new(maxImportFileSizeBytes: 10);
        string path = await fixture.WriteTextAsync(
            "servers.json",
            "[{\"displayName\":\"Oversized\",\"remoteServer\":\"host.example\"}]");

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.True(result.IsFailure);
        Assert.Contains("Import file is too large", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("10 B", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(servers);
        Assert.Null(fixture.Dialog.LastRdpImportViewModel);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonOutOfRangePort_ShowsParseErrorAndDoesNotPersist()
    {
        using ProfileImportFixture fixture = new();
        string path = await fixture.WriteJsonAsync("servers.json",
        [
            new ServerProfileDto
            {
                Id = "bad-port",
                DisplayName = "Bad Port",
                ConnectionType = "SSH",
                RemoteServer = "ssh.example.com",
                SshPort = 70000
            }
        ]);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        RdpImportRowViewModel row = Assert.Single(fixture.Dialog.LastRdpImportViewModel!.Rows);
        Assert.False(result.HasChanges);
        Assert.Empty(servers);
        Assert.True(row.HasParseError);
        Assert.False(row.IsSelected);
        Assert.Contains(nameof(ServerProfileDto.SshPort), row.ParseErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonUnsupportedConnectionType_ShowsParseErrorAndDoesNotPersist()
    {
        using ProfileImportFixture fixture = new();
        string path = await fixture.WriteJsonAsync("servers.json",
        [
            new ServerProfileDto
            {
                Id = "bad-type",
                DisplayName = "Bad Type",
                ConnectionType = "garbage",
                RemoteServer = "host.example.com"
            }
        ]);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        RdpImportRowViewModel row = Assert.Single(fixture.Dialog.LastRdpImportViewModel!.Rows);
        Assert.False(result.HasChanges);
        Assert.Empty(servers);
        Assert.True(row.HasParseError);
        Assert.False(row.IsSelected);
        Assert.Contains(nameof(ServerProfileDto.ConnectionType), row.ParseErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportFromPathAsync_JsonParseErrorSelected_DoesNotPersist()
    {
        using ProfileImportFixture fixture = new();
        fixture.Dialog.SelectParseErrorRows = true;
        string path = await fixture.WriteJsonAsync("servers.json",
        [
            new ServerProfileDto
            {
                Id = "hostile-selection",
                DisplayName = "Hostile Selection",
                ConnectionType = "SSH",
                RemoteServer = "ssh.example.com",
                SshPort = -1
            }
        ]);

        ProfileImportResult result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        List<ServerProfileDto> servers = await fixture.ConfigManager.LoadServersAsync();
        RdpImportRowViewModel row = Assert.Single(fixture.Dialog.LastRdpImportViewModel!.Rows);
        Assert.False(result.HasChanges);
        Assert.Equal(1, result.SkippedCount);
        Assert.Empty(servers);
        Assert.True(row.HasParseError);
    }

    [Fact]
    public async Task ImportFromPathAsync_UnknownExtension_ReturnsFailure()
    {
        using var fixture = new ProfileImportFixture();
        var path = await fixture.WriteTextAsync("servers.txt", "ignored");

        var result = await fixture.Service.ImportFromPathAsync(path, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Contains(".txt", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(fixture.Dialog.LastRdpImportViewModel);
    }

    [Fact]
    public void SupportedConnectionTypes_MatchesProtocolHandlerKeys()
    {
        string[] expected =
        [
            "RDP",
            "SSH",
            "SFTP",
            "VNC",
            "TELNET",
            "FTP",
            "CITRIX",
            "LOCAL",
            "WINRM"
        ];

        Assert.Equal(expected.Length, ProfileImportService.SupportedConnectionTypes.Count);
        foreach (string connectionType in expected)
        {
            Assert.True(ProfileImportService.SupportedConnectionTypes.Contains(connectionType), connectionType);
        }
    }

    private sealed class ProfileImportFixture : IDisposable
    {
        public string RootPath { get; } = Path.Combine(Path.GetTempPath(), "heimdall-profile-import-tests", Guid.NewGuid().ToString("N"));

        public ConfigManager ConfigManager { get; }

        public TestDialogService Dialog { get; } = new();

        public ProfileImportService Service { get; }

        public ProfileImportFixture(long maxImportFileSizeBytes = AppConstants.MaxImportFileSizeBytes)
        {
            Directory.CreateDirectory(RootPath);
            ConfigManager = new ConfigManager(RootPath);
            LocalizationManager localizer = CreateLocalizerAsync().GetAwaiter().GetResult();
            RdpImportService rdpImport = new(ConfigManager, localizer);
            Service = new ProfileImportService(ConfigManager, localizer, Dialog, rdpImport, maxImportFileSizeBytes);
        }

        public async Task<string> WriteTextAsync(string fileName, string content)
        {
            var path = Path.Combine(RootPath, fileName);
            await File.WriteAllTextAsync(path, content);
            return path;
        }

        public async Task<string> WriteJsonAsync(string fileName, IReadOnlyList<ServerProfileDto> servers)
        {
            var path = Path.Combine(RootPath, fileName);
            var json = JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(path, json);
            return path;
        }

        public Task<string> WriteConfigDocumentAsync(string fileName, ProfileConfigDocument document)
        {
            return WriteTextAsync(
                fileName,
                JsonSerializer.Serialize(document, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                }));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static async Task<LocalizationManager> CreateLocalizerAsync()
        {
            var manager = new LocalizationManager();
            await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
            return manager;
        }
    }

    private sealed class TestDialogService : IDialogService
    {
        public RdpImportDialogViewModel? LastRdpImportViewModel { get; private set; }

        public bool SelectParseErrorRows { get; set; }

        public List<(string Title, string Message)> InfoCalls { get; } = [];

        public List<(string Title, string Message)> WarningCalls { get; } = [];

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
            => Task.FromResult(true);

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
            => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
        {
            LastRdpImportViewModel = viewModel;
            return Task.FromResult<RdpImportSelection?>(new RdpImportSelection
            {
                Entries =
                [
                    .. viewModel.Rows.Select(row => new RdpImportSelectionEntry
                    {
                        SourceFilePath = row.SourceFilePath,
                        IsSelected = SelectParseErrorRows || !row.HasParseError,
                        ConflictResolution = row.HasNameConflict
                            ? RdpConflictResolution.AutoRename
                            : RdpConflictResolution.Skip
                    })
                ]
            });
        }

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
            InfoCalls.Add((title, message));
        }

        public void ShowWarning(string title, string message)
        {
            WarningCalls.Add((title, message));
        }
    }
}
