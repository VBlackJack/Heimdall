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
using System.Text;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

[SupportedOSPlatform("windows")]
public sealed class VaultKeyManagerTests
{
    // Cheap Argon2id parameters: orchestration tests do not re-verify KDF
    // correctness (covered by the Lot 1a KAT), so a low memory cost keeps them fast.
    private static readonly Argon2idParameters FastParams = new(MemoryKib: 256, Iterations: 1, Parallelism: 1);

    private static byte[] Pw(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void WrapDek_ThenUnwrapDek_RecoversExactDek()
    {
        var password = Pw("correct horse battery staple");
        using var dekHolder = VaultKeyManager.GenerateDek();
        var originalDek = dekHolder.Key.ToArray();

        var stored = VaultKeyManager.WrapDek(password, dekHolder.Key, FastParams);
        using var unwrapped = VaultKeyManager.UnwrapDek(password, stored);

        Assert.Equal(originalDek, unwrapped.Key.ToArray());
    }

    [Fact]
    public void GenerateDek_ProducesDistinct256BitKeys()
    {
        using var a = VaultKeyManager.GenerateDek();
        using var b = VaultKeyManager.GenerateDek();

        Assert.Equal(VaultCipher.KeySizeBytes, a.Key.Length);
        Assert.NotEqual(a.Key.ToArray(), b.Key.ToArray());
    }

    [Fact]
    public void UnwrapDek_WrongPassword_ThrowsVaultUnlockException()
    {
        using var dekHolder = VaultKeyManager.GenerateDek();
        var stored = VaultKeyManager.WrapDek(Pw("right-password"), dekHolder.Key, FastParams);

        var ex = Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.UnwrapDek(Pw("wrong-password"), stored));

        Assert.Equal(VaultUnlockException.GenericMessage, ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void UnwrapDek_WrongPasswordAndCorruption_AreIndistinguishable()
    {
        var password = Pw("the-master-password");
        using var dekHolder = VaultKeyManager.GenerateDek();
        var stored = VaultKeyManager.WrapDek(password, dekHolder.Key, FastParams);

        // (a) Wrong password -> AEAD tag mismatch.
        var wrongPassword = Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.UnwrapDek(Pw("not-the-password"), stored));

        // (b) Corrupted outer DPAPI blob -> DPAPI failure.
        var corruptedDpapi = FlipMiddleByte(stored);
        var corruptedOuter = Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.UnwrapDek(password, corruptedDpapi));

        // (c) Corrupted envelope ciphertext (DPAPI peels, structure decodes, AEAD fails).
        var envelope = DpapiProvider.Unprotect(stored);
        var envelopeRaw = Convert.FromBase64String(envelope);
        envelopeRaw[^1] ^= 0xFF; // flip the last wrapped-DEK ciphertext byte
        var corruptedEnvelope = DpapiProvider.Protect(Convert.ToBase64String(envelopeRaw));
        var corruptedInner = Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.UnwrapDek(password, corruptedEnvelope));

        // (d) Garbage input -> decode failure.
        var garbage = Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.UnwrapDek(password, "not-a-real-blob"));

        // All four causes must be indistinguishable: same type, same message, no inner.
        foreach (var ex in new[] { wrongPassword, corruptedOuter, corruptedInner, garbage })
        {
            Assert.Equal(VaultUnlockException.GenericMessage, ex.Message);
            Assert.Null(ex.InnerException);
        }
    }

    [Fact]
    public void UnwrapDek_NullStored_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => VaultKeyManager.UnwrapDek(Pw("pw"), null!));
    }

    [Fact]
    public void ChangeMasterPassword_RewrapsSameDek_OldStopsWorkingOnNewBlob()
    {
        var oldPw = Pw("old-master");
        var newPw = Pw("new-master");
        using var dekHolder = VaultKeyManager.GenerateDek();
        var originalDek = dekHolder.Key.ToArray();

        var oldBlob = VaultKeyManager.WrapDek(oldPw, dekHolder.Key, FastParams);
        var newBlob = VaultKeyManager.ChangeMasterPassword(oldPw, newPw, oldBlob, FastParams);

        // New password unwraps the new blob to the SAME DEK.
        using (var fromNew = VaultKeyManager.UnwrapDek(newPw, newBlob))
        {
            Assert.Equal(originalDek, fromNew.Key.ToArray());
        }

        // Old blob is untouched and still unwraps to the same DEK.
        using (var fromOld = VaultKeyManager.UnwrapDek(oldPw, oldBlob))
        {
            Assert.Equal(originalDek, fromOld.Key.ToArray());
        }

        // Old password no longer unwraps the new blob.
        Assert.Throws<VaultUnlockException>(() => VaultKeyManager.UnwrapDek(oldPw, newBlob));
    }

    [Fact]
    public void ChangeMasterPassword_WrongOldPassword_ThrowsVaultUnlockException()
    {
        using var dekHolder = VaultKeyManager.GenerateDek();
        var oldBlob = VaultKeyManager.WrapDek(Pw("real-old"), dekHolder.Key, FastParams);

        Assert.Throws<VaultUnlockException>(
            () => VaultKeyManager.ChangeMasterPassword(Pw("wrong-old"), Pw("new"), oldBlob, FastParams));
    }

    [Fact]
    public void WrapDek_WrongDekLength_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => VaultKeyManager.WrapDek(Pw("pw"), new byte[16], FastParams));
    }

    private static string FlipMiddleByte(string base64)
    {
        var raw = Convert.FromBase64String(base64);
        raw[raw.Length / 2] ^= 0xFF;
        return Convert.ToBase64String(raw);
    }
}
