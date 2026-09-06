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

using System.Security.AccessControl;
using System.Security.Principal;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// OpenSSH reads key files byte for byte: a UTF-8 BOM in front of the type
/// token or the PEM header makes the key unreadable, and a private key that
/// other accounts can read is refused by Win32-OpenSSH.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SshKeyFileWriterTests : IDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "heimdall-ssh-key-file-writer",
        Guid.NewGuid().ToString("N"));

    public SshKeyFileWriterTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Fact]
    public void WritePublicKey_WritesTypeTokenFirstWithLfEnding()
    {
        string path = Path.Combine(_rootPath, "id_ed25519.pub");

        SshKeyFileWriter.WritePublicKey(path, "ssh-ed25519 AAAAC3 comment");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(Utf8Bom, bytes[..3]);
        Assert.Equal("ssh-ed25519 AAAAC3 comment\n", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WritePrivateKey_WritesPemHeaderFirstWithLfEndings()
    {
        string path = Path.Combine(_rootPath, "id_ed25519");

        SshKeyFileWriter.WritePrivateKey(path, "-----BEGIN PRIVATE KEY-----\r\nAAAA\r\n-----END PRIVATE KEY-----\r\n");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(Utf8Bom, bytes[..3]);
        Assert.Equal(
            "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n",
            System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WritePrivateKey_RestrictsAclToOwnerAdministratorsAndSystem()
    {
        string path = Path.Combine(_rootPath, "id_rsa");

        SshKeyFileWriter.WritePrivateKey(path, "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n");

        FileSecurity acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            SecurityIdentifier identity = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(
                identity == currentUser || identity == administrators || identity == system,
                $"Unexpected ACL principal on generated private key: {identity}");
        }
    }

    [Fact]
    public void WritePrivateKey_ReplacesAnExistingFile()
    {
        string path = Path.Combine(_rootPath, "id_rsa");
        File.WriteAllText(path, "stale");

        SshKeyFileWriter.WritePrivateKey(path, "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----\n");

        Assert.Equal(
            "-----BEGIN PRIVATE KEY-----\nBBBB\n-----END PRIVATE KEY-----\n",
            File.ReadAllText(path));
    }
}
