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

using System.Buffers.Binary;
using System.IO;
using System.Text;
using Heimdall.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

public sealed class PuttyPpkV3AndUserCertificateTests
{
    [Theory]
    [InlineData(SshKeyAlgorithm.Ed25519, null)]
    [InlineData(SshKeyAlgorithm.Ed25519, "p@ssword-ed25519!")]
    [InlineData(SshKeyAlgorithm.Rsa2048, null)]
    [InlineData(SshKeyAlgorithm.Rsa2048, "p@ssword-rsa2048!")]
    [InlineData(SshKeyAlgorithm.EcdsaP256, null)]
    [InlineData(SshKeyAlgorithm.EcdsaP256, "p@ssword-ecdsa256!")]
    public void Generate_PuttyPpkV3_RoundTripsWithSshNetPrivateKeyFile(
        SshKeyAlgorithm algorithm,
        string? passphrase)
    {
        var generated = SshKeyPairGenerator.Generate(
            algorithm,
            comment: "unit-test@heimdall",
            passphrase: passphrase,
            format: SshPrivateKeyFormat.PuttyPpkV3);

        Assert.StartsWith("PuTTY-User-Key-File-3: ", generated.PrivateKey, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(passphrase))
        {
            Assert.Contains("Encryption: none", generated.PrivateKey, StringComparison.Ordinal);
            Assert.DoesNotContain("Key-Derivation: Argon2id", generated.PrivateKey, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Encryption: aes256-cbc", generated.PrivateKey, StringComparison.Ordinal);
            Assert.Contains("Key-Derivation: Argon2id", generated.PrivateKey, StringComparison.Ordinal);
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(generated.PrivateKey));
        using var parsed = string.IsNullOrEmpty(passphrase)
            ? new PrivateKeyFile(stream)
            : new PrivateKeyFile(stream, passphrase);

        Assert.NotEmpty(parsed.HostKeyAlgorithms);
    }

    [Fact]
    public void Generate_PuttyPpkV3_Encrypted_RejectsWrongPassphrase()
    {
        var generated = SshKeyPairGenerator.Generate(
            SshKeyAlgorithm.Ed25519,
            comment: "encrypted@heimdall",
            passphrase: "correct-passphrase",
            format: SshPrivateKeyFormat.PuttyPpkV3);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(generated.PrivateKey));
        Assert.ThrowsAny<Exception>(() => new PrivateKeyFile(stream, "wrong-passphrase"));
    }

    [Fact]
    public void ResolveUserCertificatePath_PrefersExplicitAndFallsBackToSiblingCertPub()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "heimdall-cert-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keyPath = Path.Combine(tempDir, "id_ed25519");
            var siblingCertPath = keyPath + "-cert.pub";
            var explicitCertPath = Path.Combine(tempDir, "custom-cert.pub");
            File.WriteAllText(keyPath, "dummy");

            Assert.Null(SshConnectionFactory.ResolveUserCertificatePath(keyPath, null));

            File.WriteAllText(siblingCertPath, "dummy-cert");
            Assert.Equal(siblingCertPath, SshConnectionFactory.ResolveUserCertificatePath(keyPath, null));
            Assert.Equal(explicitCertPath, SshConnectionFactory.ResolveUserCertificatePath(keyPath, explicitCertPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildConnectionInfo_WithExplicitUserCertificate_AttachesCertAlgorithm()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "heimdall-usercert-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var userKey = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, "alice@corp", passphrase: null, SshPrivateKeyFormat.OpenSsh);
            var caKey = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, "ca@corp", passphrase: null, SshPrivateKeyFormat.OpenSsh);

            var userPubBlob = Convert.FromBase64String(userKey.PublicKeyOpenSsh.Split(' ')[1]);
            var caPubBlob = Convert.FromBase64String(caKey.PublicKeyOpenSsh.Split(' ')[1]);
            // Ed25519 wire blob is string("ssh-ed25519") + string(32-byte pubkey) => last 32 bytes.
            var userRawPub = userPubBlob[^32..];

            var certBlob = BuildSyntheticEd25519UserCertificate(userRawPub, caPubBlob, "alice");
            var certLine = $"ssh-ed25519-cert-v01@openssh.com {Convert.ToBase64String(certBlob)} alice@corp";

            var keyPath = Path.Combine(tempDir, "id_ed25519");
            var certPath = Path.Combine(tempDir, "id_ed25519-cert.pub");
            File.WriteAllText(keyPath, userKey.PrivateKey);
            File.WriteAllText(certPath, certLine);

            using var client = SshConnectionFactory.CreateSshClient(
                new SshConnectionParams
                {
                    Host = "srv.internal",
                    Port = 22,
                    Username = "alice",
                    KeyPath = keyPath,
                    CertificatePath = certPath
                },
                new Heimdall.Ssh.Agents.SshAgentRegistry([]));

            var pkMethod = Assert.Single(client.ConnectionInfo.AuthenticationMethods.OfType<PrivateKeyAuthenticationMethod>());
            var keyFile = Assert.Single(pkMethod.KeyFiles);
            Assert.Contains(keyFile.HostKeyAlgorithms, a => a.Name == "ssh-ed25519-cert-v01@openssh.com");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void OpenSshConfig_CertificateFile_RoundTripsParserAndExporter()
    {
        var config = """
            Host prod-cert
                HostName 10.20.30.40
                User deploy
                IdentityFile ~/.ssh/id_ed25519
                CertificateFile ~/.ssh/id_ed25519-cert.pub
            """;

        var parsed = OpenSshConfigParser.Parse(config);
        var candidate = Assert.Single(parsed.Candidates);
        Assert.NotNull(candidate.CertificateFile);
        Assert.EndsWith("id_ed25519-cert.pub", candidate.CertificateFile, StringComparison.OrdinalIgnoreCase);

        var exported = SshConfigExporter.ExportSingleHost(new SshConfigHostEntry(
            Alias: candidate.Alias,
            HostName: candidate.HostName,
            User: candidate.User,
            IdentityFile: candidate.IdentityFile,
            CertificateFile: candidate.CertificateFile));
        Assert.Contains("CertificateFile ", exported, StringComparison.Ordinal);
    }

    private static byte[] BuildSyntheticEd25519UserCertificate(byte[] userRawPub32, byte[] caPubBlob, string principal)
    {
        using var ms = new MemoryStream();
        WriteString(ms, "ssh-ed25519-cert-v01@openssh.com");
        WriteBytes(ms, new byte[32]); // nonce
        WriteBytes(ms, userRawPub32);
        WriteUInt64(ms, 42); // serial
        WriteUInt32(ms, 1); // SSH_CERT_TYPE_USER = 1
        WriteString(ms, "alice-cert-01");
        using (var principalsStream = new MemoryStream())
        {
            WriteString(principalsStream, principal);
            WriteBytes(ms, principalsStream.ToArray());
        }

        WriteUInt64(ms, 0); // valid after
        WriteUInt64(ms, ulong.MaxValue); // valid before
        WriteBytes(ms, []); // critical options
        WriteBytes(ms, []); // extensions
        WriteBytes(ms, []); // reserved
        WriteBytes(ms, caPubBlob); // signature key
        using (var sigStream = new MemoryStream())
        {
            WriteString(sigStream, "ssh-ed25519");
            WriteBytes(sigStream, new byte[64]); // dummy signature bytes (format check only)
            WriteBytes(ms, sigStream.ToArray());
        }

        return ms.ToArray();
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteUInt64(Stream s, ulong v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteString(Stream s, string value)
        => WriteBytes(s, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(Stream s, byte[] bytes)
    {
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes);
    }
}
