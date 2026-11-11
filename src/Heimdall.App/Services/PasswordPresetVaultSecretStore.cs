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

using System.IO;
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Services;

/// <summary>
/// Seals the saved password presets with the vault key when the vault is enabled, and returns the
/// file to the legacy form when it is disabled.
/// </summary>
/// <remarks>
/// Works on the raw file and writes it atomically, never through <see cref="PasswordPresetStorage"/>:
/// that class reports a file it cannot read as an empty store and swallows write failures, and
/// neither is acceptable from a pass whose failure must stop the vault from being disabled. A file
/// that is not a sealed value (plain JSON, or anything unrecognised) is left exactly as it is.
/// Every access takes the lock the storage shares, so a save cannot land between a read and a write.
/// </remarks>
internal sealed class PasswordPresetVaultSecretStore : IVaultSecretStore
{
    /// <summary>The locale key naming this store.</summary>
    public const string NameKey = "VaultStorePasswordPresets";

    private readonly string _filePath;

    /// <param name="directoryPath">The directory the preset file lives in.</param>
    public PasswordPresetVaultSecretStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _filePath = Path.Combine(directoryPath, PasswordPresetStorage.PresetsFileName);
    }

    /// <inheritdoc/>
    public string StoreNameKey => NameKey;

    /// <inheritdoc/>
    public Task ForwardAsync(Func<string?, string?> toVault, CancellationToken cancellationToken) =>
        TransformAsync(toVault, cancellationToken);

    /// <inheritdoc/>
    public Task ReverseAsync(Func<string?, string?> toLegacy, CancellationToken cancellationToken) =>
        TransformAsync(toLegacy, cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> HoldsVaultSecretsAsync(CancellationToken cancellationToken) =>
        await InspectAsync(VaultConfigurationScan.IsVaultSecret, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<bool> HasUnsealedSecretsAsync(CancellationToken cancellationToken) =>
        await InspectAsync(value => !VaultConfigurationScan.IsVaultSecret(value), cancellationToken).ConfigureAwait(false);

    private async Task<bool> InspectAsync(Func<string, bool> test, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = PasswordPresetFileGate.For(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? sealedValue = await ReadSealedAsync(cancellationToken).ConfigureAwait(false);
            return sealedValue is not null && test(sealedValue);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TransformAsync(Func<string?, string?> transform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transform);
        SemaphoreSlim gate = PasswordPresetFileGate.For(_filePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? sealedValue = await ReadSealedAsync(cancellationToken).ConfigureAwait(false);
            if (sealedValue is null)
            {
                return;
            }

            string? next = transform(sealedValue);
            if (!string.IsNullOrEmpty(next) && !string.Equals(next, sealedValue, StringComparison.Ordinal))
            {
                await SecureFileWriter.WriteAllTextAtomicAsync(_filePath, next, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The sealed value in the file, or null when there is none to convert. An unreadable file throws.</summary>
    private async Task<string?> ReadSealedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        string raw = (await File.ReadAllTextAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false))
            .TrimStart(PasswordPresetStorage.ByteOrderMark)
            .Trim();
        return raw.Length == 0 || raw[0] is '{' or '[' ? null : raw;
    }
}
