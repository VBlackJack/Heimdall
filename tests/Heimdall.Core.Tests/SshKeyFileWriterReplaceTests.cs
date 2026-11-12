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

using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

/// <summary>
/// Saving a generated private key over an existing one must never leave the user without a key.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SshKeyFileWriterReplaceTests : IDisposable
{
    private const string OldKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nold\n-----END OPENSSH PRIVATE KEY-----\n";
    private const string NewKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nnew\n-----END OPENSSH PRIVATE KEY-----\n";

    private const string OldPublicKey = "ssh-ed25519 AAAAold user@old\n";
    private const string NewPublicKey = "ssh-ed25519 AAAAnew user@new";

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "heimdall-ssh-key-replace",
        Guid.NewGuid().ToString("N"));

    public SshKeyFileWriterReplaceTests()
    {
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            foreach (string file in Directory.GetFiles(_rootPath))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Fact]
    public void WritePrivateKey_ExistingFileHeldOpen_NeverLeavesTheUserWithoutAKey()
    {
        string path = Path.Combine(_rootPath, "id_ed25519");
        File.WriteAllText(path, OldKey);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            try
            {
                SshKeyFileWriter.WritePrivateKey(path, NewKey);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Assert.True(File.Exists(path));
        string content = Utf8NoBom.GetString(File.ReadAllBytes(path));
        Assert.True(content == OldKey || content == NewKey, "the key file holds neither the old nor the new key");
    }

    [Fact]
    public void WritePrivateKey_ReadOnlyExistingFile_ReportsTheFailureAndKeepsTheContent()
    {
        string path = Path.Combine(_rootPath, "id_rsa");
        File.WriteAllText(path, OldKey);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.ThrowsAny<Exception>(() => SshKeyFileWriter.WritePrivateKey(path, NewKey));

        Assert.Equal(OldKey, Utf8NoBom.GetString(File.ReadAllBytes(path)));
    }

    [Fact]
    public void WritePrivateKey_NewFile_WritesNormalisedTextWithoutAStrayTemp()
    {
        string path = Path.Combine(_rootPath, "id_new");

        SshKeyFileWriter.WritePrivateKey(path, NewKey.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(NewKey, Utf8NoBom.GetString(File.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_rootPath));
    }

    [Fact]
    public void WritePublicKey_ReadOnlyExistingFile_ReportsTheFailureAndKeepsTheContent()
    {
        string path = Path.Combine(_rootPath, "id_rsa.pub");
        File.WriteAllText(path, OldPublicKey);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.ThrowsAny<Exception>(() => SshKeyFileWriter.WritePublicKey(path, NewPublicKey));

        Assert.Equal(OldPublicKey, Utf8NoBom.GetString(File.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_rootPath));
    }

    [Fact]
    public void WritePublicKey_ExistingFile_IsReplacedWithoutABomOrAStrayTemp()
    {
        string path = Path.Combine(_rootPath, "id_ed25519.pub");
        File.WriteAllText(path, OldPublicKey);

        SshKeyFileWriter.WritePublicKey(path, NewPublicKey);

        Assert.Equal(NewPublicKey + "\n", Utf8NoBom.GetString(File.ReadAllBytes(path)));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Single(Directory.GetFiles(_rootPath));
    }
}
