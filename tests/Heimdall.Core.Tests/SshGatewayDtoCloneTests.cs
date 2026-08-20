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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

/// <summary>
/// Copying a gateway must not invent a field the original never declared.
/// </summary>
/// <remarks>
/// <para><see cref="SshGatewayDto.SshKeyPassphraseEncrypted"/> raises a presence flag from its
/// setter, on every assignment including a null one. So an object initializer that copies the
/// property field by field raises the flag even for a gateway that never carried it - and the flag
/// is what <see cref="SshGatewayDto.UsesLegacySshCredentialMapping"/> is derived from.</para>
/// <para>That derivation is read on three gateway connect paths, where it decides whether the
/// stored password is offered as the key passphrase. A copy that flips it silently changes how the
/// gateway authenticates.</para>
/// </remarks>
public sealed class SshGatewayDtoCloneTests
{
    [Fact]
    public void ALegacyGateway_KeepsItsLegacyMappingThroughTheClone()
    {
        SshGatewayDto source = LegacyGateway();
        Assert.True(source.UsesLegacySshCredentialMapping);

        SshGatewayDto clone = source.CloneFaithfully();

        Assert.True(
            clone.UsesLegacySshCredentialMapping,
            "The copy stopped using the legacy mapping, so it authenticates differently from its source.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePresenceFlagSurvivesTheCloneInBothStates(bool declared)
    {
        SshGatewayDto source = LegacyGateway();
        if (declared)
        {
            source.SshKeyPassphraseEncrypted = null;
        }

        Assert.Equal(declared, source.HasSshKeyPassphraseEncryptedField);

        SshGatewayDto clone = source.CloneFaithfully();

        Assert.Equal(declared, clone.HasSshKeyPassphraseEncryptedField);
        Assert.Equal(source.UsesLegacySshCredentialMapping, clone.UsesLegacySshCredentialMapping);
    }

    [Fact]
    public void EveryValueIsCarriedOver()
    {
        // Guards the guard: a clone that returned a blank gateway would satisfy the flag tests
        // above for the undeclared case while losing everything that identifies the gateway.
        SshGatewayDto source = new()
        {
            Id = "gw-1",
            Name = "Bastion",
            Host = "bastion.example.com",
            Port = 2222,
            User = "operator",
            KeyPath = @"C:\keys\bastion.ppk",
            SshPasswordEncrypted = "cipher",
            IsDefault = true,
            ParentGatewayId = "gw-0",
            HostKeyFingerprint = "SHA256:abc",
        };

        SshGatewayDto clone = source.CloneFaithfully();

        Assert.Equal(source.Id, clone.Id);
        Assert.Equal(source.Name, clone.Name);
        Assert.Equal(source.Host, clone.Host);
        Assert.Equal(source.Port, clone.Port);
        Assert.Equal(source.User, clone.User);
        Assert.Equal(source.KeyPath, clone.KeyPath);
        Assert.Equal(source.SshPasswordEncrypted, clone.SshPasswordEncrypted);
        Assert.Equal(source.IsDefault, clone.IsDefault);
        Assert.Equal(source.ParentGatewayId, clone.ParentGatewayId);
        Assert.Equal(source.HostKeyFingerprint, clone.HostKeyFingerprint);
    }

    [Fact]
    public void TheCloneIsADistinctObject()
    {
        SshGatewayDto source = LegacyGateway();

        SshGatewayDto clone = source.CloneFaithfully();
        clone.Name = "Renamed";

        Assert.NotSame(source, clone);
        Assert.NotEqual(clone.Name, source.Name);
    }

    [Fact]
    public void ACopyWithoutSecretsDropsThemWithoutFabricatingPresence()
    {
        SshGatewayDto source = LegacyGateway();

        SshGatewayDto stripped = source.CloneWithoutSecrets();

        Assert.Null(stripped.SshPasswordEncrypted);
        Assert.Null(stripped.SshKeyPassphraseEncrypted);

        // Removing a secret says nothing about whether the source declared the field, so the flag
        // is carried over rather than raised by the act of clearing.
        Assert.False(stripped.HasSshKeyPassphraseEncryptedField);

        // Everything that is not a secret is still there.
        Assert.Equal(source.Id, stripped.Id);
        Assert.Equal(source.Host, stripped.Host);
        Assert.Equal(source.KeyPath, stripped.KeyPath);
        Assert.Equal(source.HostKeyFingerprint, stripped.HostKeyFingerprint);
    }

    [Fact]
    public void ACopyWithoutSecretsLeavesTheSourceAlone()
    {
        SshGatewayDto source = LegacyGateway();

        SshGatewayDto stripped = source.CloneWithoutSecrets();
        stripped.Host = "elsewhere.example.com";

        Assert.Equal("cipher", source.SshPasswordEncrypted);
        Assert.Equal("bastion.example.com", source.Host);
        Assert.True(source.UsesLegacySshCredentialMapping);
    }

    private static SshGatewayDto LegacyGateway() => new()
    {
        Id = "gw-1",
        Name = "Bastion",
        Host = "bastion.example.com",
        Port = 22,
        User = "operator",
        KeyPath = @"C:\keys\bastion.ppk",
        SshPasswordEncrypted = "cipher",
        HostKeyFingerprint = "SHA256:abc",
    };
}
