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
/// Thrown when the vault cannot be disabled because secrets are still being written and did not
/// finish in time. Nothing was turned off; trying again later is safe.
/// </summary>
public sealed class VaultBusyException : Exception
{
    /// <summary>Create the exception.</summary>
    public VaultBusyException()
        : base("Secrets are still being written; the master password was left as it is.")
    {
    }
}
