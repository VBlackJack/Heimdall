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

public sealed class CredentialLookupConfirmedTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void NewProfile_ReadsAsConfirmed()
    {
        Assert.True(new ServerProfileDto().CredentialLookupConfirmed);
    }

    [Fact]
    public void ProfileDeserializedWithoutTheField_ReadsAsConfirmed()
    {
        ServerProfileDto? profile = JsonSerializer.Deserialize<ServerProfileDto>(
            """{ "displayName": "Old", "remoteServer": "old.example.com" }""", Options);

        Assert.NotNull(profile);
        Assert.True(profile.CredentialLookupConfirmed);
    }

    [Fact]
    public void FalseIsWrittenToDisk_AndSurvivesARoundTrip()
    {
        ServerProfileDto profile = new() { DisplayName = "Imported", CredentialLookupConfirmed = false };

        string json = JsonSerializer.Serialize(profile, Options);
        ServerProfileDto? restored = JsonSerializer.Deserialize<ServerProfileDto>(json, Options);

        Assert.Contains("\"CredentialLookupConfirmed\":false", json, StringComparison.Ordinal);
        Assert.NotNull(restored);
        Assert.False(restored.CredentialLookupConfirmed);
    }

    [Fact]
    public void CloneFaithfully_KeepsTheFlag()
    {
        ServerProfileDto profile = new() { DisplayName = "Imported", CredentialLookupConfirmed = false };

        Assert.False(profile.CloneFaithfully().CredentialLookupConfirmed);
    }

    [Fact]
    public void Sanitize_ClearsTheFlag()
    {
        List<ServerProfileDto> profiles = [new() { DisplayName = "Imported", CredentialLookupConfirmed = true }];

        _ = ImportedProfileSanitizer.Sanitize(profiles);

        Assert.False(profiles[0].CredentialLookupConfirmed);
    }

    [Fact]
    public void Sanitize_ClearsTheFlagEvenWhenTheFileSaysTrue()
    {
        ServerProfileDto? fromFile = JsonSerializer.Deserialize<ServerProfileDto>(
            """{ "displayName": "Crafted", "remoteServer": "evil.example.com", "CredentialLookupConfirmed": true }""",
            Options);
        Assert.NotNull(fromFile);
        Assert.True(fromFile.CredentialLookupConfirmed);

        _ = ImportedProfileSanitizer.Sanitize([fromFile]);

        Assert.False(fromFile.CredentialLookupConfirmed);
    }

    [Fact]
    public void Sanitize_ClearingTheFlagAloneReportsNothing()
    {
        List<ServerProfileDto> profiles = [new() { DisplayName = "Imported", ConnectionType = "SSH" }];

        Assert.False(ImportedProfileSanitizer.Sanitize(profiles).HasChanges);
    }

    [Theory]
    [InlineData(null, "Box", "Box")]
    [InlineData("", "Box", "Box")]
    [InlineData("vault-entry", "Box", "vault-entry")]
    public void NameBasedTitle_PrefersTheVaultEntryName(string? entry, string displayName, string expected)
    {
        ServerProfileDto profile = new() { DisplayName = displayName, VaultEntryName = entry };

        Assert.Equal(expected, CredentialLookupPolicy.NameBasedTitle(profile));
    }

    [Fact]
    public void NameBasedTitleDiffersFromHost_IsFalseWhenTheNameIsTheHost()
    {
        ServerProfileDto profile = new() { DisplayName = "box.example.com", RemoteServer = "box.example.com" };

        Assert.False(CredentialLookupPolicy.NameBasedTitleDiffersFromHost(profile));
        profile.DisplayName = "Box";
        Assert.True(CredentialLookupPolicy.NameBasedTitleDiffersFromHost(profile));
    }
}
