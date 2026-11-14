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

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Seals the passwords remembered for shared sessions and shared bastions with the vault key when
/// the vault is enabled, and returns them to the legacy form when it is disabled.
/// </summary>
internal sealed class SharedSessionVaultSecretStore : IVaultSecretStore
{
    /// <summary>The locale key naming this store.</summary>
    public const string NameKey = "VaultStoreSharedSessionPasswords";

    private readonly SharedSessionSecretStore _store;

    /// <param name="store">The store of remembered shared-session passwords.</param>
    public SharedSessionVaultSecretStore(SharedSessionSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc/>
    public string StoreNameKey => NameKey;

    /// <inheritdoc/>
    public Task ForwardAsync(Func<string?, string?> toVault, CancellationToken cancellationToken) =>
        _store.TransformSecretsAsync(toVault, cancellationToken);

    /// <inheritdoc/>
    public Task ReverseAsync(Func<string?, string?> toLegacy, CancellationToken cancellationToken) =>
        _store.TransformSecretsAsync(toLegacy, cancellationToken);

    /// <inheritdoc/>
    public Task DropUnreadableAsync(Func<string?, bool> isUnreadable, CancellationToken cancellationToken) =>
        _store.DropSecretsAsync(isUnreadable, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> HasUnsealedSecretsAsync(CancellationToken cancellationToken) =>
        _store.HasUnsealedSecretsAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<bool> HoldsVaultSecretsAsync(CancellationToken cancellationToken) =>
        _store.HoldsVaultSecretsAsync(cancellationToken);
}
