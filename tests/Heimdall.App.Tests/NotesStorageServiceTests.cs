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
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public class NotesStorageServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly NotesStorageService _service;

    public NotesStorageServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"heimdall-notes-test-{Guid.NewGuid():N}");
        _service = new NotesStorageService(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); }
        catch { /* test cleanup */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EnsureInitialized_CreatesDirectory()
    {
        _service.EnsureInitialized();
        Assert.True(Directory.Exists(_testDir));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "test.md");

        await _service.SaveNoteAsync(path, "# Test\n\nContent");
        var content = await _service.LoadNoteAsync(path);

        Assert.Equal("# Test\n\nContent", content);
    }

    [Fact]
    public async Task LoadNote_NonExistent_ReturnsEmpty()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "nonexistent.md");

        var content = await _service.LoadNoteAsync(path);

        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public async Task SaveNote_CreatesSubdirectories()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "sub", "dir", "test.md");

        await _service.SaveNoteAsync(path, "content");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DeleteNote_RemovesFile()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "to-delete.md");
        await _service.SaveNoteAsync(path, "content");

        await _service.DeleteNoteAsync(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeleteNote_CleansEmptyDirectories()
    {
        _service.EnsureInitialized();
        var subDir = Path.Combine(_testDir, "sub");
        var path = Path.Combine(subDir, "only-file.md");
        await _service.SaveNoteAsync(path, "content");

        await _service.DeleteNoteAsync(path);

        Assert.False(Directory.Exists(subDir));
    }

    [Fact]
    public async Task DeleteNote_PreservesNonEmptyDirectories()
    {
        _service.EnsureInitialized();
        var subDir = Path.Combine(_testDir, "sub");
        var path1 = Path.Combine(subDir, "file1.md");
        var path2 = Path.Combine(subDir, "file2.md");
        await _service.SaveNoteAsync(path1, "content1");
        await _service.SaveNoteAsync(path2, "content2");

        await _service.DeleteNoteAsync(path1);

        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(path2));
    }

    [Fact]
    public async Task ListNotes_ReturnsAllMarkdownFiles()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note1.md"), "# Note 1");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note2.md"), "# Note 2");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "readme.txt"), "Not a note");

        var notes = await _service.ListNotesAsync(null);

        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public async Task ListNotes_SearchFiltersResults()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note1.md"), "# Server Alpha\n\nSSH config");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note2.md"), "# Server Beta\n\nRDP setup");

        var notes = await _service.ListNotesAsync("Alpha");

        Assert.Single(notes);
        Assert.Equal("Server Alpha", notes[0].Title);
    }

    [Fact]
    public async Task ListNotes_SearchMatchesContent()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note1.md"), "# Note\n\nContains unique-keyword here");

        var notes = await _service.ListNotesAsync("unique-keyword");

        Assert.Single(notes);
    }

    [Fact]
    public async Task ListNotes_SearchForANoteLink_FindsTheNoteWhereTheEditorEscapedItsBrackets()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "escaped.md"), "# Escaped\n\nSee \\[\\[Procedure]] first");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "plain.md"), "# Plain\n\nSee [[Procedure]] first");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "other.md"), "# Other\n\nSee [[Network]] first");

        var notes = await _service.ListNotesAsync("[[Procedure]]");

        Assert.Equal(["Escaped", "Plain"], notes.Select(n => n.Title).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ListNotes_ExtractsTitle()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "note.md"), "# My Title\n\nBody");

        var notes = await _service.ListNotesAsync(null);

        Assert.Equal("My Title", notes[0].Title);
    }

    [Fact]
    public async Task ListNotes_FallsBackToFileNameForTitle()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "no-heading.md"), "Just body text");

        var notes = await _service.ListNotesAsync(null);

        Assert.Equal("no-heading", notes[0].Title);
    }

    [Fact]
    public async Task ListNotes_SortByDateDescending()
    {
        _service.EnsureInitialized();
        var path1 = Path.Combine(_testDir, "old.md");
        var path2 = Path.Combine(_testDir, "new.md");
        await _service.SaveNoteAsync(path1, "# Old");
        await Task.Delay(50);
        await _service.SaveNoteAsync(path2, "# New");

        var notes = await _service.ListNotesAsync(null, NoteSortOrder.DateDescending);

        Assert.Equal("New", notes[0].Title);
        Assert.Equal("Old", notes[1].Title);
    }

    [Fact]
    public async Task ListNotes_SortByNameAscending()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "b.md"), "# Bravo");
        await _service.SaveNoteAsync(Path.Combine(_testDir, "a.md"), "# Alpha");

        var notes = await _service.ListNotesAsync(null, NoteSortOrder.NameAscending);

        Assert.Equal("Alpha", notes[0].Title);
        Assert.Equal("Bravo", notes[1].Title);
    }

    [Fact]
    public async Task CreateNote_ReturnsNewFilePath()
    {
        _service.EnsureInitialized();

        var path = await _service.CreateNoteAsync(NoteTemplateKind.Blank, null);

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("# ToolNotesTplWorkingNote", content);
    }

    [Fact]
    public async Task CreateNote_Daily_IdempotentSameDay()
    {
        _service.EnsureInitialized();

        var path1 = await _service.CreateNoteAsync(NoteTemplateKind.Daily, null);
        var path2 = await _service.CreateNoteAsync(NoteTemplateKind.Daily, null);

        Assert.Equal(path1, path2);
    }

    [Fact]
    public async Task RenameNote_MovesFile()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "original.md");
        await _service.SaveNoteAsync(path, "# Original");

        var newPath = await _service.RenameNoteAsync(path, "renamed.md");

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(newPath));
        Assert.Contains("renamed", Path.GetFileName(newPath));
        Assert.Equal("# renamed", await File.ReadAllTextAsync(newPath));
    }

    [Fact]
    public async Task RenameNote_AddsExtensionIfMissing()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "test.md");
        await _service.SaveNoteAsync(path, "content");

        var newPath = await _service.RenameNoteAsync(path, "newname");

        Assert.EndsWith(".md", newPath);
    }

    [Fact]
    public async Task RenameNote_SameNameNoOp()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "test.md");
        await _service.SaveNoteAsync(path, "content");

        var newPath = await _service.RenameNoteAsync(path, "test.md");

        Assert.Equal(path, newPath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DuplicateNote_CreatesNewFile()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "original.md");
        await _service.SaveNoteAsync(path, "# Original Content");

        var copyPath = await _service.DuplicateNoteAsync(path);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(copyPath));
        Assert.NotEqual(path, copyPath);
        Assert.Contains("-copy", Path.GetFileName(copyPath));
        var copyContent = await File.ReadAllTextAsync(copyPath);
        Assert.Equal("# Original Content (Copy)", copyContent);
    }

    [Fact]
    public async Task DuplicateNote_UsesCustomDuplicateLabelForTitleAndFileName()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "incident.md");
        await _service.SaveNoteAsync(path, "# Incident");

        var copyPath = await _service.DuplicateNoteAsync(path, "Copie");

        Assert.Contains("-copie", Path.GetFileName(copyPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("# Incident (Copie)", await File.ReadAllTextAsync(copyPath));
    }

    [Fact]
    public async Task RenameNote_WithoutHeadingPreservesBodyContent()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "body-only.md");
        await _service.SaveNoteAsync(path, "body only");

        var newPath = await _service.RenameNoteAsync(path, "renamed");

        Assert.Equal("body only", await File.ReadAllTextAsync(newPath));
    }

    [Fact]
    public void GetRelativePath_NormalizesSlashes()
    {
        var result = _service.GetRelativePath(Path.Combine(_testDir, "sub", "file.md"));
        Assert.Equal("sub/file.md", result);
    }

    [Fact]
    public void IsUnderNotesRoot_TrueForChildPath()
    {
        Assert.True(_service.IsUnderNotesRoot(Path.Combine(_testDir, "file.md")));
    }

    [Fact]
    public void IsUnderNotesRoot_FalseForParentPath()
    {
        Assert.False(_service.IsUnderNotesRoot(Path.Combine(_testDir, "..", "escape.md")));
    }

    [Fact]
    public void IsUnderNotesRoot_FalseForSiblingWithPrefix()
    {
        Assert.False(_service.IsUnderNotesRoot(_testDir + "-evil"));
    }

    [Fact]
    public void SaveNoteSync_RoundTrip()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "sync-test.md");

        _service.SaveNote(path, "# Sync Save\n\nContent");

        Assert.True(File.Exists(path));
        Assert.Equal("# Sync Save\n\nContent", File.ReadAllText(path));
    }

    [Fact]
    public void SaveNoteSync_WhenAnotherWriteHoldsTheLock_ReportsNotSaved()
    {
        using var writeLock = new SemaphoreSlim(1, 1);
        var service = new NotesStorageService(_testDir, writeLock, TimeSpan.FromMilliseconds(50));
        service.EnsureInitialized();
        var path = Path.Combine(_testDir, "held.md");

        // Held the way a slow asynchronous save holds it.
        writeLock.Wait();
        bool saved;
        try
        {
            saved = service.SaveNote(path, "unsaved");
        }
        finally
        {
            writeLock.Release();
        }

        Assert.False(saved);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SaveNoteSync_ReportsSaved()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "reported.md");

        Assert.True(_service.SaveNote(path, "content"));
    }

    [Fact]
    public void SaveNoteSync_CreatesSubdirectories()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "sub", "sync", "test.md");

        _service.SaveNote(path, "content");

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SaveNoteSync_PathTraversal_Throws()
    {
        _service.EnsureInitialized();
        var escapePath = Path.Combine(_testDir, "..", "escaped.md");

        Assert.Throws<UnauthorizedAccessException>(
            () => _service.SaveNote(escapePath, "malicious"));
    }

    [Fact]
    public async Task PathTraversal_SaveThrows()
    {
        _service.EnsureInitialized();
        var escapePath = Path.Combine(_testDir, "..", "escaped.md");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.SaveNoteAsync(escapePath, "malicious"));
    }

    [Fact]
    public async Task PathTraversal_LoadThrows()
    {
        _service.EnsureInitialized();
        var escapePath = Path.Combine(_testDir, "..", "escaped.md");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.LoadNoteAsync(escapePath));
    }

    [Fact]
    public async Task PathTraversal_DeleteThrows()
    {
        _service.EnsureInitialized();
        var escapePath = Path.Combine(_testDir, "..", "escaped.md");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _service.DeleteNoteAsync(escapePath));
    }

    [Fact]
    public async Task RenameNote_InvalidCharacters_Sanitized()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "test.md");
        await _service.SaveNoteAsync(path, "content");

        var newPath = await _service.RenameNoteAsync(path, "my:note<1>");

        Assert.True(File.Exists(newPath));
        var fileName = Path.GetFileName(newPath);
        Assert.DoesNotContain(":", fileName);
        Assert.DoesNotContain("<", fileName);
        Assert.DoesNotContain(">", fileName);
    }

    [Fact]
    public async Task RenameNote_TraversalAttempt_Throws()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "test.md");
        await _service.SaveNoteAsync(path, "content");

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.RenameNoteAsync(path, ".."));
    }

    // ── Inter-note links ─────────────────────────────────────────────

    [Fact]
    public async Task FindNotePath_ByTitle_ReturnsMatch()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "my-note.md");
        await _service.SaveNoteAsync(path, "# Server Setup\n\nContent");

        var found = await _service.FindNotePathAsync("Server Setup");

        Assert.NotNull(found);
        Assert.Equal(path, found);
    }

    [Fact]
    public async Task FindNotePath_ByFileName_ReturnsMatch()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "my-note.md");
        await _service.SaveNoteAsync(path, "No heading here");

        var found = await _service.FindNotePathAsync("my-note");

        Assert.NotNull(found);
        Assert.Equal(path, found);
    }

    [Fact]
    public async Task FindNotePath_BySlugifiedName_ReturnsMatch()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "server-setup.md");
        await _service.SaveNoteAsync(path, "# Server Setup\n\nContent");

        var found = await _service.FindNotePathAsync("Server Setup");

        Assert.NotNull(found);
    }

    [Fact]
    public async Task FindNotePath_ByRelativePath_ReturnsMatch()
    {
        _service.EnsureInitialized();
        var subDir = Path.Combine(_testDir, "daily");
        Directory.CreateDirectory(subDir);
        var path = Path.Combine(subDir, "2026-03-24.md");
        await _service.SaveNoteAsync(path, "# Daily");

        var found = await _service.FindNotePathAsync("daily/2026-03-24");

        Assert.NotNull(found);
        Assert.Equal(path, found);
    }

    [Fact]
    public async Task FindNotePath_NoMatch_ReturnsNull()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(Path.Combine(_testDir, "existing.md"), "# Existing");

        var found = await _service.FindNotePathAsync("nonexistent");

        Assert.Null(found);
    }

    [Fact]
    public async Task ResolveOrCreate_ExistingNote_ReturnsExistingPath()
    {
        _service.EnsureInitialized();
        var path = Path.Combine(_testDir, "known.md");
        await _service.SaveNoteAsync(path, "# Known Note\n\nBody");

        var resolved = await _service.ResolveOrCreateNoteAsync("Known Note");

        Assert.Equal(path, resolved);
    }

    [Fact]
    public async Task ResolveOrCreate_NewNote_CreatesFile()
    {
        _service.EnsureInitialized();

        var resolved = await _service.ResolveOrCreateNoteAsync("Brand New Note");

        Assert.True(File.Exists(resolved));
        var content = await File.ReadAllTextAsync(resolved);
        Assert.Contains("# Brand New Note", content);
        Assert.EndsWith(".md", resolved);
    }

    [Fact]
    public async Task ResolveOrCreate_SlugifiesFileName()
    {
        _service.EnsureInitialized();

        var resolved = await _service.ResolveOrCreateNoteAsync("My Server Notes");

        var fileName = Path.GetFileNameWithoutExtension(resolved);
        Assert.Equal("my-server-notes", fileName);
    }

    // ── Tags ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNotes_ExtractsTags()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "tagged.md"),
            "# Tagged Note\n\n> tags: infra, prod\n\nContent");

        var notes = await _service.ListNotesAsync(null);

        Assert.Single(notes);
        Assert.Equal(2, notes[0].Tags.Count);
        Assert.Contains("infra", notes[0].Tags);
        Assert.Contains("prod", notes[0].Tags);
    }

    [Fact]
    public async Task ListNotes_NoTags_ReturnsEmptyList()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "untagged.md"),
            "# Untagged\n\nNo tags here");

        var notes = await _service.ListNotesAsync(null);

        Assert.Single(notes);
        Assert.Empty(notes[0].Tags);
    }

    [Fact]
    public async Task ListNotes_DuplicateTags_Deduplicated()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "dupes.md"),
            "# Dupes\n\n> tags: infra, INFRA, prod\n\nContent");

        var notes = await _service.ListNotesAsync(null);

        Assert.Equal(2, notes[0].Tags.Count);
    }

    [Fact]
    public async Task FindNotePath_TitlePriorityOverFileName()
    {
        _service.EnsureInitialized();
        // file named "wrong.md" but with title "Server Setup"
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "wrong.md"),
            "# Server Setup\n\nContent");
        // file named "server-setup.md" but with different title
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "server-setup.md"),
            "# Different Title\n\nContent");

        var found = await _service.FindNotePathAsync("Server Setup");

        Assert.NotNull(found);
        Assert.EndsWith("wrong.md", found);
    }

    [Fact]
    public async Task FindNotePath_AccentInsensitiveTitle()
    {
        _service.EnsureInitialized();
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "procedure.md"),
            "# Proc\u00e9dure\n\nContent with accents");

        var found = await _service.FindNotePathAsync("Procedure");

        Assert.NotNull(found);
        Assert.EndsWith("procedure.md", found);
    }

    [Fact]
    public async Task FindNotePath_AccentedSlugMatchesAsciiReference()
    {
        _service.EnsureInitialized();
        // File created with accented slug (simulating fr locale template)
        await _service.SaveNoteAsync(
            Path.Combine(_testDir, "resume-incident.md"),
            "# R\u00e9sum\u00e9 Incident\n\nContent");

        var found = await _service.FindNotePathAsync("Resume Incident");

        Assert.NotNull(found);
    }
}
