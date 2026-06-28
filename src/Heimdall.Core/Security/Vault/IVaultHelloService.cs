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
/// Headless service for enrolling, unlocking, and removing the Windows Hello
/// at-rest wrapper of the vault DEK.
/// </summary>
public interface IVaultHelloService
{
    /// <summary>Whether this machine can enroll a TPM-present Windows Hello wrapper.</summary>
    Task<bool> IsEnrollmentAvailableAsync(CancellationToken ct = default);

    /// <summary>Enroll a Hello-wrapped copy of the supplied DEK.</summary>
    Task<VaultHelloEnrollment> EnrollAsync(ReadOnlyMemory<byte> dek, string vaultId, CancellationToken ct);

    /// <summary>Unlock the DEK from a previously persisted Hello enrollment.</summary>
    Task<VaultDekHolder> UnlockAsync(VaultHelloEnrollment stored, CancellationToken ct);

    /// <summary>Remove the platform credential. The caller clears persisted metadata.</summary>
    Task RemoveAsync(string credentialName, CancellationToken ct = default);
}
