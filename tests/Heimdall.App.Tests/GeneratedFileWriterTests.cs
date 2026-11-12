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
using System.Security.AccessControl;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// A generator file that carries a private key is written with a restricted ACL; a certificate
/// is an ordinary file.
/// </summary>
public sealed class GeneratedFileWriterTests : IDisposable
{
    private const string KeyPem = "-----BEGIN PRIVATE KEY-----AAAA-----END PRIVATE KEY-----";
    private const string CertPem = "-----BEGIN CERTIFICATE-----BBBB-----END CERTIFICATE-----";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "heimdall-generated-file-writer",
        Guid.NewGuid().ToString("N"));

    public GeneratedFileWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RequiresProtectedWrite_IsTrueForAKeyFlagOrAKeyPem_AndFalseForACertificate()
    {
        Assert.True(GeneratedFileWriter.RequiresProtectedWrite(new SaveFileRequest(new byte[] { 1 }, true, "*.pfx", "a.pfx", ContainsPrivateKey: true)));
        Assert.True(GeneratedFileWriter.RequiresProtectedWrite(new SaveFileRequest(KeyPem, false, "*.key", "a.key")));
        Assert.False(GeneratedFileWriter.RequiresProtectedWrite(new SaveFileRequest(CertPem, false, "*.pem", "a.pem")));
        Assert.False(GeneratedFileWriter.RequiresProtectedWrite(new SaveFileRequest(new byte[] { 1 }, true, "*.bin", "a.bin")));
    }

    [Fact]
    public void Write_PrivateKeyText_CreatesAFileWithAProtectedAcl()
    {
        string path = Path.Combine(_directory, "server.key");

        Assert.True(GeneratedFileWriter.Write(path, new SaveFileRequest(KeyPem, false, "*.key", "server.key")));

        Assert.Equal(KeyPem, File.ReadAllText(path));
        Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Write_PfxBytes_CreatesAFileWithAProtectedAcl()
    {
        string path = Path.Combine(_directory, "server.pfx");
        byte[] pfx = [1, 2, 3, 4];

        Assert.True(GeneratedFileWriter.Write(path, new SaveFileRequest(pfx, true, "*.pfx", "server.pfx", ContainsPrivateKey: true)));

        Assert.Equal(pfx, File.ReadAllBytes(path));
        Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Write_Certificate_KeepsTheInheritedAcl()
    {
        string path = Path.Combine(_directory, "server.pem");

        Assert.True(GeneratedFileWriter.Write(path, new SaveFileRequest(CertPem, false, "*.pem", "server.pem")));

        Assert.Equal(CertPem, File.ReadAllText(path));
        Assert.False(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Write_PrivateKeyOverAnExistingFile_ReplacesItAndLeavesNoTemporaryFile()
    {
        string path = Path.Combine(_directory, "server.key");
        File.WriteAllText(path, "old");

        Assert.True(GeneratedFileWriter.Write(path, new SaveFileRequest(KeyPem, false, "*.key", "server.key")));

        Assert.Equal(KeyPem, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void Write_PrivateKeyThatCannotBeWritten_KeepsTheExistingFileAndThrows()
    {
        string path = Path.Combine(_directory, "held.key");
        File.WriteAllText(path, "old");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                GeneratedFileWriter.Write(path, new SaveFileRequest(KeyPem, false, "*.key", "held.key")));
        }

        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public void Write_ContentOfAnUnknownForm_WritesNothing()
    {
        string path = Path.Combine(_directory, "none.bin");

        Assert.False(GeneratedFileWriter.Write(path, new SaveFileRequest(42, false, "*.*", "none.bin")));

        Assert.False(File.Exists(path));
    }
}
