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
using System.Text;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests.Fakes;

internal sealed class FakeNotesStorage : INotesStorageService
{
    private readonly Dictionary<string, StoredNote> _notes = new(StringComparer.OrdinalIgnoreCase);
    private int _createSequence;

    public FakeNotesStorage(string notesRootPath, params FakeNoteSeed[] notes)
    {
        NotesRootPath = notesRootPath;
        foreach (var note in notes)
        {
            var filePath = BuildAbsolutePath(note.RelativePath);
            _notes[filePath] = new StoredNote(
                filePath,
                NormalizeRelativePath(note.RelativePath),
                note.Content,
                note.LastModifiedUtc ?? DateTime.UtcNow);
        }
    }

    public string NotesRootPath { get; }

    public Exception? SaveFailure { get; set; }

    /// <summary>The synchronous save reports a write lock it could not take.</summary>
    public bool SyncSaveLockTimesOut { get; set; }

    public Exception? ListFailure { get; set; }

    public IReadOnlyCollection<string> Paths => _notes.Keys.ToList();

    public void EnsureInitialized()
    {
    }

    public Task<IReadOnlyList<NoteListItem>> ListNotesAsync(
        string? searchText,
        NoteSortOrder sortOrder = NoteSortOrder.DateDescending,
        CancellationToken cancellationToken = default)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        IEnumerable<StoredNote> notes = _notes.Values;
        var normalizedSearch = searchText?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            notes = notes.Where(note =>
                note.Content.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || note.Title.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || note.RelativePath.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                || note.Summary.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase));
        }

        var items = notes
            .Select(note => new NoteListItem(
                note.FilePath,
                note.RelativePath,
                note.Title,
                note.Summary,
                note.Tags,
                note.LastModifiedUtc,
                note.LastModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
            .ToList();

        IEnumerable<NoteListItem> sorted = sortOrder switch
        {
            NoteSortOrder.DateAscending => items.OrderBy(x => x.LastModifiedUtc).ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase),
            NoteSortOrder.NameAscending => items.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.LastModifiedUtc),
            NoteSortOrder.NameDescending => items.OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.LastModifiedUtc),
            _ => items.OrderByDescending(x => x.LastModifiedUtc).ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
        };

        return Task.FromResult<IReadOnlyList<NoteListItem>>(sorted.ToList());
    }

    public Task<string> ResolveOrCreateNoteAsync(string noteReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalized = StripMarkdownExtension(noteReference).Trim();
        var existing = _notes.Values.FirstOrDefault(note =>
            string.Equals(note.Title, normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(note.RelativePath), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(StripMarkdownExtension(note.RelativePath), normalized.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return Task.FromResult(existing.FilePath);
        }

        var slug = Slugify(normalized);
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = $"note-{++_createSequence}";
        }

        var filePath = BuildAbsolutePath($"{slug}.md");
        _notes[filePath] = new StoredNote(filePath, $"{slug}.md", $"# {normalized}\n", DateTime.UtcNow);
        return Task.FromResult(filePath);
    }

    public Task<string> CreateNoteAsync(
        NoteTemplateKind templateKind,
        ToolContext? context,
        LocalizationManager? localizer = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stem = templateKind switch
        {
            NoteTemplateKind.Daily => "daily-note",
            NoteTemplateKind.Incident => "incident-note",
            NoteTemplateKind.Procedure => "procedure-note",
            _ => "new-note"
        };

        var filePath = GetUniquePath(BuildAbsolutePath($"{stem}.md"));
        var title = Path.GetFileNameWithoutExtension(filePath);
        _notes[filePath] = new StoredNote(
            filePath,
            NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, filePath)),
            $"# {title}\n",
            DateTime.UtcNow);
        return Task.FromResult(filePath);
    }

    public string CreateFolder(string folderName, string? parentFolder = null)
    {
        var parent = parentFolder ?? NotesRootPath;
        return Path.Combine(parent, folderName);
    }

    public Task<string> MoveNoteToFolderAsync(string filePath, string targetFolder, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_notes.Remove(filePath, out var note))
        {
            throw new FileNotFoundException(filePath);
        }

        var newPath = GetUniquePath(Path.Combine(targetFolder, Path.GetFileName(filePath)));
        _notes[newPath] = note with
        {
            FilePath = newPath,
            RelativePath = NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, newPath)),
            LastModifiedUtc = DateTime.UtcNow
        };
        return Task.FromResult(newPath);
    }

    public Task<string> LoadNoteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_notes.TryGetValue(filePath, out var note) ? note.Content : string.Empty);
    }

    public Task SaveNoteAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveInternal(filePath, content);
        return Task.CompletedTask;
    }

    public bool SaveNote(string filePath, string content)
    {
        if (SyncSaveLockTimesOut)
        {
            return false;
        }

        SaveInternal(filePath, content);
        return true;
    }

    public Task DeleteNoteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _notes.Remove(filePath);
        return Task.CompletedTask;
    }

    public Task<string> RenameNoteAsync(string filePath, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_notes.Remove(filePath, out var note))
        {
            throw new FileNotFoundException(filePath);
        }

        var sanitized = StripMarkdownExtension(newName).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException("Invalid file name.", nameof(newName));
        }

        var directory = Path.GetDirectoryName(filePath) ?? NotesRootPath;
        var newPath = GetUniquePath(Path.Combine(directory, $"{Slugify(sanitized)}.md"));
        _notes[newPath] = note with
        {
            FilePath = newPath,
            RelativePath = NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, newPath)),
            Content = UpdatePrimaryHeading(note.Content, sanitized),
            LastModifiedUtc = DateTime.UtcNow
        };
        return Task.FromResult(newPath);
    }

    public Task<string> DuplicateNoteAsync(string filePath, string duplicateLabel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_notes.TryGetValue(filePath, out var note))
        {
            throw new FileNotFoundException(filePath);
        }

        var suffix = string.IsNullOrWhiteSpace(duplicateLabel) ? "copy" : Slugify(duplicateLabel);
        var directory = Path.GetDirectoryName(filePath) ?? NotesRootPath;
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var newPath = GetUniquePath(Path.Combine(directory, $"{baseName}-{suffix}.md"));
        _notes[newPath] = note with
        {
            FilePath = newPath,
            RelativePath = NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, newPath)),
            Content = UpdatePrimaryHeading(note.Content, $"{note.Title} {duplicateLabel}".Trim()),
            LastModifiedUtc = DateTime.UtcNow
        };
        return Task.FromResult(newPath);
    }

    public string GetRelativePath(string filePath)
        => NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, filePath));

    public bool IsUnderNotesRoot(string filePath)
    {
        var fullRoot = Path.GetFullPath(NotesRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullFile = Path.GetFullPath(filePath);
        return fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullFile, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveInternal(string filePath, string content)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        var relativePath = NormalizeRelativePath(Path.GetRelativePath(NotesRootPath, filePath));
        _notes[filePath] = new StoredNote(filePath, relativePath, content ?? string.Empty, DateTime.UtcNow);
    }

    private string BuildAbsolutePath(string relativePath)
        => Path.Combine(NotesRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string GetUniquePath(string targetPath)
    {
        if (!_notes.ContainsKey(targetPath))
        {
            return targetPath;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? NotesRootPath;
        var name = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name}-{index}{extension}");
            if (!_notes.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static string StripMarkdownExtension(string value)
        => value.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? value[..^3]
            : value;

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string ExtractTitle(string content, string filePath)
    {
        foreach (var rawLine in (content ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string ExtractSummary(string content)
    {
        foreach (var rawLine in (content ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            return line;
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ExtractTags(string content)
    {
        var tags = content
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith('#') && token.Length > 1)
            .Select(token => token.TrimStart('#').TrimEnd('.', ',', ';', ':'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tags;
    }

    private static string UpdatePrimaryHeading(string content, string title)
    {
        var lines = (content ?? string.Empty).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].TrimStart().StartsWith("# ", StringComparison.Ordinal))
            {
                lines[index] = $"# {title}";
                return string.Join('\n', lines);
            }
        }

        return $"# {title}\n{content}".TrimEnd();
    }

    private sealed record StoredNote(
        string FilePath,
        string RelativePath,
        string Content,
        DateTime LastModifiedUtc)
    {
        public string Title => ExtractTitle(Content, FilePath);
        public string Summary => ExtractSummary(Content);
        public IReadOnlyList<string> Tags => ExtractTags(Content);
    }
}

internal sealed record FakeNoteSeed(
    string RelativePath,
    string Content,
    DateTime? LastModifiedUtc = null);
