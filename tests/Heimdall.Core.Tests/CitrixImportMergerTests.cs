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

using System.Text.Json;
using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class CitrixImportMergerTests
{
    [Fact]
    public void Merge_WhenExistingIsEmpty_AppendsAllAndCountsAdded()
    {
        List<ServerProfileDto> existing = [];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile("store-01@@AppOne", "App One", "token-1"),
            BuildCitrixProfile("store-01@@AppTwo", "App Two", "token-2")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 2, Updated: 0, Unchanged: 0), result);
        Assert.Equal(2, existing.Count);
        Assert.Equal("store-01@@AppOne", existing[0].CitrixId);
        Assert.Equal("store-01@@AppTwo", existing[1].CitrixId);
    }

    [Fact]
    public void Merge_WhenMatchedCitrixMetadataChanges_UpdatesInPlaceAndPreservesUserFields()
    {
        Dictionary<string, JsonElement> extensionData = new()
        {
            ["customNote"] = JsonSerializer.SerializeToElement("user-preserved-note")
        };

        ServerProfileDto existingProfile = new()
        {
            Id = "stable-profile-id",
            DisplayName = "Custom User Name",
            Group = "Custom Group",
            ConnectionType = "Citrix",
            CitrixId = "store-01@@App",
            CitrixAppName = "Old App Name",
            CitrixStoreFrontUrl = "https://old-store.example.test",
            RemoteServer = "https://old-store.example.test",
            CitrixLaunchCommandLine = "vault:v1:old-nonce:old-cipher",
            IsFavorite = true,
            SortOrder = 42,
            Tags = "prod,citrix",
            ExtensionData = extensionData
        };

        List<ServerProfileDto> existing = [existingProfile];
        List<ServerProfileDto> incoming =
        [
            new ServerProfileDto
            {
                Id = "new-random-id",
                DisplayName = "Scanned Name",
                Group = "Citrix/Scanned",
                ConnectionType = "Citrix",
                CitrixId = "store-01@@App",
                CitrixAppName = "New App Name",
                CitrixStoreFrontUrl = "https://store.example.test",
                RemoteServer = "https://store.example.test",
                CitrixLaunchCommandLine = "vault:v1:new-nonce:new-cipher"
            }
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 0, Updated: 1, Unchanged: 0), result);
        ServerProfileDto merged = Assert.Single(existing);
        Assert.Same(existingProfile, merged);
        Assert.Equal("stable-profile-id", merged.Id);
        Assert.Equal("Custom User Name", merged.DisplayName);
        Assert.Equal("Custom Group", merged.Group);
        Assert.True(merged.IsFavorite);
        Assert.Equal(42, merged.SortOrder);
        Assert.Equal("prod,citrix", merged.Tags);
        Assert.NotNull(merged.ExtensionData);
        Assert.Equal("user-preserved-note", merged.ExtensionData["customNote"].GetString());

        Assert.Equal("vault:v1:new-nonce:new-cipher", merged.CitrixLaunchCommandLine);
        Assert.Equal("https://store.example.test", merged.CitrixStoreFrontUrl);
        Assert.Equal("New App Name", merged.CitrixAppName);
        Assert.Equal("https://store.example.test", merged.RemoteServer);
    }

    [Fact]
    public void Merge_WhenMatchedCitrixMetadataIsIdentical_RefreshesTokenAndCountsUnchanged()
    {
        ServerProfileDto existingProfile = new()
        {
            Id = "stable-profile-id",
            DisplayName = "Custom User Name",
            Group = "Custom Group",
            ConnectionType = "citrix",
            CitrixId = "store-01@@App",
            CitrixAppName = "App",
            CitrixStoreFrontUrl = "https://store.example.test",
            RemoteServer = "https://store.example.test",
            CitrixLaunchCommandLine = "vault:v1:nonce-a:cipher-a"
        };

        List<ServerProfileDto> existing = [existingProfile];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile(
                citrixId: "store-01@@App",
                appName: "App",
                launchCommandLine: "vault:v1:nonce-b:cipher-b",
                storeFrontUrl: "https://store.example.test")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 0, Updated: 0, Unchanged: 1), result);
        ServerProfileDto merged = Assert.Single(existing);
        Assert.Equal("stable-profile-id", merged.Id);
        Assert.Equal("Custom User Name", merged.DisplayName);
        Assert.Equal("Custom Group", merged.Group);
        Assert.Equal("vault:v1:nonce-b:cipher-b", merged.CitrixLaunchCommandLine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Merge_WhenIncomingHasNoCitrixId_AppendsToExisting(string? incomingCitrixId)
    {
        List<ServerProfileDto> existing =
        [
            BuildCitrixProfile("store-01@@App", "App", "token-existing")
        ];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile(incomingCitrixId, "Legacy App", "token-legacy")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 1, Updated: 0, Unchanged: 0), result);
        Assert.Equal(2, existing.Count);
        Assert.Equal("Legacy App", existing[1].DisplayName);
    }

    [Fact]
    public void Merge_WhenIncomingContainsDuplicateCitrixId_KeepsFirstAndIgnoresDuplicates()
    {
        List<ServerProfileDto> existing = [];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile("store-01@@App", "App First", "token-first"),
            BuildCitrixProfile("store-01@@App", "App Duplicate 1", "token-dup-1"),
            BuildCitrixProfile("store-01@@App", "App Duplicate 2", "token-dup-2")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 1, Updated: 0, Unchanged: 0), result);
        ServerProfileDto single = Assert.Single(existing);
        Assert.Equal("App First", single.DisplayName);
        Assert.Equal("token-first", single.CitrixLaunchCommandLine);
    }

    [Fact]
    public void Merge_WhenExistingNonCitrixProfileHasSameCitrixId_LeavesExistingUntouchedAndAppendsIncoming()
    {
        ServerProfileDto sshProfile = new()
        {
            Id = "ssh-id",
            DisplayName = "SSH Server",
            ConnectionType = "SSH",
            CitrixId = "store-01@@App",
            RemoteServer = "ssh.example.test",
            CitrixLaunchCommandLine = null
        };

        List<ServerProfileDto> existing = [sshProfile];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile("store-01@@App", "App", "token-citrix")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 1, Updated: 0, Unchanged: 0), result);
        Assert.Equal(2, existing.Count);
        Assert.Equal("SSH", existing[0].ConnectionType);
        Assert.Equal("ssh.example.test", existing[0].RemoteServer);
        Assert.Null(existing[0].CitrixLaunchCommandLine);
        Assert.Equal("Citrix", existing[1].ConnectionType);
        Assert.Equal("token-citrix", existing[1].CitrixLaunchCommandLine);
    }

    [Fact]
    public void Merge_WhenExistingCitrixProfileHasNoCitrixId_LeavesExistingUntouchedAndAppendsIncoming()
    {
        ServerProfileDto legacyCitrixProfile = new()
        {
            Id = "legacy-citrix-id",
            DisplayName = "App",
            ConnectionType = "Citrix",
            CitrixId = null,
            CitrixAppName = "App",
            CitrixStoreFrontUrl = "https://store.example.test",
            RemoteServer = "https://store.example.test",
            CitrixLaunchCommandLine = "token-legacy"
        };

        List<ServerProfileDto> existing = [legacyCitrixProfile];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile("store-01@@App", "App", "token-new")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 1, Updated: 0, Unchanged: 0), result);
        Assert.Equal(2, existing.Count);
        Assert.Equal("legacy-citrix-id", existing[0].Id);
        Assert.Null(existing[0].CitrixId);
        Assert.Equal("token-legacy", existing[0].CitrixLaunchCommandLine);
        Assert.Equal("store-01@@App", existing[1].CitrixId);
        Assert.Equal("token-new", existing[1].CitrixLaunchCommandLine);
    }

    [Fact]
    public void Merge_WhenExistingCitrixProfileIsNotInIncoming_PreservesUnmatchedProfile()
    {
        ServerProfileDto unmatchedCitrix = BuildCitrixProfile("store-01@@OtherApp", "Other App", "token-other");
        List<ServerProfileDto> existing = [unmatchedCitrix];
        List<ServerProfileDto> incoming =
        [
            BuildCitrixProfile("store-01@@App", "App", "token-app")
        ];

        CitrixImportMergeResult result = CitrixImportMerger.Merge(existing, incoming);

        Assert.Equal(new CitrixImportMergeResult(Added: 1, Updated: 0, Unchanged: 0), result);
        Assert.Equal(2, existing.Count);
        Assert.Same(unmatchedCitrix, existing[0]);
        Assert.Equal("store-01@@App", existing[1].CitrixId);
    }

    private static ServerProfileDto BuildCitrixProfile(
        string? citrixId,
        string appName,
        string launchCommandLine,
        string storeFrontUrl = "https://store.example.test") =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = appName,
            ConnectionType = "Citrix",
            CitrixId = citrixId,
            CitrixAppName = appName,
            CitrixStoreFrontUrl = storeFrontUrl,
            RemoteServer = storeFrontUrl,
            CitrixLaunchCommandLine = launchCommandLine,
            CitrixUseSso = true,
            UseDirectConnection = true
        };
}
