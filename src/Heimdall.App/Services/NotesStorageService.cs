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
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

public enum NoteSortOrder
{
    DateDescending,
    DateAscending,
    NameAscending,
    NameDescending
}

public sealed record NoteListItem(
    string FilePath,
    string RelativePath,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    DateTime LastModifiedUtc,
    string LastModifiedDisplay);

public sealed class NoteTreeNode
{
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string? FilePath { get; init; }
    public string? FolderPath { get; init; }
    public NoteListItem? Note { get; init; }
    public List<NoteTreeNode> Children { get; } = new();
    public bool IsFolder => FilePath is null;
    public string DisplayTitle => Note?.Title ?? Name;
    public string DisplaySummary => Note?.Summary ?? string.Empty;
    public string DisplayMeta => Note is null
        ? string.Empty
        : $"{Note.LastModifiedDisplay} | {Name}";
    public string DisplayTags => Note?.Tags.Count > 0
        ? string.Join("   ", Note.Tags.Select(tag => $"#{tag}"))
        : string.Empty;

    /// <summary>
    /// What the row shows besides its title, as plain comma-separated text for a screen reader:
    /// date, file name and tags, without the pipe and the hash marks that only read well on screen.
    /// Carries no localized word, so the storage model stays free of the localizer; the word that
    /// says "note" or "folder" is added by the view, which re-evaluates it on a locale change.
    /// </summary>
    public string DisplayDetail
    {
        get
        {
            if (Note is null)
            {
                return string.Empty;
            }

            string[] parts =
            [
                Note.LastModifiedDisplay,
                Name,
                string.Join(", ", Note.Tags)
            ];
            return string.Join(", ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }
    }

    public static List<NoteTreeNode> BuildTree(IReadOnlyList<NoteListItem> notes, string notesRootPath)
    {
        var root = new NoteTreeNode { FolderPath = notesRootPath };

        foreach (var note in notes)
        {
            var segments = note.RelativePath.Split('/');
            var current = root;
            var currentPath = notesRootPath;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                currentPath = Path.Combine(currentPath, segments[i]);
                var folder = current.Children.FirstOrDefault(
                    c => c.IsFolder && string.Equals(c.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                if (folder is null)
                {
                    folder = new NoteTreeNode { Name = segments[i], Icon = "\U0001F4C1 ", FolderPath = currentPath };
                    current.Children.Add(folder);
                }
                current = folder;
            }

            var fileName = segments[^1];
            current.Children.Add(new NoteTreeNode
            {
                Name = fileName,
                Icon = "",
                FilePath = note.FilePath,
                Note = note
            });
        }

        // Also add empty folders that exist on disk
        AddEmptyFolders(root, notesRootPath);
        SortChildren(root);
        return root.Children;
    }

    private static void AddEmptyFolders(NoteTreeNode parent, string folderPath)
    {
        if (!Directory.Exists(folderPath)) return;

        foreach (var subDir in Directory.EnumerateDirectories(folderPath))
        {
            var dirName = Path.GetFileName(subDir);
            var existing = parent.Children.FirstOrDefault(
                c => c.IsFolder && string.Equals(c.Name, dirName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new NoteTreeNode { Name = dirName, Icon = "\U0001F4C1 ", FolderPath = subDir };
                parent.Children.Add(existing);
            }

            AddEmptyFolders(existing, subDir);
        }
    }

    private static void SortChildren(NoteTreeNode node)
    {
        node.Children.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var child in node.Children.Where(c => c.IsFolder))
        {
            SortChildren(child);
        }
    }
}

public sealed class NotesStorageService : INotesStorageService
{
    private const string EscapableBracket = "[";
    private const string EscapedBracket = "\\[";

    private sealed record NoteLookupCandidate(
        string FilePath,
        string Title,
        string FileNameWithoutExtension,
        string RelativePathWithoutExtension);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>How long the synchronous save waits for a write already in progress.</summary>
    private static readonly TimeSpan SyncSaveLockWait = TimeSpan.FromSeconds(2);

    private readonly string _notesRootPath;
    private readonly SemaphoreSlim _writeLock;
    private readonly TimeSpan _syncSaveLockWait;

    public NotesStorageService(string notesRootPath)
        : this(notesRootPath, new SemaphoreSlim(1, 1), SyncSaveLockWait)
    {
    }

    /// <summary>
    /// Takes the write lock and the synchronous wait from the caller, so a test can hold
    /// the lock the way a slow asynchronous save does.
    /// </summary>
    internal NotesStorageService(string notesRootPath, SemaphoreSlim writeLock, TimeSpan syncSaveLockWait)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRootPath);
        ArgumentNullException.ThrowIfNull(writeLock);
        _notesRootPath = notesRootPath;
        _writeLock = writeLock;
        _syncSaveLockWait = syncSaveLockWait;
    }

    public string NotesRootPath => _notesRootPath;

    public void EnsureInitialized() => Directory.CreateDirectory(_notesRootPath);

    public Task<IReadOnlyList<NoteListItem>> ListNotesAsync(
        string? searchText,
        NoteSortOrder sortOrder = NoteSortOrder.DateDescending,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<NoteListItem>>(() =>
        {
            EnsureInitialized();

            var normalizedSearch = searchText?.Trim();
            var results = new List<NoteListItem>();

            foreach (var filePath in Directory.EnumerateFiles(_notesRootPath, "*.md", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var content = File.ReadAllText(filePath, Utf8NoBom);
                var title = ExtractTitle(filePath, content);
                var relativePath = GetRelativePath(filePath);

                if (!string.IsNullOrWhiteSpace(normalizedSearch)
                    && !ContentContainsSearch(content, normalizedSearch)
                    && title.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) < 0
                    && relativePath.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                results.Add(new NoteListItem(
                    filePath,
                    relativePath,
                    title,
                    ExtractSummary(content),
                    ExtractTags(content),
                    File.GetLastWriteTimeUtc(filePath),
                    File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm")));
            }

            IEnumerable<NoteListItem> sorted = sortOrder switch
            {
                NoteSortOrder.DateAscending => results
                    .OrderBy(n => n.LastModifiedUtc)
                    .ThenBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase),
                NoteSortOrder.NameAscending => results
                    .OrderBy(n => n.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(n => n.LastModifiedUtc),
                NoteSortOrder.NameDescending => results
                    .OrderByDescending(n => n.Title, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(n => n.LastModifiedUtc),
                _ => results
                    .OrderByDescending(n => n.LastModifiedUtc)
                    .ThenBy(n => n.RelativePath, StringComparer.OrdinalIgnoreCase),
            };

            return sorted.ToList();
        }, cancellationToken);
    }

    public Task<string?> FindNotePathAsync(string noteReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteReference);

        return Task.Run<string?>(() =>
        {
            EnsureInitialized();

            var titleReference = NormalizeNoteReference(noteReference);
            var pathReference = NormalizeRelativeReference(StripMarkdownExtension(titleReference));
            var fileNameReference = GetReferenceFileName(pathReference);
            var slugifiedFileNameReference = NotesTemplateFactory.SlugifyValue(fileNameReference);
            var slugifiedRelativeReference = SlugifyRelativeReference(pathReference);

            var candidates = EnumerateLookupCandidates(cancellationToken)
                .OrderBy(candidate => candidate.RelativePathWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Accent-insensitive title match (e.g., [[Procedure]] finds "Procédure")
            return candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Title, titleReference, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        NotesTemplateFactory.RemoveDiacritics(candidate.Title),
                        NotesTemplateFactory.RemoveDiacritics(titleReference),
                        StringComparison.OrdinalIgnoreCase))?.FilePath
                ?? candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.FileNameWithoutExtension, fileNameReference, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.FileNameWithoutExtension, slugifiedFileNameReference, StringComparison.OrdinalIgnoreCase))?.FilePath
                ?? candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.RelativePathWithoutExtension, pathReference, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.RelativePathWithoutExtension, slugifiedRelativeReference, StringComparison.OrdinalIgnoreCase))?.FilePath;
        }, cancellationToken);
    }

    public async Task<string> ResolveOrCreateNoteAsync(string noteReference, CancellationToken cancellationToken = default)
    {
        var existingPath = await FindNotePathAsync(noteReference, cancellationToken).ConfigureAwait(false);
        if (existingPath is not null)
        {
            return existingPath;
        }

        return await CreateLinkedNoteAsync(noteReference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> CreateNoteAsync(
        NoteTemplateKind templateKind,
        ToolContext? context,
        Heimdall.Core.Localization.LocalizationManager? localizer = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var nowLocal = DateTime.Now;
        var draft = NotesTemplateFactory.Create(templateKind, context, nowLocal, localizer);
        var desiredPath = Path.Combine(_notesRootPath, draft.RelativePath);
        if (templateKind == NoteTemplateKind.Daily && File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var targetPath = GetUniquePath(desiredPath);

        await SaveNoteAsync(targetPath, draft.Content, cancellationToken).ConfigureAwait(false);
        return targetPath;
    }

    public string CreateFolder(string folderName, string? parentFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);
        var sanitized = SanitizeFileName(folderName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException("Invalid folder name.", nameof(folderName));
        }

        var parent = parentFolder ?? _notesRootPath;
        ValidatePathWithinRoot(Path.Combine(parent, "x"));
        var folderPath = Path.Combine(parent, sanitized);
        Directory.CreateDirectory(folderPath);
        return folderPath;
    }

    public async Task<string> MoveNoteToFolderAsync(string filePath, string targetFolder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);
        ValidatePathWithinRoot(Path.Combine(targetFolder, "x"));

        var fileName = Path.GetFileName(filePath);
        var newPath = Path.Combine(targetFolder, fileName);
        newPath = GetUniquePath(newPath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(targetFolder);
            File.Move(filePath, newPath);
        }
        finally
        {
            _writeLock.Release();
        }

        RemoveEmptyDirectories(Path.GetDirectoryName(filePath));
        return newPath;
    }

    public async Task<string> LoadNoteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);

        cancellationToken.ThrowIfCancellationRequested();
        EnsureInitialized();

        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        return await File.ReadAllTextAsync(filePath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveNoteAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, content ?? string.Empty, Utf8NoBom, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Synchronous save for use in <see cref="IToolView.CanClose"/> and
    /// <see cref="IDisposable.Dispose"/>, which cannot be async.
    /// </summary>
    /// <remarks>
    /// A wait that expires is reported, not swallowed: returning normally let the Notes tab
    /// mark the note clean and close, and the last edits were lost without a word.
    /// </remarks>
    public bool SaveNote(string filePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);

        if (!_writeLock.Wait(_syncSaveLockWait))
        {
            FileLogger.Warn($"SaveNote timed out waiting for write lock: {filePath}");
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, content ?? string.Empty, Utf8NoBom);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteNoteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        RemoveEmptyDirectories(Path.GetDirectoryName(filePath));
    }

    public async Task<string> RenameNoteAsync(string filePath, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ValidatePathWithinRoot(filePath);

        var desiredTitle = StripMarkdownExtension(newName).Trim();
        var sanitized = SanitizeFileName(newName);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new ArgumentException("Invalid file name.", nameof(newName));
        }

        if (!sanitized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            sanitized += ".md";
        }

        var directory = Path.GetDirectoryName(filePath) ?? _notesRootPath;
        var newPath = Path.Combine(directory, sanitized);
        ValidatePathWithinRoot(newPath);

        if (string.Equals(filePath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        newPath = GetUniquePath(newPath);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var content = File.ReadAllText(filePath, Utf8NoBom);
            File.Move(filePath, newPath);

            var updatedContent = UpdatePrimaryHeading(content, desiredTitle);
            if (!string.Equals(updatedContent, content, StringComparison.Ordinal))
            {
                File.WriteAllText(newPath, updatedContent, Utf8NoBom);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        return newPath;
    }

    public async Task<string> DuplicateNoteAsync(string filePath, CancellationToken cancellationToken = default)
        => await DuplicateNoteAsync(filePath, "Copy", cancellationToken).ConfigureAwait(false);

    public async Task<string> DuplicateNoteAsync(string filePath, string duplicateLabel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ValidatePathWithinRoot(filePath);

        var content = await LoadNoteAsync(filePath, cancellationToken).ConfigureAwait(false);
        var directory = Path.GetDirectoryName(filePath) ?? _notesRootPath;
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var fileSuffix = NotesTemplateFactory.SlugifyValue(duplicateLabel);
        if (string.IsNullOrWhiteSpace(fileSuffix))
        {
            fileSuffix = "copy";
        }

        var copyPath = GetUniquePath(Path.Combine(directory, $"{baseName}-{fileSuffix}.md"));
        var desiredTitle = BuildDuplicatedTitle(ExtractTitle(filePath, content), duplicateLabel);
        var duplicatedContent = UpdatePrimaryHeading(content, desiredTitle);

        await SaveNoteAsync(copyPath, duplicatedContent, cancellationToken).ConfigureAwait(false);
        return copyPath;
    }

    public string GetRelativePath(string filePath)
        => Path.GetRelativePath(_notesRootPath, filePath).Replace('\\', '/');

    public bool IsUnderNotesRoot(string filePath)
    {
        var fullRoot = Path.GetFullPath(_notesRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullFile = Path.GetFullPath(filePath);

        return fullFile.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullFile, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void ValidatePathWithinRoot(string filePath)
    {
        if (!IsUnderNotesRoot(filePath))
        {
            throw new UnauthorizedAccessException(
                $"Path '{filePath}' is outside the notes directory.");
        }
    }

    private string GetUniquePath(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? _notesRootPath;
        var fileName = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName}-{index}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{fileName}-{DateTime.Now:HHmmssfff}{extension}");
    }

    private void RemoveEmptyDirectories(string? startDirectory)
    {
        var current = startDirectory;
        while (!string.IsNullOrEmpty(current)
            && current.StartsWith(_notesRootPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current, _notesRootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sanitized.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        var result = sanitized.ToString().Trim().Trim('.');
        return result.Contains("..", StringComparison.Ordinal) ? string.Empty : result;
    }

    /// <summary>
    /// Whether the note's text holds the search text. A note saved by the editor before it wrote
    /// [[X]] unescaped holds \[\[X]]; a search for [[X]] finds it as well.
    /// </summary>
    internal static bool ContentContainsSearch(string content, string search)
    {
        if (content.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return search.Contains(EscapableBracket)
            && content.Replace(EscapedBracket, EscapableBracket, StringComparison.Ordinal)
                .Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractTitle(string filePath, string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                return line[2..].Trim();
            }
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    private static string BuildDuplicatedTitle(string title, string duplicateLabel)
    {
        var normalizedLabel = string.IsNullOrWhiteSpace(duplicateLabel) ? "Copy" : duplicateLabel.Trim();
        return $"{title} ({normalizedLabel})";
    }

    private static string UpdatePrimaryHeading(string content, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(newTitle))
        {
            return content;
        }

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var hasTrailingNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.TrimStart();
            if (!trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                continue;
            }

            var indentLength = rawLine.Length - trimmed.Length;
            var indentation = indentLength > 0 ? rawLine[..indentLength] : string.Empty;
            lines[i] = $"{indentation}# {newTitle}";

            var updated = string.Join('\n', lines);
            if (hasTrailingNewline)
            {
                updated += "\n";
            }

            return newline == "\r\n"
                ? updated.Replace("\n", "\r\n", StringComparison.Ordinal)
                : updated;
        }

        return content;
    }

    private static string ExtractSummary(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)
                || line.StartsWith("#", StringComparison.Ordinal)
                || line.StartsWith(">", StringComparison.Ordinal)
                || line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Length <= 120 ? line : $"{line[..117]}...";
        }

        return string.Empty;
    }

    private async Task<string> CreateLinkedNoteAsync(string noteReference, CancellationToken cancellationToken)
    {
        var title = StripMarkdownExtension(NormalizeNoteReference(noteReference));
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Invalid note reference.", nameof(noteReference));
        }

        var fileName = NotesTemplateFactory.SlugifyValue(title);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Invalid note reference.", nameof(noteReference));
        }

        var targetPath = GetUniquePath(Path.Combine(_notesRootPath, $"{fileName}.md"));
        var content = new StringBuilder()
            .AppendLine($"# {title}")
            .AppendLine()
            .ToString();

        await SaveNoteAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
        return targetPath;
    }

    private IEnumerable<NoteLookupCandidate> EnumerateLookupCandidates(CancellationToken cancellationToken)
    {
        foreach (var filePath in Directory.EnumerateFiles(_notesRootPath, "*.md", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var content = File.ReadAllText(filePath, Utf8NoBom);
            var relativePath = GetRelativePath(filePath);

            yield return new NoteLookupCandidate(
                filePath,
                ExtractTitle(filePath, content),
                Path.GetFileNameWithoutExtension(filePath),
                StripMarkdownExtension(relativePath));
        }
    }

    private static IReadOnlyList<string> ExtractTags(string content)
    {
        var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!TryGetMetadataPayload(line, out var payload)
                || !payload.StartsWith("tags:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var tag in payload["tags:".Length..]
                         .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (uniqueTags.Add(tag))
                {
                    tags.Add(tag);
                }
            }
        }

        return tags;
    }

    private static bool TryGetMetadataPayload(string line, out string payload)
    {
        payload = string.Empty;
        if (!line.StartsWith(">", StringComparison.Ordinal))
        {
            return false;
        }

        var value = line.Length > 1 && line[1] == ' '
            ? line[2..]
            : line[1..];

        payload = value.Trim();
        return payload.Length > 0;
    }

    private static string NormalizeNoteReference(string noteReference)
        => noteReference.Trim();

    private static string NormalizeRelativeReference(string noteReference)
        => noteReference.Replace('\\', '/').Trim().Trim('/');

    private static string StripMarkdownExtension(string value)
        => value.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? value[..^3]
            : value;

    private static string GetReferenceFileName(string pathReference)
        => pathReference.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? pathReference;

    private static string SlugifyRelativeReference(string pathReference)
    {
        if (string.IsNullOrWhiteSpace(pathReference))
        {
            return string.Empty;
        }

        return string.Join(
            '/',
            pathReference
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(NotesTemplateFactory.SlugifyValue)
                .Where(segment => segment.Length > 0));
    }
}
