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
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// The single fidelity primitive for copying a profile.
/// </summary>
/// <remarks>
/// <para>Two hand-written assignment lists existed before it and had drifted in opposite
/// directions. What makes that drift dangerous is not the missing values but the four presence
/// flags: their setters raise them on any assignment, including of null, so a copy written as a
/// list of assignments fabricates presence the source never had, and
/// <see cref="ServerProfileDto.UsesLegacySshCredentialMapping"/> flips with it.</para>
/// <para>These oracles therefore assert flag PRESERVATION, not value equality. A value-equality
/// oracle passes on a clone that fabricates every flag, which is exactly the bug.</para>
/// <para>They also assert that a copied dictionary keeps its comparer. A copy that imposes one
/// keeps every key while changing how the copy is searched, so the loss is invisible to any oracle
/// that only compares contents.</para>
/// </remarks>
public sealed class ServerProfileDtoCloneTests
{
    // All sixteen combinations of the four presence flags, false states included - a flag that is
    // false is the state a setter-based copy destroys. The fourth, the RDP resolution mode, is the
    // one whose setter fires even for the default enum value, so a copy that assigns it always
    // claims a mode the source may never have declared.
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void EveryCombinationOfThePresenceFlags_SurvivesTheClone(
        bool winRmPort,
        bool sshPort,
        bool passphrase,
        bool rdpResolutionMode)
    {
        ServerProfileDto source = new() { Id = "s", DisplayName = "S" };

        if (winRmPort)
        {
            source.WinRmPort = 5986;
        }

        if (sshPort)
        {
            source.SshPort = 2222;
        }

        if (passphrase)
        {
            source.SshKeyPassphraseEncrypted = "cipher";
        }

        if (rdpResolutionMode)
        {
            source.RdpResolutionMode = RdpResolutionMode.Fixed;
        }

        // The source is only a valid fixture for this oracle if the flags really are in the state
        // the row asks for: an assignment that raised one by accident would make the row vacuous.
        Assert.Equal(winRmPort, source.HasWinRmPortField);
        Assert.Equal(sshPort, source.HasSshPortField);
        Assert.Equal(passphrase, source.HasSshKeyPassphraseEncryptedField);
        Assert.Equal(rdpResolutionMode, source.HasRdpResolutionModeField);

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.Equal(source.HasWinRmPortField, clone.HasWinRmPortField);
        Assert.Equal(source.HasSshPortField, clone.HasSshPortField);
        Assert.Equal(
            source.HasSshKeyPassphraseEncryptedField,
            clone.HasSshKeyPassphraseEncryptedField);
        Assert.Equal(source.HasRdpResolutionModeField, clone.HasRdpResolutionModeField);

        // Asserted against the row as well as against the source, so a clone that fabricates a flag
        // cannot pass by having corrupted both sides identically.
        Assert.Equal(rdpResolutionMode, clone.HasRdpResolutionModeField);

        // The values travel with the flags, without either being invented.
        Assert.Equal(source.WinRmPort, clone.WinRmPort);
        Assert.Equal(source.SshPort, clone.SshPort);
        Assert.Equal(source.SshKeyPassphraseEncrypted, clone.SshKeyPassphraseEncrypted);
        Assert.Equal(source.RdpResolutionMode, clone.RdpResolutionMode);
    }

    // The reason the flags matter. A legacy profile - key plus password, no passphrase field - reads
    // its password as the key passphrase. A clone that fabricates the flag stops doing so and fails
    // to authenticate against a key that works in the source.
    [Fact]
    public void ALegacyCredentialProfile_KeepsItsLegacyMappingThroughTheClone()
    {
        ServerProfileDto source = new()
        {
            Id = "s",
            DisplayName = "S",
            SshKeyPath = @"C:\keys\id.ppk",
            SshPasswordEncrypted = "cipher",
        };

        Assert.False(source.HasSshKeyPassphraseEncryptedField);
        Assert.True(source.UsesLegacySshCredentialMapping);

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.False(clone.HasSshKeyPassphraseEncryptedField);
        Assert.True(clone.UsesLegacySshCredentialMapping);
    }

    [Fact]
    public void TheFieldsTheOldListsHadDropped_AreCarried()
    {
        ServerProfileDto source = new()
        {
            Id = "s",
            DisplayName = "S",
            SessionLoggingOverride = true,
            VaultEntryName = "vault-entry",
            WinRmUsername = "winrm-user",
            WinRmPasswordEncrypted = "winrm-cipher",
            WinRmUseSsl = true,
            WinRmSkipCertificateCheck = true,
            WinRmIdentityMode = WinRmIdentityMode.Credential,
            WinRmPort = 5986,
            SshKeyPassphraseEncrypted = "passphrase-cipher",
        };

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.Equal(true, clone.SessionLoggingOverride);
        Assert.Equal("vault-entry", clone.VaultEntryName);
        Assert.Equal("winrm-user", clone.WinRmUsername);
        Assert.Equal("winrm-cipher", clone.WinRmPasswordEncrypted);
        Assert.True(clone.WinRmUseSsl);
        Assert.True(clone.WinRmSkipCertificateCheck);
        Assert.Equal(WinRmIdentityMode.Credential, clone.WinRmIdentityMode);
        Assert.Equal(5986, clone.WinRmPort);
        Assert.Equal("passphrase-cipher", clone.SshKeyPassphraseEncrypted);
    }

