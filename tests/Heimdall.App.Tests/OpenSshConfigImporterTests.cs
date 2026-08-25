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
using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class OpenSshConfigImporterTests
{
    [Fact]
    public async Task ComputeStatuses_AllNew_WhenNoExistingMatch()
    {
        using var fixture = new OpenSshImportFixture();
        var assessments = await fixture.Importer.ComputeStatusesAsync(
        [
            new OpenSshImportCandidate { Alias = "prod", HostName = "prod.example.com", SourceLineNumber = 1 },
            new OpenSshImportCandidate { Alias = "logs", HostName = "logs.example.com", SourceLineNumber = 2 }
        ]);

        Assert.All(assessments, assessment => Assert.Equal(ImportCandidateStatus.New, assessment.Status));
    }

    [Fact]
    public async Task ComputeStatuses_DuplicateDetectedByAliasCaseInsensitive()
    {
        using var fixture = new OpenSshImportFixture();
        await fixture.ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = "PROD1",
                ConnectionType = "SSH",
                RemoteServer = "existing.example.com"
            }
        ]);

        var assessments = await fixture.Importer.ComputeStatusesAsync(
        [
            new OpenSshImportCandidate { Alias = "prod1", HostName = "new.example.com", SourceLineNumber = 1 }
        ]);

        var assessment = Assert.Single(assessments);
        Assert.Equal(ImportCandidateStatus.Duplicate, assessment.Status);
    }

    [Fact]
    public async Task ImportSelectedAsync_AddsNewProfiles_AndSkipsDuplicates_ReturnsOutcome()
    {
        using var fixture = new OpenSshImportFixture();
        await fixture.ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "existing",
                DisplayName = "prod1",
                ConnectionType = "SSH",
                RemoteServer = "existing.example.com"
            }
        ]);

        var missingKeyPath = Path.Combine(fixture.RootPath, "keys", "missing");
        var outcome = await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod1",
                HostName = "duplicate.example.com",
                SourceLineNumber = 1
            },
            new OpenSshImportCandidate
            {
                Alias = "logs",
                HostName = "logs.example.com",
                Port = 2222,
                User = "alice",
                IdentityFile = missingKeyPath,
                SourceLineNumber = 2
            }
        ]);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal(2, servers.Count);
        var imported = Assert.Single(servers, server => server.DisplayName == "logs");
        Assert.Equal("SSH", imported.ConnectionType);
        Assert.Equal("Embedded", imported.SshMode);
        Assert.Equal(2222, imported.SshPort);
        Assert.Equal("alice", imported.SshUsername);
        Assert.Equal(missingKeyPath, imported.SshKeyPath);
        Assert.Equal(1, outcome.ImportedCount);
        Assert.Equal(1, outcome.SkippedDuplicates);
        Assert.Equal(1, outcome.WarningCount);
    }

    [Fact]
    public async Task ImportSelectedAsync_SingleHopProxyJump_CreatesGatewayAndLinksServer()
    {
        using var fixture = new OpenSshImportFixture();

        var outcome = await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain = [Hop("bastion.example.com", user: "ops", port: 2222)]
            }
        ]);

        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        var gateway = Assert.Single(settings.SshGateways);
        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(1, outcome.ImportedCount);
        Assert.Equal("bastion.example.com", gateway.Host);
        Assert.Equal("ops", gateway.User);
        Assert.Equal(2222, gateway.Port);
        Assert.Null(gateway.ParentGatewayId);
        Assert.Equal(gateway.Id, server.SshGatewayId);
    }

    // BL-0095. Same defect as the profile importer, different order: here the settings write
    // comes BEFORE the server mutation. The snapshot is still taken at the start and written
    // back whole, so anything persisted meanwhile is erased.
    [Fact]
    public async Task ImportSelectedAsync_KeepsAGatewayPersistedWhileTheImportWasWorking()
    {
        using var fixture = new OpenSshImportFixture();
        var injected = false;
        fixture.AfterSettingsLoaded = () =>
        {
            if (injected)
            {
                return;
            }

            injected = true;
            fixture.ConfigManager
                .MergeSettingAsync(settings => settings.SshGateways.Add(new SshGatewayDto
                {
                    Id = "created-during-the-import",
                    Name = "Created meanwhile",
                    Host = "meanwhile.example.test",
                    Port = 22,
                    User = "ops"
                }))
                .GetAwaiter()
                .GetResult();
        };

        var outcome = await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain = [Hop("bastion.example.com", user: "ops", port: 2222)]
            }
        ]);

        Assert.Equal(1, outcome.ImportedCount);
        Assert.True(injected, "the concurrent write never ran, so this test measured nothing");
        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        Assert.Contains(
            settings.SshGateways,
            gateway => string.Equals(gateway.Id, "created-during-the-import", StringComparison.Ordinal));
        Assert.Contains(
            settings.SshGateways,
            gateway => string.Equals(gateway.Host, "bastion.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportSelectedAsync_MultiHopProxyJump_CreatesParentLinks()
    {
        using var fixture = new OpenSshImportFixture();

        await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain =
                [
                    Hop("h1.example.com", user: "u1"),
                    Hop("h2.example.com", user: "u2", port: 2202),
                    Hop("h3.example.com")
                ]
            }
        ]);

        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal(3, settings.SshGateways.Count);
        var h1 = Assert.Single(settings.SshGateways, gateway => gateway.Host == "h1.example.com");
        var h2 = Assert.Single(settings.SshGateways, gateway => gateway.Host == "h2.example.com");
        var h3 = Assert.Single(settings.SshGateways, gateway => gateway.Host == "h3.example.com");
        Assert.Null(h1.ParentGatewayId);
        Assert.Equal(h1.Id, h2.ParentGatewayId);
        Assert.Equal(h2.Id, h3.ParentGatewayId);
        Assert.Equal(h3.Id, server.SshGatewayId);
    }

    [Fact]
    public async Task ImportSelectedAsync_ReusesSameGatewayWithinBatch()
    {
        using var fixture = new OpenSshImportFixture();

        await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod-a",
                HostName = "a.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain = [Hop("bastion.example.com", user: "ops")]
            },
            new OpenSshImportCandidate
            {
                Alias = "prod-b",
                HostName = "b.example.com",
                SourceLineNumber = 5,
                ProxyJumpChain = [Hop("bastion.example.com", user: "ops")]
            }
        ]);

        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        var servers = await fixture.ConfigManager.LoadServersAsync();
        var gateway = Assert.Single(settings.SshGateways);
        Assert.All(servers, server => Assert.Equal(gateway.Id, server.SshGatewayId));
    }

    [Fact]
    public async Task ImportSelectedAsync_ReusesExistingGatewayWithoutMutatingIt()
    {
        using var fixture = new OpenSshImportFixture();
        var existing = new SshGatewayDto
        {
            Id = "existing-gw",
            Name = "Existing Bastion",
            Host = "bastion.example.com",
            Port = 22,
            User = "ops",
            KeyPath = @"C:\existing\id_ed25519"
        };
        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        settings.SshGateways.Add(existing);
        await fixture.ConfigManager.SaveSettingsAsync(settings);

        var assessments = await fixture.Importer.ComputeStatusesAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain = [Hop("bastion.example.com", user: "ops", identityFile: @"C:\new\id_ed25519")]
            }
        ]);
        var step = Assert.Single(Assert.Single(assessments).GatewayPreviewSteps);

        await fixture.Importer.ImportSelectedAsync([assessments[0].Candidate]);

        var reloadedSettings = await fixture.ConfigManager.LoadSettingsAsync();
        var gateway = Assert.Single(reloadedSettings.SshGateways);
        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Equal("Existing Bastion", step.ReusedGatewayName);
        Assert.Equal("existing-gw", server.SshGatewayId);
        Assert.Equal(@"C:\existing\id_ed25519", gateway.KeyPath);
        Assert.Null(gateway.ParentGatewayId);
    }

    [Fact]
    public async Task ImportSelectedAsync_HopHostBlockOverrides_AreAppliedToGateway()
    {
        using var fixture = new OpenSshImportFixture();
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                HostName prod.example.com
                ProxyJump bastion
            Host bastion
                HostName bastion.internal
                User jumpuser
                Port 2200
                IdentityFile ~/.ssh/id_jump
            """);
        var prod = Assert.Single(result.Candidates, candidate => candidate.Alias == "prod");

        await fixture.Importer.ImportSelectedAsync([prod]);

        var gateway = Assert.Single((await fixture.ConfigManager.LoadSettingsAsync()).SshGateways);
        Assert.Equal("bastion.internal", gateway.Host);
        Assert.Equal("jumpuser", gateway.User);
        Assert.Equal(2200, gateway.Port);
        Assert.NotNull(gateway.KeyPath);
        Assert.Contains($"{Path.DirectorySeparatorChar}.ssh{Path.DirectorySeparatorChar}id_jump", gateway.KeyPath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportSelectedAsync_HopWithoutHostBlock_UsesProxyJumpOnly()
    {
        using var fixture = new OpenSshImportFixture();

        await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain = [Hop("raw-bastion", user: "ops")]
            }
        ]);

        var gateway = Assert.Single((await fixture.ConfigManager.LoadSettingsAsync()).SshGateways);
        Assert.Equal("raw-bastion", gateway.Host);
        Assert.Equal("ops", gateway.User);
        Assert.Null(gateway.KeyPath);
    }

    [Fact]
    public async Task ImportSelectedAsync_UnsupportedProxyJumpDiagnostics_DoNotCreateGateway()
    {
        using var fixture = new OpenSshImportFixture();
        var result = OpenSshConfigParser.Parse(
            """
            Host prod
                ProxyCommand ssh -W %h:%p bastion
            """);
        var candidate = Assert.Single(result.Candidates);

        await fixture.Importer.ImportSelectedAsync([candidate]);

        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Empty(settings.SshGateways);
        Assert.Null(server.SshGatewayId);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == OpenSshDiagnosticCode.ProxyCommandUnsupported);
    }

    [Fact]
    public async Task ImportSelectedAsync_ProducedChain_IsConsumableByGatewayChainResolver()
    {
        using var fixture = new OpenSshImportFixture();

        await fixture.Importer.ImportSelectedAsync(
        [
            new OpenSshImportCandidate
            {
                Alias = "prod",
                HostName = "prod.example.com",
                SourceLineNumber = 1,
                ProxyJumpChain =
                [
                    Hop("h1.example.com"),
                    Hop("h2.example.com"),
                    Hop("h3.example.com")
                ]
            }
        ]);

        var settings = await fixture.ConfigManager.LoadSettingsAsync();
        var server = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        var chain = GatewayChainResolver.ResolveChain(server.SshGatewayId!, settings.SshGateways, _ => null);
        Assert.Equal(["h1.example.com", "h2.example.com", "h3.example.com"], chain.Select(item => item.Host));
    }

    private static OpenSshProxyJumpHop Hop(
        string hostName,
        string? user = null,
        int port = 22,
        string? identityFile = null)
    {
        return new OpenSshProxyJumpHop
        {
            Host = hostName,
            HostName = hostName,
            Port = port,
            User = user,
            IdentityFile = identityFile,
            SourceLineNumber = 1
        };
    }

    private sealed class OpenSshImportFixture : IDisposable
    {
        public OpenSshImportFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "heimdall-b60-tests", Guid.NewGuid().ToString("N"));
            ConfigManager = new ConfigManager(RootPath);
            ConfigManager.InitializeAsync().GetAwaiter().GetResult();
            InterceptingConfigManager intercepted = new(ConfigManager)
            {
                AfterLoadSettings = () => AfterSettingsLoaded?.Invoke()
            };
            Importer = new OpenSshConfigImporter(intercepted);
        }

        /// <summary>
        /// Runs when the importer takes its settings snapshot, so a test can persist through
        /// another surface exactly while the importer holds one.
        /// </summary>
        public Action? AfterSettingsLoaded { get; set; }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public OpenSshConfigImporter Importer { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
