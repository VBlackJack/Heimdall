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

using System.Text.Json.Serialization;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Flat DTO for SSH gateway JSON deserialization.
/// The ViewModel layer converts these to ObservableObject models.
/// </summary>
public sealed class SshGatewayDto
{
    private string? _sshKeyPassphraseEncrypted;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string User { get; set; } = string.Empty;
    public string? KeyPath { get; set; }
    public string? SshPasswordEncrypted { get; set; }
    public string? SshKeyPassphraseEncrypted
    {
        get => _sshKeyPassphraseEncrypted;
        set
        {
            _sshKeyPassphraseEncrypted = value;
            HasSshKeyPassphraseEncryptedField = true;
        }
    }

    [JsonIgnore]
    public bool HasSshKeyPassphraseEncryptedField { get; private set; }

    [JsonIgnore]
    public bool UsesLegacySshCredentialMapping =>
        !HasSshKeyPassphraseEncryptedField
        && !string.IsNullOrWhiteSpace(KeyPath)
        && !string.IsNullOrWhiteSpace(SshPasswordEncrypted);

    public bool IsDefault { get; set; }
    public string? ParentGatewayId { get; set; }
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// Returns a complete copy of this gateway, including whether it declared a passphrase field.
    /// </summary>
    /// <remarks>
    /// <para>No property setter is used while copying. <see cref="SshKeyPassphraseEncrypted"/>
    /// raises its presence flag from the setter on every assignment, a null one included, so an
    /// object initializer that copies the property field by field raises the flag on a copy of a
    /// gateway that never declared it. The flag is what
    /// <see cref="UsesLegacySshCredentialMapping"/> is derived from, and that derivation decides on
    /// three connect paths whether the stored password is offered as the key passphrase - so such a
    /// copy authenticates differently from the gateway it was copied from.</para>
    /// <para>Every member is a string, an int or a bool, so the shallow copy is complete. A member
    /// that is not, added later, has to be copied here explicitly.</para>
    /// </remarks>
    public SshGatewayDto CloneFaithfully() => (SshGatewayDto)MemberwiseClone();

    /// <summary>
    /// Returns a complete copy with the stored secrets removed.
    /// </summary>
    /// <remarks>
    /// Clearing the passphrase goes through the backing field rather than the property, because
    /// removing a secret says nothing about whether the source declared the field. Assigning null
    /// through the setter would raise the flag, which is the same fabrication this primitive
    /// exists to avoid.
    /// </remarks>
    public SshGatewayDto CloneWithoutSecrets()
    {
        SshGatewayDto copy = CloneFaithfully();
        copy.SshPasswordEncrypted = null;
        copy._sshKeyPassphraseEncrypted = null;
        return copy;
    }
}