    [Fact]
    public void WritingToTheClonesMonitorArray_LeavesTheSourceAlone()
    {
        ServerProfileDto source = new()
        {
            Id = "s",
            DisplayName = "S",
            RdpSelectedMonitorIndices = [0, 2],
        };

        ServerProfileDto clone = source.CloneFaithfully();
        Assert.Equal(source.RdpSelectedMonitorIndices, clone.RdpSelectedMonitorIndices);

        clone.RdpSelectedMonitorIndices[0] = 7;

        Assert.Equal(0, source.RdpSelectedMonitorIndices[0]);
    }

    [Fact]
    public void WritingToTheClonesPostConnectSteps_LeavesTheSourceAlone()
    {
        ServerProfileDto source = new() { Id = "s", DisplayName = "S" };
        source.PostConnectSteps.Add(new PostConnectStep
        {
            Id = "step-1",
            Input = "uptime",
            DelayMs = 42,
            CommandLibraryParams = new Dictionary<string, string> { ["host"] = "alpha" },
        });

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.Single(clone.PostConnectSteps);
        Assert.Equal("uptime", clone.PostConnectSteps[0].Input);
        Assert.Equal(42, clone.PostConnectSteps[0].DelayMs);
        Assert.Equal("alpha", clone.PostConnectSteps[0].CommandLibraryParams!["host"]);

        // The list, the step and the step's own dictionary must all be distinct: a collection
        // expression copies the list while sharing every element, which is the shape that was there.
        clone.PostConnectSteps.Add(new PostConnectStep { Input = "extra" });
        clone.PostConnectSteps[0].Input = "rebooted";
        clone.PostConnectSteps[0].CommandLibraryParams!["host"] = "beta";

        Assert.Single(source.PostConnectSteps);
        Assert.Equal("uptime", source.PostConnectSteps[0].Input);
        Assert.Equal("alpha", source.PostConnectSteps[0].CommandLibraryParams!["host"]);
    }

    [Fact]
    public void WritingToTheClonesExtensionData_LeavesTheSourceAlone()
    {
        ServerProfileDto source = JsonSerializer.Deserialize<ServerProfileDto>(
            """{"id":"s","displayName":"S","unknownFutureField":{"nested":1}}""")!;

        Assert.True(source.ExtensionData.ContainsKey("unknownFutureField"));

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.True(clone.ExtensionData.ContainsKey("unknownFutureField"));
        Assert.Equal(
            source.ExtensionData["unknownFutureField"].GetRawText(),
            clone.ExtensionData["unknownFutureField"].GetRawText());

        clone.ExtensionData.Remove("unknownFutureField");
        clone.ExtensionData["added"] = JsonSerializer.SerializeToElement(1);

        Assert.True(source.ExtensionData.ContainsKey("unknownFutureField"));
        Assert.False(source.ExtensionData.ContainsKey("added"));
    }

    // A dictionary is not only its contents: the comparer decides what "the same key" means. A copy
    // that imposes one keeps every entry and still answers differently, which no content comparison
    // can see.
    [Fact]
    public void ACaseInsensitiveDictionary_KeepsItsLookupSemanticsInTheClone()
    {
        ServerProfileDto source = new()
        {
            Id = "s",
            DisplayName = "S",
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["unknownFutureField"] = JsonSerializer.SerializeToElement(1),
            },
        };
        source.PostConnectSteps.Add(new PostConnectStep
        {
            Id = "step-1",
            Input = "uptime",
            CommandLibraryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["host"] = "alpha",
            },
        });

        Assert.True(source.ExtensionData.ContainsKey("UNKNOWNFUTUREFIELD"));
        Assert.True(source.PostConnectSteps[0].CommandLibraryParams!.ContainsKey("HOST"));

        ServerProfileDto clone = source.CloneFaithfully();

        Assert.Same(source.ExtensionData.Comparer, clone.ExtensionData.Comparer);
        Assert.Same(
            source.PostConnectSteps[0].CommandLibraryParams!.Comparer,
            clone.PostConnectSteps[0].CommandLibraryParams!.Comparer);

        // Asserted as behaviour too, not only as comparer identity: this is what the caller loses.
        Assert.True(clone.ExtensionData.ContainsKey("UNKNOWNFUTUREFIELD"));
        Assert.Equal("alpha", clone.PostConnectSteps[0].CommandLibraryParams!["HOST"]);
    }

    // Preserving the comparer must not cost the isolation: the copy is still a separate dictionary,
    // including when a key differs from the source's only by case.
    [Fact]
    public void WritingToACaseInsensitiveCloneDictionary_LeavesTheSourceAlone()
    {
        ServerProfileDto source = new()
        {
            Id = "s",
            DisplayName = "S",
            ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["unknownFutureField"] = JsonSerializer.SerializeToElement(1),
            },
        };
        source.PostConnectSteps.Add(new PostConnectStep
        {
            Id = "step-1",
            CommandLibraryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["host"] = "alpha",
            },
        });

        ServerProfileDto clone = source.CloneFaithfully();

        clone.ExtensionData["UNKNOWNFUTUREFIELD"] = JsonSerializer.SerializeToElement(2);
        clone.ExtensionData["ADDED"] = JsonSerializer.SerializeToElement(3);
        clone.PostConnectSteps[0].CommandLibraryParams!["HOST"] = "beta";

        Assert.Equal("1", source.ExtensionData["unknownFutureField"].GetRawText());
        Assert.False(source.ExtensionData.ContainsKey("added"));
        Assert.Equal("alpha", source.PostConnectSteps[0].CommandLibraryParams!["host"]);
    }

    [Fact]
    public void TheCloneIsADistinctInstance()
    {
        ServerProfileDto source = new() { Id = "s", DisplayName = "S" };

        Assert.NotSame(source, source.CloneFaithfully());
    }
}
