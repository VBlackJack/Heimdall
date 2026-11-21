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
using System.Security.Principal;
using System.Text;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Notes are written atomically and keep the permissions of the folder they live in, and notes
/// created at the same time never share a path.
/// </summary>
public sealed class NotesStorageAtomicWriteTests : IDisposable
{
    private const int ConcurrentCreations = 8;
    private static readonly TimeSpan SyncWait = TimeSpan.FromSeconds(2);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"heimdall-notes-atomic-{Guid.NewGuid():N}");
    private readonly NotesStorageService _service;

    public NotesStorageAtomicWriteTests()
    {
        _service = new NotesStorageService(_root);
        _service.EnsureInitialized();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveNoteAsync_WritesNoBomAndLeavesNoTempFile()
    {
        string path = Path.Combine(_root, "plain.md");

        await _service.SaveNoteAsync(path, "# Caf\u00e9\n");

        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.AsSpan().StartsWith(Utf8Bom));
        Assert.Equal("# Caf\u00e9\n", Encoding.UTF8.GetString(bytes));
        AssertNoTempFiles();
    }

    [Fact]
    public void SaveNote_WritesNoBomAndLeavesNoTempFile()
    {
        string path = Path.Combine(_root, "plain-sync.md");

        Assert.True(_service.SaveNote(path, "# Sync\n"));

        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(Utf8Bom));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task SaveNoteAsync_OverAnExistingNote_ReplacesTheContent()
    {
        string path = Path.Combine(_root, "existing.md");
        await _service.SaveNoteAsync(path, "first");

        await _service.SaveNoteAsync(path, "second");

        Assert.Equal("second", await File.ReadAllTextAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Saves_KeepTheInheritedPermissionsOfTheFolder()
    {
        string asyncPath = Path.Combine(_root, "acl-async.md");
        string syncPath = Path.Combine(_root, "acl-sync.md");
        string renamePath = Path.Combine(_root, "acl-old.md");

        await _service.SaveNoteAsync(asyncPath, "# A\n");
        Assert.False(AreAccessRulesProtected(asyncPath));

        Assert.True(_service.SaveNote(syncPath, "# S\n"));
        Assert.False(AreAccessRulesProtected(syncPath));

        await _service.SaveNoteAsync(renamePath, "# Old\n");
        string renamed = await _service.RenameNoteAsync(renamePath, "acl-new");
        Assert.StartsWith("# acl-new", await File.ReadAllTextAsync(renamed), StringComparison.Ordinal);
        Assert.False(AreAccessRulesProtected(renamed));
    }

    [Fact]
    public async Task Save_OverAnExistingNote_KeepsAnExplicitAccessRuleAddedToIt()
    {
        string path = Path.Combine(_root, "shared.md");
        await _service.SaveNoteAsync(path, "first");
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var info = new FileInfo(path);
        FileSecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);

        await _service.SaveNoteAsync(path, "second");

        FileSecurity after = new FileInfo(path).GetAccessControl();
        Assert.Contains(
            after.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Allow);
        Assert.Equal("second", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Save_WhileAReaderHoldsTheFileOpenWithSharing_Succeeds()
    {
        string path = Path.Combine(_root, "held.md");
        await _service.SaveNoteAsync(path, "first");

        using (var reader = new FileStream(
                   path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            await _service.SaveNoteAsync(path, "second");
            Assert.True(_service.SaveNote(path, "third"));
        }

        Assert.Equal("third", await File.ReadAllTextAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task ConcurrentDuplicates_GetDistinctPathsWithIntactContent()
    {
        string source = Path.Combine(_root, "source.md");
        await _service.SaveNoteAsync(source, "# Source\nbody\n");

        string[] copies = await Task.WhenAll(Enumerable.Range(0, ConcurrentCreations)
            .Select(_ => Task.Run(() => _service.DuplicateNoteAsync(source, "Copy"))));

        Assert.Equal(ConcurrentCreations, copies.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string copy in copies)
        {
            Assert.Equal("# Source (Copy)\nbody", (await File.ReadAllTextAsync(copy)).TrimEnd('\n'));
        }

        AssertNoTempFiles();
    }

    [Fact]
    public async Task FailureWhileStaging_LeavesTheOriginalIntactAndNoTempFile()
    {
        string path = Path.Combine(_root, "keep.md");
        await _service.SaveNoteAsync(path, "original");
        var failing = new NotesStorageService(
            _root,
            new SemaphoreSlim(1, 1),
            SyncWait,
            new NoteFileWriter(_ => throw new IOException("staging failed")));

        await Assert.ThrowsAsync<IOException>(() => failing.SaveNoteAsync(path, "replacement"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        AssertNoTempFiles();

        Assert.Throws<IOException>(() => failing.SaveNote(path, "replacement"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Save_WhileAReaderHoldsTheFileWithoutDeleteSharing_FallsBackAndWritesTheContent()
    {
        string path = Path.Combine(_root, "no-delete-share.md");
        await _service.SaveNoteAsync(path, "first");

        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await _service.SaveNoteAsync(path, "second");
            Assert.Equal("second", ReadSharedText(reader));
            Assert.True(_service.SaveNote(path, "third"));
        }

        Assert.Equal("third", await File.ReadAllTextAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task StagedFile_IsHiddenAndTemporaryWhileStaged_AndThePublishedNoteIsNeither()
    {
        string path = Path.Combine(_root, "attrs.md");
        FileAttributes? staged = null;
        var writer = new NoteFileWriter(temp => staged = File.GetAttributes(temp));
        var service = new NotesStorageService(_root, new SemaphoreSlim(1, 1), SyncWait, writer);

        await service.SaveNoteAsync(path, "content");

        Assert.NotNull(staged);
        Assert.True(staged!.Value.HasFlag(FileAttributes.Hidden));
        Assert.True(staged.Value.HasFlag(FileAttributes.Temporary));
        FileAttributes published = File.GetAttributes(path);
        Assert.False(published.HasFlag(FileAttributes.Hidden));
        Assert.False(published.HasFlag(FileAttributes.Temporary));
    }

    [Fact]
    public async Task FirstListing_DeletesOldOrphanedStagedFiles_AndKeepsRecentOnes()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        string old = Path.Combine(sub, "a.md.0123abcd.tmp");
        string recent = Path.Combine(_root, "b.md.4567ef01.tmp");
        string unrelated = Path.Combine(_root, "keep.tmp");
        foreach (string file in new[] { old, recent, unrelated })
        {
            await File.WriteAllTextAsync(file, "x");
        }

        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(2));
        var service = new NotesStorageService(_root);

        await service.ListNotesAsync(null);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    private static string ReadSharedText(FileStream reader)
    {
        reader.Position = 0;
        using var streamReader = new StreamReader(reader, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return streamReader.ReadToEnd();
    }

    private void AssertNoTempFiles()
        => Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));

    private static bool AreAccessRulesProtected(string path)
        => new FileInfo(path).GetAccessControl().AreAccessRulesProtected;
}
