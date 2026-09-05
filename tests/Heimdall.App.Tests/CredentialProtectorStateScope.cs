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

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the process-global <see cref="CredentialProtector"/> static state for the lifetime of
/// one test class instance: vault disabled, no DEK, and the HMAC key the class asks for, on
/// entry and again on exit. xUnit builds a fresh instance per test, so the baseline surrounds
/// every test of a class that owns a scope.
/// </summary>
/// <remarks>
/// <para>The App-side twin of the type of the same name in <c>Heimdall.Core.Tests</c>. This
/// project does not reference that one, and the collection definition is already duplicated
/// for the same reason; the guard in <see cref="CredentialProtectorAppCollection"/> keeps every
/// member of the App collection on this type.</para>
/// <para>Membership of the collection removes concurrency and nothing else: a class inherits
/// the mode the previously scheduled member left behind, and a reader that only protects a
/// value fails far from the writer that forgot one reset. The previous values are deliberately
/// not snapshotted: the vault slots are write-only in production, and inheriting them is the
/// defect this type exists to remove.</para>
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
    /// lifetimes that xUnit drives without a constructor, such as <c>IAsyncLifetime</c>, and
    /// for tests that flip the vault mode inside a <c>try</c> and restore it in the
    /// <c>finally</c>.
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
