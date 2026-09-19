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

using System.Runtime.Versioning;
using Heimdall.Core.Security;
using TwinShell.Core.Interfaces;

namespace TwinShell.Persistence.Security;

/// <summary>
/// Binds <see cref="ISecretProtector"/> to the application's credential protector, so
/// stored history is sealed exactly like every other secret Heimdall keeps at rest.
/// </summary>
/// <remarks>
/// <para>
/// With a master-password vault active this produces a v2 blob under the vault key; with
/// no vault it produces the legacy DPAPI form, which is already enough for the concern
/// that kept real commands out of the history in the first place - the database file is
/// plain SQLite, and a DPAPI blob is useless to anyone reading that file from another
/// account or another machine.
/// </para>
/// <para>
/// The protector refuses to write while a vault is configured but locked, and this adapter
/// lets that refusal through unchanged. Downgrading to the weaker form at that moment is
/// exactly the write-downgrade the vault design forbids.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CredentialProtectorAdapter : ISecretProtector
{
    /// <inheritdoc/>
    public string Protect(string plainText) => CredentialProtector.Protect(plainText);

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="CredentialProtector.Unprotect"/> throws for a vault blob while the vault
    /// is locked, and returns null for anything else it cannot read. Both mean the same
    /// thing to a history row - it cannot be shown - so both become null here.
    /// </remarks>
    public string? Unprotect(string protectedValue)
    {
        try
        {
            return CredentialProtector.Unprotect(protectedValue);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
