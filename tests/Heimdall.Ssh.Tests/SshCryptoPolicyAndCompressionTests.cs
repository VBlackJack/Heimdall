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

using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Tests;

public sealed class SshCryptoPolicyAndCompressionTests
{
    [Fact]
    public void Create_WithCompressionEnabled_PrioritizesDelayedZlibAheadOfNone()
    {
        var enabledInfo = SshConnectionFactory.Create(new SshConnectionParams
        {
            Host = "server.example.com",
            Port = 22,
            Username = "ops",
            Password = "secret",
            Compression = true
        });

        var disabledInfo = SshConnectionFactory.Create(new SshConnectionParams
        {
            Host = "server.example.com",
            Port = 22,
            Username = "ops",
            Password = "secret",
            Compression = false
        });

        Assert.Equal("zlib@openssh.com", enabledInfo.CompressionAlgorithms.Keys.First());
        Assert.Contains("none", enabledInfo.CompressionAlgorithms.Keys);
        Assert.Equal("none", disabledInfo.CompressionAlgorithms.Keys.First());
    }

    [Fact]
    public void Create_WithStrictModernPolicy_RemovesDeprecatedAlgorithmsAndKeepsPostQuantumAndAead()
    {
        var info = SshConnectionFactory.Create(new SshConnectionParams
        {
            Host = "strict.example.com",
            Port = 22,
            Username = "ops",
            Password = "secret",
            CryptoPolicy = SshCryptoPolicy.StrictModern
        });

        Assert.DoesNotContain(info.KeyExchangeAlgorithms.Keys, SshSessionDiagnostics.IsWeakKex);
        Assert.DoesNotContain(info.HostKeyAlgorithms.Keys, SshSessionDiagnostics.IsWeakHostKey);
        Assert.DoesNotContain(info.Encryptions.Keys, SshSessionDiagnostics.IsWeakCipher);
        Assert.DoesNotContain(info.HmacAlgorithms.Keys, SshSessionDiagnostics.IsWeakMac);

        Assert.DoesNotContain("ssh-rsa", info.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain("ssh-rsa-cert-v01@openssh.com", info.HostKeyAlgorithms.Keys);
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("mlkem768x25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("aes256-gcm@openssh.com", info.Encryptions.Keys);
        Assert.Contains("chacha20-poly1305@openssh.com", info.Encryptions.Keys);
        Assert.Contains("hmac-sha2-256-etm@openssh.com", info.HmacAlgorithms.Keys);
    }

    [Fact]
    public void SshSessionDiagnostics_DetectsPostQuantumKexAndWeakRsaSha1HostCert()
    {
        Assert.True(SshSessionDiagnostics.IsPostQuantumKex("mlkem768x25519-sha256"));
        Assert.True(SshSessionDiagnostics.IsPostQuantumKex("sntrup761x25519-sha512@openssh.com"));
        Assert.False(SshSessionDiagnostics.IsPostQuantumKex("curve25519-sha256"));

        Assert.True(SshSessionDiagnostics.IsWeakCrypto(
            "curve25519-sha256",
            "ssh-rsa-cert-v01@openssh.com",
            "aes256-gcm@openssh.com",
            "none"));

        Assert.False(SshSessionDiagnostics.IsWeakCrypto(
            "mlkem768x25519-sha256",
            "ssh-ed25519-cert-v01@openssh.com",
            "chacha20-poly1305@openssh.com",
            "none"));
    }

    [Fact]
    public void SshCryptoPolicyResolver_PrefersProfileOverrideOverGlobalSetting()
    {
        Assert.Equal(
            SshCryptoPolicy.StrictModern,
            SshCryptoPolicyResolver.Resolve(SshCryptoPolicy.StrictModern, SshCryptoPolicy.Standard));
        Assert.Equal(
            SshCryptoPolicy.LegacyCompatible,
            SshCryptoPolicyResolver.Resolve(null, SshCryptoPolicy.LegacyCompatible));
    }
}
