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
/// A store outside the configuration files that keeps secrets which the vault must seal when it
/// is enabled and return to the legacy form when it is disabled.
/// </summary>
/// <remarks>
/// <para>The lifecycle service passes the transforms, so a store never handles the vault key nor
/// the legacy protector itself. Each transform maps one stored protected value to its new form and
/// returns it unchanged when it is already in that form, so both passes are idempotent and can be
/// resumed after a crash. A store rewrites what it holds atomically, and only when something
/// changed.</para>
/// <para>Every method reads the store itself, never a cache, and an unreadable store throws: an
/// answer of "nothing there" must never come from a failure to look.</para>
/// </remarks>
public interface IVaultSecretStore
{
    /// <summary>The locale key naming the store, shown to the user when it blocks a disable.</summary>
    string StoreNameKey { get; }

    /// <summary>Seals every legacy secret of the store with the vault key.</summary>
    /// <param name="toVault">Maps a stored protected value to its vault-sealed form.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    Task ForwardAsync(Func<string?, string?> toVault, CancellationToken cancellationToken);

    /// <summary>Returns every vault-sealed secret of the store to the legacy form.</summary>
    /// <param name="toLegacy">Maps a stored protected value to its legacy form.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    Task ReverseAsync(Func<string?, string?> toLegacy, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the store still holds a secret the forward pass left in the legacy form, which can
    /// happen when a secret cannot be opened (written by another Windows account, say).
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<bool> HasUnsealedSecretsAsync(CancellationToken cancellationToken);

    /// <summary>Whether the store still holds at least one secret sealed with the vault key.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<bool> HoldsVaultSecretsAsync(CancellationToken cancellationToken);
}
