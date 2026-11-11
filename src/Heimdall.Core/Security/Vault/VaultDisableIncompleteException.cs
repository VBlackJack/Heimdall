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
/// Thrown when a disable that had to be undone could not write the wrapped key back to disk.
/// </summary>
/// <remarks>
/// Nothing was disabled. The key is still held in memory and the vault is on in this process, but
/// not on disk: closing Heimdall now would lose every secret sealed with that key. Call
/// <see cref="VaultLifecycleService.RetryRestoreAsync"/> until it succeeds.
/// </remarks>
public sealed class VaultDisableIncompleteException : Exception
{
    /// <summary>Create the exception.</summary>
    /// <param name="innerException">The failure that stopped the disable, if any.</param>
    public VaultDisableIncompleteException(Exception? innerException = null)
        : base("The vault key could not be written back to disk; it is held in memory only.", innerException)
    {
    }
}
