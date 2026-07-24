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
using System.Security.AccessControl;
using System.Security.Principal;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

[SupportedOSPlatform("windows")]
public class SecureFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public SecureFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private string TempFile(string name = "test.txt") => Path.Combine(_tempDir, name);

    // ── WriteAndProtect creates file with content ──────────────────────

    [Fact]
    public void WriteAndProtect_CreatesFileWithContent()
    {
        var path = TempFile();
        var content = "sensitive data";

        SecureFileWriter.WriteAndProtect(path, content);

        Assert.True(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    // ── WriteAndProtect file is readable by current user ───────────────

    [Fact]
    public void WriteAndProtect_FileIsReadableByCurrentUser()
    {
        var path = TempFile();
        SecureFileWriter.WriteAndProtect(path, "readable content");

        // If this does not throw, the current user can read the file
        var content = File.ReadAllText(path);
        Assert.Equal("readable content", content);
    }

    // ── WriteAndProtect on existing file overwrites ────────────────────

    [Fact]
    public void WriteAndProtect_ExistingFile_Overwrites()
    {
        var path = TempFile();

        SecureFileWriter.WriteAndProtect(path, "first");
        SecureFileWriter.WriteAndProtect(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
    }

    // ── WriteAndProtect with null path throws ──────────────────────────

    [Fact]
    public void WriteAndProtect_NullPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SecureFileWriter.WriteAndProtect(null!, "content"));
    }

    [Fact]
    public void WriteAndProtect_EmptyPath_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            SecureFileWriter.WriteAndProtect(string.Empty, "content"));
    }

    // ── WriteAndProtect with null content writes empty ─────────────────

    [Fact]
    public void WriteAndProtect_NullContent_WritesEmptyFile()
    {
        var path = TempFile();

        SecureFileWriter.WriteAndProtect(path, null!);

        Assert.True(File.Exists(path));
        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    // ── Written file content matches input ─────────────────────────────

    [Theory]
    [InlineData("simple text")]
    [InlineData("line1\nline2\nline3")]
    [InlineData("unicode: \u00e9\u00e0\u00fc\u00f1")]
    [InlineData("")]
    public void WriteAndProtect_ContentMatchesInput(string content)
    {
        var path = TempFile();

        SecureFileWriter.WriteAndProtect(path, content);

        Assert.Equal(content, File.ReadAllText(path));
    }

    // ── WriteAndProtect applies restrictive ACL ────────────────────────

    [Fact]
    public void WriteAndProtect_AppliesRestrictiveAcl()
    {
        var path = TempFile();

        SecureFileWriter.WriteAndProtect(path, "protected");

        AssertRestrictiveAcl(path);
    }

    [Fact]
    public void WriteAndProtect_PreExistingFileWithInheritedAcl_AppliesRestrictiveAcl()
    {
        string path = TempFile();
        File.WriteAllText(path, "stale-permissive");

        SecureFileWriter.WriteAndProtect(path, "secret");

        Assert.Equal("secret", File.ReadAllText(path));
        AssertRestrictiveAcl(path);
    }

    [Fact]
    public async Task WriteAndProtectAsync_PreExistingFileWithInheritedAcl_AppliesRestrictiveAcl()
    {
        string path = TempFile();
        File.WriteAllText(path, "stale-permissive");

        await SecureFileWriter.WriteAndProtectAsync(path, "secret");

        Assert.Equal("secret", File.ReadAllText(path));
        AssertRestrictiveAcl(path);
    }

    // ── UTF-8 without BOM ──────────────────────────────────────────────

    [Fact]
    public void WriteAndProtect_WritesUtf8WithoutBom()
    {
        var path = TempFile("nobom.txt");

        SecureFileWriter.WriteAndProtect(path, "test");

        var bytes = File.ReadAllBytes(path);
        // UTF-8 BOM is 0xEF, 0xBB, 0xBF — verify it is absent
        Assert.False(
            bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void CreateWriteAndProtect_CreatesExclusiveRestrictedFile()
    {
        string path = TempFile("installer.exe");

        using (FileStream stream = SecureFileWriter.CreateWriteAndProtect(path))
        {
            stream.Write("verified"u8);
            Assert.Throws<IOException>(() => File.WriteAllText(path, "attacker"));
        }

        Assert.Equal("verified", File.ReadAllText(path));
        AssertRestrictiveAcl(path);
    }

    [Fact]
    public void CreateRestrictedDirectory_DisablesInheritanceAndRestrictsIdentities()
    {
        string path = Path.Combine(_tempDir, "update-stage");

        SecureFileWriter.CreateRestrictedDirectory(path);

        DirectorySecurity acl = new DirectoryInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        AssertOnlyExpectedIdentities(acl.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier)));
    }

    private static void AssertRestrictiveAcl(string path)
    {
        FileInfo fileInfo = new(path);
        FileSecurity acl = fileInfo.GetAccessControl();

        Assert.True(acl.AreAccessRulesProtected);

        AuthorizationRuleCollection rules = acl.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));

        AssertOnlyExpectedIdentities(rules);
    }

    private static void AssertOnlyExpectedIdentities(AuthorizationRuleCollection rules)
    {
        HashSet<string> expectedIdentities = new(StringComparer.OrdinalIgnoreCase);
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            expectedIdentities.Add(currentUser.Value);
        }

        SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
        expectedIdentities.Add(administrators.Value);
        expectedIdentities.Add(system.Value);

        Assert.True(rules.Count > 0);
        foreach (FileSystemAccessRule rule in rules)
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            if (rule.IdentityReference is SecurityIdentifier securityIdentifier)
            {
                Assert.True(
                    expectedIdentities.Contains(securityIdentifier.Value),
                    $"Unexpected ACL identity: {securityIdentifier.Value}");
            }
        }
    }
}
