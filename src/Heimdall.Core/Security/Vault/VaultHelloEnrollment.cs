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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Persisted metadata for a Windows Hello-wrapped copy of the vault DEK.
/// </summary>
public sealed record VaultHelloEnrollment(
    string VaultId,
    string WrappedDek,
    string Challenge,
    string Salt,
    string CredentialName,
    string PublicKeyHash)
{
    /// <summary>
    /// Builds the AAD binding inputs, rejecting malformed Base64 without
    /// revealing any wrapped material.
    /// </summary>
    public VaultHelloBinding ToBinding()
    {
        if (string.IsNullOrWhiteSpace(VaultId) ||
            string.IsNullOrWhiteSpace(WrappedDek) ||
            string.IsNullOrWhiteSpace(Challenge) ||
            string.IsNullOrWhiteSpace(Salt) ||
            string.IsNullOrWhiteSpace(CredentialName) ||
            string.IsNullOrWhiteSpace(PublicKeyHash))
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }

        try
        {
            var challenge = Convert.FromBase64String(Challenge);
            var salt = Convert.FromBase64String(Salt);
            return new VaultHelloBinding(VaultId, PublicKeyHash, challenge, salt);
        }
        catch (FormatException)
        {
            throw new VaultHelloException(VaultHelloFailureReason.CryptoFailure);
        }
    }
}

/// <summary>
/// Non-secret fields bound into the AES-GCM AAD for a Hello DEK wrapper.
/// </summary>
public sealed record VaultHelloBinding(
    string VaultId,
    string PublicKeyHash,
    byte[] Challenge,
    byte[] Salt);
