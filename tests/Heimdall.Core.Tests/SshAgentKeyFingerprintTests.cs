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
using System.Text.Json.Serialization;
using Heimdall.Core.Configuration;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Tests;

public sealed class SshAgentKeyFingerprintTests
{
    private const string SampleFingerprint = "SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s";
    private const string SharedProfileIdSuffix = "profile";

    // The persistence options of ConfigManager: camelCase names, nulls left out.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void SshAgentKeyFingerprint_DefaultsToNull_AndIsNotWrittenWhenNull()
    {
        ServerProfileDto profile = new() { DisplayName = "Plain" };

        string json = JsonSerializer.Serialize(profile, WriteOptions);

        Assert.Null(profile.SshAgentKeyFingerprint);
        Assert.DoesNotContain("sshAgentKeyFingerprint", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        ServerProfileDto profile = new() { DisplayName = "Pinned", SshAgentKeyFingerprint = SampleFingerprint };

        string json = JsonSerializer.Serialize(profile, WriteOptions);
        ServerProfileDto? restored = JsonSerializer.Deserialize<ServerProfileDto>(json, ReadOptions);

        Assert.NotNull(restored);
        Assert.Equal(SampleFingerprint, restored.SshAgentKeyFingerprint);
    }

    [Fact]
    public void ImportSanitizer_ClearsIt()
    {
        List<ServerProfileDto> profiles =
            [new() { DisplayName = "Imported", SshAgentKeyFingerprint = SampleFingerprint }];

        _ = ImportedProfileSanitizer.Sanitize(profiles);

        Assert.Null(profiles[0].SshAgentKeyFingerprint);
    }

    [Fact]
    public void SharedSanitizer_DropsIt()
    {
        ServerProfileDto source = new()
        {
            Id = SharedSessionIds.PrefixFor("teamfile1") + SharedProfileIdSuffix,
            ConnectionType = "SSH",
            RemoteServer = "srv-x.example.test",
            SshAgentKeyFingerprint = SampleFingerprint,
        };

        ServerProfileDto? shared = SharedProfileSanitizer.Sanitize(source);

        Assert.NotNull(shared);
        Assert.Null(shared.SshAgentKeyFingerprint);
    }

    [Fact]
    public void CloneFaithfully_KeepsIt()
    {
        ServerProfileDto profile = new() { DisplayName = "Pinned", SshAgentKeyFingerprint = SampleFingerprint };

        Assert.Equal(SampleFingerprint, profile.CloneFaithfully().SshAgentKeyFingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ABlankValue_MeansAnyKey_WhereverItCameFrom(string blank)
    {
        ServerProfileDto assigned = new() { SshAgentKeyFingerprint = blank };
        ServerProfileDto read = JsonSerializer.Deserialize<ServerProfileDto>(
            $"{{\"sshAgentKeyFingerprint\":\"{blank.Replace("\t", "\\t")}\"}}",
            ReadOptions)!;

        Assert.Null(assigned.SshAgentKeyFingerprint);
        Assert.Null(read.SshAgentKeyFingerprint);
    }

    [Fact]
    public void Compute_MatchesTheHostKeyFingerprintOfTheSameBlob()
    {
        byte[] blob = [1, 2, 3, 4, 5];

        Assert.Equal(HostKeyFormats.ComputeSha256Fingerprint(blob), SshAgentKeyFingerprints.Compute(blob));
        Assert.True(SshAgentKeyFingerprints.IsWellFormed(SshAgentKeyFingerprints.Compute(blob)));
    }

    [Theory]
    [InlineData("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s", true)]
    [InlineData("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s=", false)]
    [InlineData("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2", false)]
    [InlineData("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2sA", false)]
    [InlineData("MD5:00:11:22:33:44:55:66:77:88:99:aa:bb:cc:dd:ee:ff", false)]
    [InlineData("sha256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s", false)]
    [InlineData("uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(" SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s", false)]
    [InlineData("SHA256:uNiVztksCsDhcc0u9e8BujQXVUpKZIDTMczCvj3tD2s\n", false)]
    [InlineData(null, false)]
    public void IsWellFormed_AcceptsOpenSshForm_RejectsOthers(string? value, bool expected)
    {
        Assert.Equal(expected, SshAgentKeyFingerprints.IsWellFormed(value));
    }
}
