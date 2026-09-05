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

namespace Heimdall.Core.Tests.Vault;

/// <summary>
/// Pins the process-global <see cref="CredentialProtector"/> static state for the lifetime of
/// one test class instance. xUnit builds a fresh instance per test, so the baseline is
/// re-established before and after every test of a class that owns a scope.
/// </summary>
/// <remarks>
/// <para>The protector keeps the vault-enabled flag, the borrowed DEK and the legacy HMAC key in
/// static slots shared by every test in the assembly. Members of
/// <see cref="CredentialProtectorStaticCollection"/> never run concurrently, but membership
/// alone does not make a class independent of the mode the previously scheduled class left
/// behind: a writer that forgets one reset leaves "enabled, no DEK" on the floor, and the next
/// reader's <c>Protect</c> throws <c>VaultLockedException</c> far from the cause. Measured on
/// 2026-09-05: dropping <c>SetVaultEnabled(false)</c> from one writer's <c>Dispose</c> and
/// scheduling its locked-vault test last failed two <c>ConfigManagerTests</c> cases.</para>
/// <para>The previous values are deliberately not snapshotted: the vault slots are write-only in
/// production (nothing can read back the borrowed DEK or the raw HMAC key), and inheriting them
/// is the defect this type exists to remove. The scope establishes a declared baseline on entry
/// and re-establishes it on exit: vault disabled, no DEK, and the HMAC key the caller asked
/// for.</para>
/// <para><see cref="CredentialProtectorCollectionGuardTests"/> keeps every member of the
/// collection on this type, so the rule is declared once here instead of being re-typed in
/// each class.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CredentialProtectorStateScope : IDisposable
{
    /// <summary>
    /// Establish the baseline for a test class instance.
    /// </summary>
    /// <param name="hmacKeyBase64">
    /// The legacy HMAC key to install, or <c>null</c> for plain DPAPI output.
    /// </param>
    public CredentialProtectorStateScope(string? hmacKeyBase64 = null)
    {
        Reset(hmacKeyBase64);
    }

    /// <summary>
    /// Re-establish the baseline: vault disabled, no DEK, and the given HMAC key. Exposed for
    /// lifetimes that xUnit drives without a constructor, such as <c>IAsyncLifetime</c>.
    /// </summary>
    /// <param name="hmacKeyBase64">
    /// The legacy HMAC key to install, or <c>null</c> for plain DPAPI output.
    /// </param>
    public static void Reset(string? hmacKeyBase64 = null)
    {
        // Drop the DEK before clearing the flag: the intermediate state is then "enabled but
        // locked", which fails closed, rather than "disabled with a DEK still installed", which
        // would silently emit a v2 blob.
        CredentialProtector.ClearVaultKey();
        CredentialProtector.SetVaultEnabled(false);
        CredentialProtector.Initialize(hmacKeyBase64);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Reset();
    }
}
