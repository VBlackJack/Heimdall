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

using Heimdall.Core.Logging;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Services;

/// <summary>
/// Vault-aware reader for the Command Library Git access token. Decrypts a
/// version-2 (HMS1) token when the vault is unlocked, a legacy DPAPI token when
/// no vault is configured (backward compatible), and treats Git as unavailable
/// when a v2 token is encountered while the vault is locked: it returns
/// <c>null</c> (no exception escapes), deferring Git until unlock (D6) rather
/// than using a wrong or empty token. No token material is ever logged.
/// </summary>
internal static class GitTokenReader
{
    /// <summary>
    /// Decrypt the stored Git access token across vault states, or return
    /// <c>null</c> when there is no token or it cannot be read yet.
    /// </summary>
    /// <param name="encrypted">The stored token blob (v2, legacy DPAPI, or null).</param>
    /// <returns>The plaintext token, or <c>null</c> when absent/locked/undecryptable.</returns>
    internal static string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted))
        {
            return null;
        }

        try
        {
            // Reads a v2 token (vault unlocked) or a legacy DPAPI token (no vault).
            // CredentialProtector fails closed on a v2 token while the vault is locked.
            return CredentialProtector.Unprotect(encrypted);
        }
        catch (VaultLockedException)
        {
            // Vault enabled but locked: defer Git until unlock. No token material logged.
            FileLogger.Info(
                "[TwinShell] Git access token unavailable until the vault is unlocked; Git sync deferred.");
            return null;
        }
        catch
        {
            // Undecryptable legacy blob (e.g. DPAPI from another user): Git stays unconfigured.
            return null;
        }
    }
}
