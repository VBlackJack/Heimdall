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

using System.Security.Cryptography;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Single-owner holder for a decrypted 256-bit data-encryption key (DEK). The
/// key lives in a pinned buffer so the GC cannot relocate (and thereby
/// duplicate) it, and is zeroed on <see cref="Dispose"/>. Keeping the DEK
/// resident under <c>ProtectedMemory</c> while idle is deferred to Lot 4; this
/// type provides only the pinned, zeroable, disposable container.
/// </summary>
public sealed class VaultDekHolder : IDisposable
{
    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>
    /// Copy <paramref name="key"/> into a freshly pinned buffer owned by this
    /// holder. Constructed only by <see cref="VaultKeyManager"/> (and tests);
    /// callers obtain holders from the manager rather than building them.
    /// </summary>
    /// <param name="key">The 256-bit DEK to take a pinned copy of.</param>
    /// <exception cref="ArgumentException">Thrown when the key is not 32 bytes.</exception>
    internal VaultDekHolder(ReadOnlySpan<byte> key)
    {
        if (key.Length != VaultCipher.KeySizeBytes)
        {
            throw new ArgumentException(
                $"DEK must be {VaultCipher.KeySizeBytes} bytes.", nameof(key));
        }

        _key = GC.AllocateArray<byte>(key.Length, pinned: true);
        key.CopyTo(_key);
    }

    /// <summary>Whether the holder has been disposed (and its key zeroed).</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// The DEK bytes, for use by the cipher. Read-only; the caller must not copy
    /// the key into an unmanaged-lifetime buffer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown after <see cref="Dispose"/>.</exception>
    public ReadOnlySpan<byte> Key =>
        _disposed ? throw new ObjectDisposedException(nameof(VaultDekHolder)) : _key;

    /// <summary>Zero the pinned key buffer. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
