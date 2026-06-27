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
/// Thrown when a version-2 vault secret blob is read while no usable DEK is
/// available (the vault is locked). This is the explicit fail-closed signal
/// required for downgrade resistance: a v2 blob must never be silently returned
/// as <c>null</c> nor reinterpreted through the legacy decryption path when the
/// vault is locked.
/// </summary>
public sealed class VaultLockedException : Exception
{
    /// <summary>The fixed generic message used whenever the vault is locked.</summary>
    public const string GenericMessage = "The vault is locked.";

    /// <summary>Create the exception with the fixed generic message.</summary>
    public VaultLockedException()
        : base(GenericMessage)
    {
    }
}
