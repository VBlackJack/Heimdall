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
public sealed class SecureFileWriterAtomicTests : IDisposable
{
    private readonly string _tempDir;

    public SecureFileWriterAtomicTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Heimdall.Atomic." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private string TempFile(string name = "settings.json") => Path.Combine(_tempDir, name);

    private string[] StrayTemps() => Directory.GetFiles(_tempDir, "*.tmp*");

    [Fact]
    public async Task WriteAllTextAtomicAsync_CreatesFileWithContent()
    {
        var path = TempFile();

        await SecureFileWriter.WriteAllTextAtomicAsync(path, "config-content");

        Assert.True(File.Exists(path));
        Assert.Equal("config-content", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_AppliesRestrictiveAcl()
    {
        var path = TempFile();

        await SecureFileWriter.WriteAllTextAtomicAsync(path, "protected");

        AssertRestrictiveAcl(path);
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_OverwriteExisting_FullyReplacesAndStaysRestrictive()
    {
        var path = TempFile();
        await SecureFileWriter.WriteAllTextAtomicAsync(path, "first-content-that-is-quite-long");
        await SecureFileWriter.WriteAllTextAtomicAsync(path, "second");

        // Atomic replace: the new content is fully present, the old fully gone — never a mix.
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        AssertRestrictiveAcl(path);
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_OverwritePreExistingPermissiveFile_BecomesRestrictive()
    {
        var path = TempFile();
        await File.WriteAllTextAsync(path, "stale-permissive");

        await SecureFileWriter.WriteAllTextAtomicAsync(path, "secret");

        Assert.Equal("secret", await File.ReadAllTextAsync(path));
        AssertRestrictiveAcl(path);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("line1\nline2\nline3")]
    [InlineData("unicode: éàüñ 中文")]
    [InlineData("")]
    public async Task WriteAllTextAtomicAsync_RoundTrip_IdenticalContent(string content)
    {
        var path = TempFile();

        await SecureFileWriter.WriteAllTextAtomicAsync(path, content);

        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_WritesUtf8WithoutBom()
    {
        var path = TempFile("nobom.json");

        await SecureFileWriter.WriteAllTextAtomicAsync(path, "data");

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_Success_LeavesNoTempSibling()
    {
        var path = TempFile();

        await SecureFileWriter.WriteAllTextAtomicAsync(path, "content");

        Assert.Empty(StrayTemps());
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_RenameFails_OriginalIntact_TempCleanedUp()
    {
        var path = TempFile();
        await SecureFileWriter.WriteAllTextAtomicAsync(path, "ORIGINAL");

        // Hold the target open without delete-sharing so the atomic replace fails.
        using (var locking = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(
                () => SecureFileWriter.WriteAllTextAtomicAsync(path, "NEW-CONTENT"));

            // The failure is surfaced (fail-closed), not swallowed into the fallback.
            Assert.NotNull(error);
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error.GetType()}");

            // The temp is cleaned up and the original is untouched (no partial write).
            Assert.Empty(StrayTemps());
        }

        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_NullPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => SecureFileWriter.WriteAllTextAtomicAsync(null!, "content"));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_EmptyPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => SecureFileWriter.WriteAllTextAtomicAsync(string.Empty, "content"));
    }

    [Fact]
    public async Task WriteAllTextAtomicAsync_NullContent_WritesEmptyFile()
    {
        var path = TempFile();

        await SecureFileWriter.WriteAllTextAtomicAsync(path, null!);

        Assert.True(File.Exists(path));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
    }

    private static void AssertRestrictiveAcl(string path)
    {
        FileInfo fileInfo = new(path);
        FileSecurity acl = fileInfo.GetAccessControl();

        Assert.True(acl.AreAccessRulesProtected);

        HashSet<string> expectedIdentities = new(StringComparer.OrdinalIgnoreCase);
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            expectedIdentities.Add(currentUser.Value);
        }

        expectedIdentities.Add(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value);
        expectedIdentities.Add(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value);

        AuthorizationRuleCollection rules = acl.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            targetType: typeof(SecurityIdentifier));

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
