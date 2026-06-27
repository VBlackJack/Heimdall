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
/// Resumable state of the vault's forward migration (legacy secrets -> v2). Only
/// the forward direction is tracked: enabling the vault persists
/// <see cref="InProgress"/> before migrating so a crash is recoverable, then
/// <see cref="Complete"/> on success. Disabling runs an idempotent reverse pass
/// that does not use this state (it is retry-safe by construction).
/// </summary>
public enum VaultMigrationState
{
    /// <summary>The vault is not enabled (or has been disabled); no migration pending.</summary>
    None = 0,

    /// <summary>Forward migration has started and is not confirmed complete; resume on unlock.</summary>
    InProgress = 1,

    /// <summary>Forward migration completed.</summary>
    Complete = 2,
}
