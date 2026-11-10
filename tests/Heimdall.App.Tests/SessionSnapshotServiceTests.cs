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
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class SessionSnapshotServiceTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsSessionsInOrder()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();

        await service.SaveAsync(
        [
            new SessionSnapshotEntry { ServerId = secondId, ConnectionType = "RDP", Order = 1 },
            new SessionSnapshotEntry { ServerId = firstId, ConnectionType = "SSH", Order = 0 },
        ]);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(SessionSnapshotFile.CurrentSchemaVersion, snapshot!.SchemaVersion);
        Assert.Equal(2, snapshot.Sessions.Count);
        Assert.Equal(firstId, snapshot.Sessions[0].ServerId);
        Assert.Equal(secondId, snapshot.Sessions[1].ServerId);
    }

    /// <summary>
    /// A shared profile's identifier is not a GUID, and its tab must come back at the next launch.
    /// </summary>
    [Fact]
    public async Task SaveAndLoad_KeepsTheSessionOfASharedProfile()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        string sharedId = SharedSessionIds.PrefixFor("a1b2c3") + "9f8e7d";
        var localId = Guid.NewGuid().ToString();

        await service.SaveAsync(
        [
            new SessionSnapshotEntry { ServerId = localId, ConnectionType = "RDP", Order = 0 },
            new SessionSnapshotEntry { ServerId = sharedId, ConnectionType = "SSH", Order = 1 },
        ]);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Equal([localId, sharedId], snapshot!.Sessions.Select(entry => entry.ServerId));
    }

    [Theory]
    [InlineData("shared-")]
    [InlineData("shared--hash")]
    [InlineData("shared-UPPER-hash")]
    [InlineData("not-shared-a1b2c3-9f8e7d")]
    [InlineData("garbage")]
    public async Task Load_MalformedIdentifier_IsStillDropped(string malformedId)
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.ConfigManager.ConfigPath);
        var validId = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(
            service.SnapshotPath,
            $$"""
            {
              "schemaVersion": 1,
              "savedAtUtc": "2026-04-19T12:34:56.0000000Z",
              "sessions": [
                { "serverId": "{{malformedId}}", "connectionType": "SSH", "order": 0 },
                { "serverId": "{{validId}}", "connectionType": "RDP", "order": 1 }
              ]
            }
            """);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(validId, Assert.Single(snapshot!.Sessions).ServerId);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();

        var snapshot = await service.LoadAsync();

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Load_CorruptedFile_ReturnsNull()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.ConfigManager.ConfigPath);
        await File.WriteAllTextAsync(service.SnapshotPath, "{ definitely-not-json");

        var snapshot = await service.LoadAsync();

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task Load_PreservesUnknownFields()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.ConfigManager.ConfigPath);
        var serverId = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(
            service.SnapshotPath,
            $$"""
            {
              "schemaVersion": 2,
              "savedAtUtc": "2026-04-19T12:34:56.0000000Z",
              "futureSection": { "enabled": true },
              "sessions": [
                {
                  "serverId": "{{serverId}}",
                  "connectionType": "SSH",
                  "order": 0,
                  "futureField": "kept"
                }
              ]
            }
            """);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.SchemaVersion);
        Assert.NotNull(snapshot.ExtraProperties);
        Assert.True(snapshot.ExtraProperties!.ContainsKey("futureSection"));
        Assert.NotNull(snapshot.Sessions[0].ExtraProperties);
        Assert.True(snapshot.Sessions[0].ExtraProperties!.ContainsKey("futureField"));
    }

    [Fact]
    public async Task Load_FutureSchema_StillLoadsValidSessions()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.ConfigManager.ConfigPath);
        var serverId = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(
            service.SnapshotPath,
            $$"""
            {
              "schemaVersion": 99,
              "savedAtUtc": "2026-04-19T12:34:56.0000000Z",
              "sessions": [
                { "serverId": "{{serverId}}", "connectionType": "SFTP", "order": 0 }
              ]
            }
            """);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Sessions);
        Assert.Equal("SFTP", snapshot.Sessions[0].ConnectionType);
    }

    [Fact]
    public async Task Load_InvalidEntry_SkipsCorruptedItem()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        Directory.CreateDirectory(fixture.ConfigManager.ConfigPath);
        var validId = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(
            service.SnapshotPath,
            $$"""
            {
              "schemaVersion": 1,
              "savedAtUtc": "2026-04-19T12:34:56.0000000Z",
              "sessions": [
                { "serverId": "garbage", "connectionType": "SSH", "order": 0 },
                { "serverId": "{{validId}}", "connectionType": "RDP", "order": 1 }
              ]
            }
            """);

        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Sessions);
        Assert.Equal(validId, snapshot.Sessions[0].ServerId);
    }

    [Fact]
    public async Task Save_LeavesNoTemporaryFilesBehind()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();

        await service.SaveAsync(
        [
            new SessionSnapshotEntry
            {
                ServerId = Guid.NewGuid().ToString(),
                ConnectionType = "SSH",
                Order = 0
            }
        ]);

        var tempFiles = Directory
            .GetFiles(fixture.ConfigManager.ConfigPath, "*.tmp", SearchOption.TopDirectoryOnly);

        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Clear_IsIdempotent()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();
        await service.SaveAsync(
        [
            new SessionSnapshotEntry
            {
                ServerId = Guid.NewGuid().ToString(),
                ConnectionType = "SSH",
                Order = 0
            }
        ]);

        await service.ClearAsync();
        await service.ClearAsync();

        Assert.False(File.Exists(service.SnapshotPath));
    }

    [Fact]
    public async Task Save_EmptyList_PersistsEmptySnapshot()
    {
        using var fixture = new SnapshotServiceFixture();
        var service = fixture.CreateService();

        await service.SaveAsync([]);
        var snapshot = await service.LoadAsync();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.Sessions);
    }

    private sealed class SnapshotServiceFixture : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "heimdall-b55-tests", Guid.NewGuid().ToString("N"));

        public ConfigManager ConfigManager { get; }

        public SnapshotServiceFixture()
        {
            ConfigManager = new ConfigManager(_rootPath);
        }

        public SessionSnapshotService CreateService() => new(ConfigManager);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
