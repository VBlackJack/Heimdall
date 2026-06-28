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
using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class VaultHelloProtectorTests
{
    [Fact]
    public void DeriveHelloKek_FixedInputs_IsDeterministic()
    {
        var signature = Bytes(256, 0x11);
        var salt = Bytes(VaultHelloProtector.SaltSizeBytes, 0x22);

        var a = VaultHelloProtector.DeriveHelloKek(signature, salt);
        var b = VaultHelloProtector.DeriveHelloKek(signature, salt);

        Assert.Equal(VaultCipher.KeySizeBytes, a.Length);
        Assert.Equal(a, b);
        CryptographicOperations.ZeroMemory(a);
        CryptographicOperations.ZeroMemory(b);
    }

    [Fact]
    public void DeriveHelloKek_DifferentSalt_ChangesKey()
    {
        var signature = Bytes(256, 0x11);
        var saltA = Bytes(VaultHelloProtector.SaltSizeBytes, 0x22);
        var saltB = Bytes(VaultHelloProtector.SaltSizeBytes, 0x23);

        var a = VaultHelloProtector.DeriveHelloKek(signature, saltA);
        var b = VaultHelloProtector.DeriveHelloKek(signature, saltB);

        Assert.NotEqual(a, b);
        CryptographicOperations.ZeroMemory(a);
        CryptographicOperations.ZeroMemory(b);
    }

    [Fact]
    public void WrapDek_ThenUnwrapDek_RoundTrips()
    {
        var dek = Bytes(VaultCipher.KeySizeBytes, 0x31);
        var helloKek = Bytes(VaultCipher.KeySizeBytes, 0x41);
        var binding = Binding();

        var wrapped = VaultHelloProtector.WrapDek(dek, helloKek, binding);
        using var unwrapped = VaultHelloProtector.UnwrapDek(wrapped, helloKek, binding);

        Assert.Equal(dek, unwrapped.Key.ToArray());
    }

    [Fact]
    public void UnwrapDek_TamperedAad_FailsGenerically()
    {
        var dek = Bytes(VaultCipher.KeySizeBytes, 0x31);
        var helloKek = Bytes(VaultCipher.KeySizeBytes, 0x41);
        var wrapped = VaultHelloProtector.WrapDek(dek, helloKek, Binding());
        var tamperedBinding = Binding(publicKeyHash: "BADHASH");

        var ex = Assert.Throws<VaultHelloException>(
            () => VaultHelloProtector.UnwrapDek(wrapped, helloKek, tamperedBinding));
        Assert.Equal(VaultHelloFailureReason.CryptoFailure, ex.Reason);
    }

    [Fact]
    public void UnwrapDek_TamperedCiphertext_FailsGenerically()
    {
        var dek = Bytes(VaultCipher.KeySizeBytes, 0x31);
        var helloKek = Bytes(VaultCipher.KeySizeBytes, 0x41);
        var wrapped = VaultHelloProtector.WrapDek(dek, helloKek, Binding());
        var raw = Convert.FromBase64String(wrapped);
        raw[^1] ^= 0x01;
        var tampered = Convert.ToBase64String(raw);

        var ex = Assert.Throws<VaultHelloException>(
            () => VaultHelloProtector.UnwrapDek(tampered, helloKek, Binding()));
        Assert.Equal(VaultHelloFailureReason.CryptoFailure, ex.Reason);
    }

    [Fact]
    public void UnwrapDek_WrongHelloKek_FailsGenerically()
    {
        var dek = Bytes(VaultCipher.KeySizeBytes, 0x31);
        var helloKek = Bytes(VaultCipher.KeySizeBytes, 0x41);
        var wrongKek = Bytes(VaultCipher.KeySizeBytes, 0x42);
        var wrapped = VaultHelloProtector.WrapDek(dek, helloKek, Binding());

        var ex = Assert.Throws<VaultHelloException>(
            () => VaultHelloProtector.UnwrapDek(wrapped, wrongKek, Binding()));
        Assert.Equal(VaultHelloFailureReason.CryptoFailure, ex.Reason);
    }

    private static VaultHelloBinding Binding(string publicKeyHash = "PUBKEYHASH")
    {
        return new VaultHelloBinding(
            "vault-id",
            publicKeyHash,
            Bytes(VaultHelloProtector.ChallengeSizeBytes, 0x51),
            Bytes(VaultHelloProtector.SaltSizeBytes, 0x61));
    }

    private static byte[] Bytes(int length, byte seed)
    {
        return Enumerable.Range(0, length).Select(i => (byte)(seed + i)).ToArray();
    }
}
