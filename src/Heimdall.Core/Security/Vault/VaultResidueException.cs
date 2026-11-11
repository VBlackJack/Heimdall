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
/// Thrown when the vault cannot be disabled: some stores still hold secrets sealed with the vault
/// key after they were returned to the legacy form, or a step failed. The vault stays on.
/// </summary>
/// <remarks>
/// Whatever was converted is sealed again before this is thrown. If that restoration itself failed,
/// <see cref="ProtectionRestored"/> is false: some secrets are then protected by Windows only until
/// the next unlock, which seals them again.
/// </remarks>
public sealed class VaultResidueException : Exception
{
    /// <summary>Create the exception for the stores that still hold vault secrets.</summary>
    /// <param name="storeNameKeys">The locale keys naming the stores, in the order they were checked; empty when a step failed instead.</param>
    /// <param name="protectionRestored">Whether everything converted was sealed again.</param>
    /// <param name="innerException">The failure that stopped the disable, if it was one.</param>
    public VaultResidueException(
        IReadOnlyList<string> storeNameKeys,
        bool protectionRestored = true,
        Exception? innerException = null)
        : base("The master password could not be removed from every store.", innerException)
    {
        ArgumentNullException.ThrowIfNull(storeNameKeys);
        StoreNameKeys = storeNameKeys;
        ProtectionRestored = protectionRestored;
    }

    /// <summary>The locale keys naming the stores that still hold vault secrets.</summary>
    public IReadOnlyList<string> StoreNameKeys { get; }

    /// <summary>Whether everything that was converted has been sealed again.</summary>
    public bool ProtectionRestored { get; }
}
