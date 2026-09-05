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

using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Tells a connect that was refused for a reason the user can act on apart from one that failed.
/// </summary>
/// <remarks>
/// <para>Decrypting the profile's password throws <see cref="VaultLockedException"/> while the
/// workspace is locked. That used to land in the generic catch of the connect attempt, which
/// wrote "Unable to start the embedded Remote Desktop session" followed by the exception text
/// into the session header - a fault report for something that is not a fault. The Citrix
/// launcher already says "unlock the vault" for the same exception; this is the same sentence
/// for RDP.</para>
/// <para>Pure and separate from the view so the mapping has a test, and so a second refusal
/// reason is one line here rather than one more catch clause in a code-behind.</para>
/// </remarks>
internal static class RdpConnectRefusal
{
    /// <summary>Locale key of the refusal shown for a locked vault.</summary>
    internal const string VaultLockedStatusKey = "RdpConnectVaultLocked";

    /// <summary>
    /// The locale key of the status line to show for <paramref name="exception"/>, or null when it
    /// is a fault rather than a refusal.
    /// </summary>
    internal static string? StatusKeyFor(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is VaultLockedException ? VaultLockedStatusKey : null;
    }
}
