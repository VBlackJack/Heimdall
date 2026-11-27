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

namespace Heimdall.Ssh.Tests;

public sealed class SshSessionDiagnosticsTests
{
    [Theory]
    [InlineData("curve25519-sha256", "ssh-ed25519", "chacha20-poly1305@openssh.com", "hmac-sha1")]
    [InlineData("curve25519-sha256@libssh.org", "rsa-sha2-512", "aes256-gcm@openssh.com", "none")]
    [InlineData("ecdh-sha2-nistp256", "ecdsa-sha2-nistp256", "aes256-ctr", "hmac-sha2-256-etm@openssh.com")]
    [InlineData("diffie-hellman-group14-sha256", "rsa-sha2-256", "aes128-ctr", "hmac-sha2-512")]
    public void IsWeakCrypto_ModernSuites_ReturnsFalse(string kex, string hostKey, string cipher, string mac)
    {
        Assert.False(SshSessionDiagnostics.IsWeakCrypto(kex, hostKey, cipher, mac));
    }

    [Theory]
    [InlineData("diffie-hellman-group14-sha1", "ssh-ed25519", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("diffie-hellman-group-exchange-sha1@ssh.com", "ssh-ed25519", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("diffie-hellman-group1-sha1", "ssh-ed25519", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-dss", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-dss-cert-v01@openssh.com", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-rsa", "aes256-ctr", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "aes128-cbc", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "rijndael-cbc@lysator.liu.se", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "3des-cbc", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "arcfour256", "hmac-sha2-256")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "aes256-ctr", "hmac-sha1")]
    [InlineData("curve25519-sha256", "ssh-ed25519", "aes256-ctr", "hmac-md5")]
    public void IsWeakCrypto_LegacySuites_ReturnsTrue(string kex, string hostKey, string cipher, string mac)
    {
        Assert.True(SshSessionDiagnostics.IsWeakCrypto(kex, hostKey, cipher, mac));
    }

    [Fact]
    public void SshShellSession_GetDiagnostics_WhenDisconnected_ReturnsNull()
    {
        using var session = new SshShellSession();
        Assert.Null(session.GetDiagnostics());
    }
}
