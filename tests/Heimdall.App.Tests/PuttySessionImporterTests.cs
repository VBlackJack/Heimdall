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

namespace Heimdall.App.Tests;

public sealed class PuttySessionImporterTests
{
    [Fact]
    public async Task ReadAndParseAsync_UsesRegistrySourceSnapshot()
    {
        using var fixture = new PuttyImportFixture(
        [
            new RawPuttySession("Prod%20SSH", new Dictionary<string, object?>
            {
                ["Protocol"] = "ssh",
                ["HostName"] = "prod.example.com"
            })
        ]);

        var result = await fixture.Importer.ReadAndParseAsync();

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Prod SSH", candidate.DisplayName);
    }

    [Fact]
    public async Task ComputeStatuses_MarksDuplicateAcrossSourcesByDisplayName()
    {
        using var fixture = new PuttyImportFixture([]);
        await fixture.ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = Guid.NewGuid().ToString(),
                DisplayName = "Prod SSH",
                ConnectionType = "SSH",
                RemoteServer = "existing.example.com"
            }
        ]);

        var assessments = await fixture.Importer.ComputeStatusesAsync(
        [
            new PuttySessionCandidate
            {
                DisplayName = "prod ssh",
                HostName = "new.example.com"
            }
        ]);

        var assessment = Assert.Single(assessments);
        Assert.Equal(ImportCandidateStatus.Duplicate, assessment.Status);
    }

    [Fact]
    public async Task ComputeStatuses_MarksMissingHostAsInvalid()
    {
        using var fixture = new PuttyImportFixture([]);

        var assessments = await fixture.Importer.ComputeStatusesAsync(
        [
            new PuttySessionCandidate
            {
                DisplayName = "Broken",
                HostName = null
            }
        ]);

        var assessment = Assert.Single(assessments);
        Assert.Equal(ImportCandidateStatus.Invalid, assessment.Status);
    }

    [Fact]
    public async Task ImportSelectedAsync_SkipsInvalidAndDuplicates_ServerSide()
    {
        using var fixture = new PuttyImportFixture([]);
        await fixture.ConfigManager.SaveServersAsync(
        [
            new ServerProfileDto
            {
                Id = "existing",
                DisplayName = "Prod SSH",
                ConnectionType = "SSH",
                RemoteServer = "existing.example.com"
            }
        ]);

        var outcome = await fixture.Importer.ImportSelectedAsync(
        [
            new PuttySessionCandidate
            {
                DisplayName = "Prod SSH",
                HostName = "duplicate.example.com"
            },
            new PuttySessionCandidate
            {
                DisplayName = "Broken",
                HostName = null
            },
            new PuttySessionCandidate
            {
                DisplayName = "Fresh",
                HostName = "fresh.example.com",
                Port = 2022,
                UserName = "alice",
                PublicKeyFile = @"C:\keys\admin.ppk"
            }
        ]);

        var servers = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal(2, servers.Count);
        var imported = Assert.Single(servers, server => server.DisplayName == "Fresh");
        Assert.Equal("SSH", imported.ConnectionType);
        Assert.Equal("Embedded", imported.SshMode);
        Assert.Equal(2022, imported.SshPort);
        Assert.Equal("alice", imported.SshUsername);
        Assert.Equal(@"C:\keys\admin.ppk", imported.SshKeyPath);
        Assert.Equal(1, outcome.ImportedCount);
        Assert.Equal(1, outcome.SkippedDuplicates);
        Assert.Equal(1, outcome.SkippedInvalid);
    }

    // S-02, entry point family "PuTTY sessions": a key file on a share is dropped at the import
    // boundary, counted as a warning, and named with its value so the user can re-enter it.
    [Fact]
    public async Task ImportSelectedAsync_ShareKeyFile_IsDroppedAndReported()
    {
        using var fixture = new PuttyImportFixture([]);

        var outcome = await fixture.Importer.ImportSelectedAsync(
        [
            new PuttySessionCandidate
            {
                DisplayName = "Roaming",
                HostName = "ssh.example.com",
                PublicKeyFile = @"\\fs\home$\u\key.ppk"
            }
        ]);

        ServerProfileDto imported = Assert.Single(await fixture.ConfigManager.LoadServersAsync());
        Assert.Null(imported.SshKeyPath);
        Assert.Equal(1, outcome.WarningCount);
        ImportedItemSanitization item = Assert.Single(outcome.Sanitization.Items);
        Assert.Equal("Roaming", item.Name);
        Assert.Equal(@"\\fs\home$\u\key.ppk", Assert.Single(item.ClearedPaths).Value);
    }

    private sealed class PuttyImportFixture : IDisposable
    {
        public PuttyImportFixture(IReadOnlyList<RawPuttySession> sessions)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "heimdall-b61-tests", Guid.NewGuid().ToString("N"));
            ConfigManager = new ConfigManager(RootPath);
            ConfigManager.InitializeAsync().GetAwaiter().GetResult();
            Importer = new PuttySessionImporter(new FakePuttySessionRegistrySource(sessions), ConfigManager);
        }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public PuttySessionImporter Importer { get; }

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
