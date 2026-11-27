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

using System.Text;
using Heimdall.Core.Security;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

public sealed class SshKeyPairGeneratorTests
{
    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519, "ssh-ed25519", "Ed25519", "id_ed25519")]
    [InlineData(SshKeyAlgorithm.Rsa2048, "ssh-rsa", "RSA", "id_rsa")]
    [InlineData(SshKeyAlgorithm.EcdsaP256, "ecdsa-sha2-nistp256", "ECDSA", "id_ecdsa")]
    [InlineData(SshKeyAlgorithm.EcdsaP384, "ecdsa-sha2-nistp384", "ECDSA", "id_ecdsa")]
    [InlineData(SshKeyAlgorithm.EcdsaP521, "ecdsa-sha2-nistp521", "ECDSA", "id_ecdsa")]
    public void Generate_OpenSshUnencrypted_RoundTripsThroughSshNetAndAuditEngine(
        SshKeyAlgorithm algorithm,
        string expectedPrefix,
        string expectedAuditAlgorithm,
        string expectedFileName)
    {
        var pair = SshKeyPairGenerator.Generate(
            algorithm,
            comment: "operator@heimdall",
            passphrase: null,
            format: SshPrivateKeyFormat.OpenSsh);

        Assert.StartsWith(expectedPrefix + " ", pair.PublicKeyOpenSsh, StringComparison.Ordinal);
        Assert.EndsWith(" operator@heimdall", pair.PublicKeyOpenSsh, StringComparison.Ordinal);
        Assert.StartsWith("SHA256:", pair.Fingerprint, StringComparison.Ordinal);
        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", pair.PrivateKey, StringComparison.Ordinal);
        Assert.Equal(expectedFileName, pair.DefaultFileBaseName);

        using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKey));
        using var parsed = new PrivateKeyFile(keyStream);
        Assert.NotEmpty(parsed.HostKeyAlgorithms);

        var pubAudit = SshKeyAuditEngine.Audit(pair.PublicKeyOpenSsh);
        Assert.NotNull(pubAudit);
        Assert.Equal(expectedAuditAlgorithm, pubAudit.Algorithm);
        Assert.Equal(pair.Fingerprint, pubAudit.Fingerprint);

        var privAudit = SshKeyAuditEngine.Audit(pair.PrivateKey);
        Assert.NotNull(privAudit);
        Assert.Equal(expectedAuditAlgorithm, privAudit.Algorithm);
        Assert.Equal(pair.Fingerprint, privAudit.Fingerprint);
        Assert.True(privAudit.IsPrivateKey);
        Assert.False(privAudit.IsEncrypted);
    }

    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519)]
    [InlineData(SshKeyAlgorithm.Rsa2048)]
    [InlineData(SshKeyAlgorithm.EcdsaP256)]
    [InlineData(SshKeyAlgorithm.EcdsaP384)]
    [InlineData(SshKeyAlgorithm.EcdsaP521)]
    public void Generate_OpenSshEncryptedWithPassphrase_RoundTripsThroughSshNet(SshKeyAlgorithm algorithm)
    {
        const string passphrase = "test-passphrase-for-bcrypt-kdf!";
        var pair = SshKeyPairGenerator.Generate(
            algorithm,
            comment: "enc@heimdall",
            passphrase: passphrase,
            format: SshPrivateKeyFormat.OpenSsh);

        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", pair.PrivateKey, StringComparison.Ordinal);

        using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKey));
        using var parsed = new PrivateKeyFile(keyStream, passphrase);
        Assert.NotEmpty(parsed.HostKeyAlgorithms);

        var privAudit = SshKeyAuditEngine.Audit(pair.PrivateKey);
        Assert.NotNull(privAudit);
        Assert.True(privAudit.IsPrivateKey);
        Assert.True(privAudit.IsEncrypted);
        Assert.Equal(pair.Fingerprint, privAudit.Fingerprint);
    }

    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519, "Ed25519")]
    [InlineData(SshKeyAlgorithm.Rsa2048, "RSA")]
    [InlineData(SshKeyAlgorithm.EcdsaP256, "ECDSA")]
    public void Generate_Pkcs8PemUnencrypted_ParsesWithAuditEngine(
        SshKeyAlgorithm algorithm,
        string expectedAuditAlgorithm)
    {
        var pair = SshKeyPairGenerator.Generate(
            algorithm,
            comment: null,
            passphrase: null,
            format: SshPrivateKeyFormat.Pkcs8Pem);

        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", pair.PrivateKey, StringComparison.Ordinal);

        var audit = SshKeyAuditEngine.Audit(pair.PrivateKey);
        Assert.NotNull(audit);
        Assert.Equal(expectedAuditAlgorithm, audit.Algorithm);
        Assert.True(audit.IsPrivateKey);
        Assert.False(audit.IsEncrypted);
    }

    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519)]
    [InlineData(SshKeyAlgorithm.Rsa2048)]
    [InlineData(SshKeyAlgorithm.EcdsaP256)]
    public void Generate_Pkcs8PemEncrypted_ProducesEncryptedPemBlock(SshKeyAlgorithm algorithm)
    {
        var pair = SshKeyPairGenerator.Generate(
            algorithm,
            comment: "pkcs8@heimdall",
            passphrase: "pkcs8-secret-passphrase",
            format: SshPrivateKeyFormat.Pkcs8Pem);

        Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", pair.PrivateKey, StringComparison.Ordinal);

        var audit = SshKeyAuditEngine.Audit(pair.PrivateKey);
        Assert.NotNull(audit);
        Assert.True(audit.IsPrivateKey);
        Assert.True(audit.IsEncrypted);
    }
}
