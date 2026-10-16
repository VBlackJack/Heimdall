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

using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public sealed class SharedSessionCoreTests
{
    private static readonly string Prefix = SharedSessionIds.PrefixFor("teamfile1");
    private const string Folder = "PROD";

    // --- Identifier namespace ---

    [Fact]
    public void PrefixFor_IncludesTheSourceSoTwoSourcesNeverCollide()
    {
        Assert.Equal("shared-teamfile1-", Prefix);
        Assert.NotEqual(Prefix, SharedSessionIds.PrefixFor("teamfile2"));
        Assert.True(SharedSessionIds.IsShared(Prefix + "x"));
        Assert.False(SharedSessionIds.IsShared("local-x"));
        Assert.False(SharedSessionIds.IsShared(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Team")]
    [InlineData("team-file")]
    [InlineData("team file")]
    public void PrefixFor_InvalidSourceId_Throws(string sourceId)
    {
        Assert.False(SharedSessionIds.IsValidSourceId(sourceId));
        Assert.Throws<ArgumentException>(() => SharedSessionIds.PrefixFor(sourceId));
    }

    [Fact]
    public void NewSourceId_IsValid()
    {
        Assert.True(SharedSessionIds.IsValidSourceId(SharedSessionIds.NewSourceId()));
    }

    [Fact]
    public void IsSharedProfile_SeesThroughASessionKey()
    {
        var profile = new ServerProfileDto { Id = Prefix + "profile" };
        profile.AdoptSessionIdentity("pane-key-1");

        Assert.True(SharedSessionIds.IsSharedProfile(profile));
    }

    // --- Folder defaults ---

    [Fact]
    public void ApplyInherited_SharedProfile_TakesNothingFromALocalFolder()
    {
        var shared = new ServerProfileDto { Id = Prefix + "profile", Group = Folder, ConnectionType = "SSH" };
        var local = new ServerProfileDto { Id = "local-profile", Group = Folder, ConnectionType = "SSH" };

        GroupDefaultsDto.ApplyInherited(shared, FolderDefaults());
        GroupDefaultsDto.ApplyInherited(local, FolderDefaults());

        Assert.Null(shared.SshUsername);
        Assert.Null(shared.SshKeyPath);
        Assert.Null(shared.SshGatewayId);

        // Control: the same folder fills a local profile, so the assertions above measure the
        // guard and not an empty set of defaults.
        Assert.Equal("local-folder-user", local.SshUsername);
        Assert.Equal("local-gateway", local.SshGatewayId);
    }

    [Fact]
    public void ApplyInherited_SharedProfileUnderASessionKey_TakesNothingEither()
    {
        var shared = new ServerProfileDto { Id = Prefix + "profile", Group = Folder, ConnectionType = "SSH" };
        shared.AdoptSessionIdentity("session-key-1");

        GroupDefaultsDto.ApplyInherited(shared, FolderDefaults());

        Assert.Null(shared.SshUsername);
    }

    [Fact]
    public void GatewayIdFor_SharedProfile_NeverInheritsALocalFolderGateway()
    {
        var shared = new ServerProfileDto { Id = Prefix + "profile", Group = Folder, ConnectionType = "SSH" };
        var local = new ServerProfileDto { Id = "local-profile", Group = Folder, ConnectionType = "SSH" };

        Assert.Null(SshGatewayRoute.GatewayIdFor(shared, FolderDefaults()));
        Assert.Equal("local-gateway", SshGatewayRoute.GatewayIdFor(local, FolderDefaults()));
    }

    // --- Effective settings ---

    [Fact]
    public void WithSharedGateways_IsACopy_AndTheCanonicalSettingsAreUntouched()
    {
        var canonical = new AppSettings();
        canonical.SshGateways.Add(new SshGatewayDto { Id = "local-gateway", Host = "local.example.test" });
        var shared = new SshGatewayDto { Id = Prefix + "gateway", Host = "gw1.example.test", User = "jdoe" };

        AppSettings effective = SharedSessionSettings.WithSharedGateways(canonical, [shared, shared]);

        Assert.NotSame(canonical, effective);
        Assert.Equal(new[] { "local-gateway", shared.Id }, effective.SshGateways.Select(gateway => gateway.Id));
        Assert.Equal("local-gateway", Assert.Single(canonical.SshGateways).Id);
        Assert.NotSame(shared, effective.SshGateways[1]);
    }

    [Fact]
    public void WithSharedGateways_IgnoresAGatewayOutsideTheSharedNamespace()
    {
        AppSettings effective = SharedSessionSettings.WithSharedGateways(
            new AppSettings(),
            [new SshGatewayDto { Id = "local-gateway", Host = "x.example.test" }]);

        Assert.Empty(effective.SshGateways);
    }

    // --- Post-connect fingerprint ---

    [Fact]
    public void Fingerprint_IgnoresTheStepIdentifier()
    {
        var first = new PostConnectStep { Id = "a", Input = "sudo su - www" };
        var second = new PostConnectStep { Id = "b", Input = "sudo su - www" };

        Assert.Equal(PostConnectFingerprint.Compute([first]), PostConnectFingerprint.Compute([second]));
    }

    [Fact]
    public void Fingerprint_ChangesWithWhatRuns()
    {
        string baseline = PostConnectFingerprint.Compute([new PostConnectStep { Input = "sudo su - www" }]);

        Assert.NotEqual(baseline, PostConnectFingerprint.Compute([new PostConnectStep { Input = "sudo su - root" }]));
        Assert.NotEqual(baseline, PostConnectFingerprint.Compute([new PostConnectStep { Input = "sudo su - www", Enabled = false }]));
        Assert.NotEqual(baseline, PostConnectFingerprint.Compute(
            [new PostConnectStep { Input = "sudo su - www" }, new PostConnectStep { Input = "id" }]));
        Assert.NotEqual(
            PostConnectFingerprint.Compute([new PostConnectStep { Input = "a" }, new PostConnectStep { Input = "b" }]),
            PostConnectFingerprint.Compute([new PostConnectStep { Input = "b" }, new PostConnectStep { Input = "a" }]));
    }

    // --- File decoding ---

    [Fact]
    public void Decode_WithoutBom_ReadsWindows1252()
    {
        Assert.Equal("caf\u00e9", MobaXtermFileDecoder.Decode([0x63, 0x61, 0x66, 0xE9]));
    }

    [Fact]
    public void Decode_WithUtf8Bom_ReadsUtf8()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("caf\u00e9")];

        Assert.EndsWith("caf\u00e9", MobaXtermFileDecoder.Decode(bytes), StringComparison.Ordinal);
        Assert.True(MobaXtermFileDecoder.HasUtf8Bom(bytes));
    }

    private static Dictionary<string, GroupDefaultsDto> FolderDefaults() => new()
    {
        [Folder] = new GroupDefaultsDto
        {
            SshGatewayId = "local-gateway",
            SshUsername = "local-folder-user",
            SshKeyPath = @"C:\keys\local.key",
        },
    };
}
