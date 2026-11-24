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
using System.Security.Cryptography;
using System.Text.Json;
using Heimdall.Core.Logging;
using Heimdall.Core.Recipes;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// File-backed <see cref="IRecipeStore"/>: the recipes folder under the data directory, one file
/// per recipe and one index file.
/// </summary>
/// <remarks>
/// <para>The index is the record of what was imported: id, file name, SHA-256 of the exact bytes,
/// the program path resolved at import and the import time. A recipe loads only when its index
/// entry exists and the file on disk still hashes to the recorded value, so a file dropped into
/// the folder, or edited afterwards, is skipped and reported instead of being run. The file name is
/// derived from the validated id and never taken from a caller; it carries a fixed prefix because a
/// name whose first dotted segment is a Windows device name (an id such as <c>con.tool</c>) would
/// otherwise open the device.</para>
/// <para>There is deliberately no parameterless constructor: the production location is resolved
/// once, in the composition root, and a test cannot reach the user's own folder.</para>
/// <para>A replace writes the new file before the index. A crash between the two leaves a file
/// whose hash no longer matches its entry, which loads as nothing: the safe direction.</para>
/// </remarks>
public sealed class RecipeStore : IRecipeStore
{
    /// <summary>The name of the index file inside the recipes folder.</summary>
    public const string IndexFileName = "index.json";

    /// <summary>Prefix of every stored recipe file name, ahead of the id.</summary>
    public const string FileNamePrefix = "rcp-";

    /// <summary>The most recipes the store keeps, so a hostile batch cannot fill the disk or the Tools tab.</summary>
    public const int MaxRecipes = 200;

    /// <summary>The index format version this build writes and reads.</summary>
    internal const int IndexSchemaVersion = 1;

    /// <summary>The largest index file read, in bytes.</summary>
    private const long MaxIndexBytes = 1024 * 1024;

    private const int MaxIdLength = (RecipeLimits.MaxIdPartLength * 2) + 1;

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _directory;

    /// <summary>Creates a store whose folder is <c>recipes</c> under <paramref name="dataRoot"/>.</summary>
    /// <param name="dataRoot">The application data directory (the one <c>--data-dir</c> overrides).</param>
    public RecipeStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _directory = Core.Configuration.ApplicationDataPathResolver.GetRecipesDirectory(dataRoot);
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>The file name a recipe of this id is stored under.</summary>
    public static string FileNameFor(string id) => FileNamePrefix + id + RecipeFormat.FileExtension;

    /// <inheritdoc />
    public RecipeLoadResult LoadAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return RecipeLoadResult.Empty;
            }

            List<LoadedRecipe> loaded = [];
            List<RecipeLoadIssue> issues = [];
            HashSet<string> indexedFiles = new(StringComparer.OrdinalIgnoreCase);

            if (!TryReadIndex(out List<RecipeStoreEntry> entries, issues, out _))
            {
                entries = [];
            }

            foreach (RecipeStoreEntry entry in entries)
            {
                indexedFiles.Add(entry.FileName);
                LoadOne(entry, loaded, issues);
            }

            foreach (string path in EnumerateRecipeFiles())
            {
                string fileName = Path.GetFileName(path);
                if (!indexedFiles.Contains(fileName))
                {
                    issues.Add(new RecipeLoadIssue(RecipeLoadIssueCode.NotIndexed, fileName, null));
                }
            }

            return new RecipeLoadResult(loaded, issues);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RecipeStoreEntry> List()
    {
        lock (_gate)
        {
            return Directory.Exists(_directory) && TryReadIndex(out List<RecipeStoreEntry> entries, issues: null, out _)
                ? entries
                : [];
        }
    }

    /// <inheritdoc />
    public RecipeStoreEntry? Current(string id) => CurrentEntry(id);

    private RecipeStoreEntry? CurrentEntry(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            if (!Directory.Exists(_directory) || !TryReadIndex(out List<RecipeStoreEntry> entries, issues: null, out _))
            {
                return null;
            }

            return entries.Find(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public RecipeStoreResult Add(byte[] bytes, string resolvedProgramPath)
        => Announce(Write(bytes, resolvedProgramPath, replace: false));

    /// <inheritdoc />
    public RecipeStoreResult Replace(byte[] bytes, string resolvedProgramPath)
        => Announce(Write(bytes, resolvedProgramPath, replace: true));

    /// <inheritdoc />
    public RecipeStoreResult Remove(string id) => Announce(RemoveCore(id));

    // Raised outside the lock, so a subscriber that asks the store something cannot deadlock.
    private RecipeStoreResult Announce(RecipeStoreResult result)
    {
        if (result.Succeeded)
        {
            Changed?.Invoke();
        }

        return result;
    }

    private RecipeStoreResult RemoveCore(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            if (!Directory.Exists(_directory))
            {
                return RecipeStoreResult.Fail(RecipeStoreFailure.NotFound);
            }

            // An index that cannot be read in full is not rewritten from what could be read of it.
            if (!TryReadIndex(out List<RecipeStoreEntry> entries, issues: null, out bool damaged, forWrite: true))
            {
                return RecipeStoreResult.Fail(damaged ? RecipeStoreFailure.IndexDamaged : RecipeStoreFailure.StorageError);
            }

            int position = entries.FindIndex(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));
            if (position < 0)
            {
                return RecipeStoreResult.Fail(RecipeStoreFailure.NotFound);
            }

            RecipeStoreEntry removed = entries[position];
            entries.RemoveAt(position);

            try
            {
                // The index goes first: if the file then survives, it is merely not indexed, which
                // loads as nothing. The other order could leave an entry with no file.
                WriteIndex(entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogger.Warn($"Recipe store: the index could not be written while removing a recipe ({ex.GetType().Name})");
                return RecipeStoreResult.Fail(RecipeStoreFailure.StorageError);
            }

            try
            {
                File.Delete(Path.Combine(_directory, removed.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogger.Warn($"Recipe store: the file {removed.FileName} could not be deleted ({ex.GetType().Name})");
            }

            return RecipeStoreResult.Success();
        }
    }

    /// <inheritdoc />
    public byte[]? ReadOriginalBytes(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        lock (_gate)
        {
            if (!Directory.Exists(_directory) || !TryReadIndex(out List<RecipeStoreEntry> entries, issues: null, out _))
            {
                return null;
            }

            RecipeStoreEntry? entry = entries.Find(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
            if (entry is null)
            {
                return null;
            }

            byte[]? bytes = TryReadFile(Path.Combine(_directory, entry.FileName));
            return bytes is not null && HashMatches(bytes, entry.Sha256) ? bytes : null;
        }
    }

    private RecipeStoreResult Write(byte[] bytes, string resolvedProgramPath, bool replace)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(resolvedProgramPath);

        // Validated here, never trusted from the caller: the preview the user confirmed was built
        // from the caller's parse, and this is the parse the stored file is held to.
        RecipeParseResult parsed = RecipeParser.Parse(bytes);
        if (parsed.Recipe is not { } recipe)
        {
            return RecipeStoreResult.Invalid(parsed.Errors);
        }

        if (!Path.IsPathFullyQualified(resolvedProgramPath))
        {
            return RecipeStoreResult.Fail(RecipeStoreFailure.InvalidProgramPath);
        }

        lock (_gate)
        {
            // An index that exists and cannot be read is not an empty store: writing from an empty
            // list would replace it and drop the trust record of every other recipe. A missing index
            // is an empty store.
            List<RecipeStoreEntry> entries = [];
            bool damaged = false;
            if (Directory.Exists(_directory) && !TryReadIndex(out entries, issues: null, out damaged, forWrite: true))
            {
                return RecipeStoreResult.Fail(damaged ? RecipeStoreFailure.IndexDamaged : RecipeStoreFailure.StorageError);
            }

            int position = entries.FindIndex(entry => string.Equals(entry.Id, recipe.Id, StringComparison.Ordinal));
            if (replace && position < 0)
            {
                return RecipeStoreResult.Fail(RecipeStoreFailure.NotFound);
            }

            if (!replace && position >= 0)
            {
                return RecipeStoreResult.Fail(RecipeStoreFailure.AlreadyExists);
            }

            if (!replace && entries.Count >= MaxRecipes)
            {
                return RecipeStoreResult.Fail(RecipeStoreFailure.TooManyRecipes);
            }

            string fileName = FileNameFor(recipe.Id);
            string filePath = Path.Combine(_directory, fileName);
            RecipeStoreEntry newEntry = new(
                recipe.Id,
                fileName,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                resolvedProgramPath,
                DateTimeOffset.UtcNow);

            byte[]? previousBytes = replace ? TryReadFile(filePath) : null;
            try
            {
                EnsureDirectory();
                SecureFileWriter.WriteBytesAtomic(filePath, bytes);

                if (replace)
                {
                    entries[position] = newEntry;
                }
                else
                {
                    entries.Add(newEntry);
                }

                WriteIndex(entries);
                return RecipeStoreResult.Success(newEntry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLogger.Warn($"Recipe store: recipe {recipe.Id} could not be stored ({ex.GetType().Name})");
                RestoreAfterFailure(filePath, previousBytes);
                return RecipeStoreResult.Fail(RecipeStoreFailure.StorageError);
            }
        }
    }

    private static void RestoreAfterFailure(string filePath, byte[]? previousBytes)
    {
        try
        {
            if (previousBytes is null)
            {
                File.Delete(filePath);
            }
            else
            {
                SecureFileWriter.WriteBytesAtomic(filePath, previousBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The leftover file is not indexed, or no longer matches its entry: it loads as nothing.
            FileLogger.Warn($"Recipe store: the previous state of a recipe file could not be restored ({ex.GetType().Name})");
        }
    }

    private void LoadOne(RecipeStoreEntry entry, List<LoadedRecipe> loaded, List<RecipeLoadIssue> issues)
    {
        string path = Path.Combine(_directory, entry.FileName);
        if (!File.Exists(path))
        {
            issues.Add(new RecipeLoadIssue(RecipeLoadIssueCode.FileMissing, entry.FileName, entry.Id));
            return;
        }

        byte[]? bytes = TryReadFile(path);
        if (bytes is null)
        {
            issues.Add(new RecipeLoadIssue(RecipeLoadIssueCode.Unreadable, entry.FileName, entry.Id));
            return;
        }

        if (!HashMatches(bytes, entry.Sha256))
        {
            issues.Add(new RecipeLoadIssue(RecipeLoadIssueCode.HashMismatch, entry.FileName, entry.Id));
            return;
        }

        RecipeParseResult parsed = RecipeParser.Parse(bytes);
        if (parsed.Recipe is not { } recipe || !string.Equals(recipe.Id, entry.Id, StringComparison.Ordinal))
        {
            issues.Add(new RecipeLoadIssue(RecipeLoadIssueCode.Invalid, entry.FileName, entry.Id));
            return;
        }

        loaded.Add(new LoadedRecipe(recipe, entry));
    }

    private IEnumerable<string> EnumerateRecipeFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_directory, "*" + RecipeFormat.FileExtension).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"Recipe store: the folder could not be listed ({ex.GetType().Name})");
            return [];
        }
    }

    private static bool HashMatches(byte[] bytes, string expectedSha256)
        => string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase);

    private static byte[]? TryReadFile(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length > RecipeLimits.MaxFileBytes)
            {
                // An oversize file can never be a valid recipe; it is not read into memory.
                return info.Exists ? [] : null;
            }

            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // forWrite: the caller is about to rewrite the index, so an entry that could not be built (invalid or
    // duplicate) counts as an index that cannot be read, rather than being dropped from the rewrite.
    // damaged: set when forWrite refused because an entry could not be built, as opposed to an index that
    // could not be read or parsed at all.
    private bool TryReadIndex(out List<RecipeStoreEntry> entries, List<RecipeLoadIssue>? issues, out bool damaged, bool forWrite = false)
    {
        damaged = false;
        entries = [];
        string indexPath = Path.Combine(_directory, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return true;
        }

        IndexDocument? document;
        try
        {
            FileInfo info = new(indexPath);
            if (info.Length > MaxIndexBytes)
            {
                throw new JsonException("The index is larger than it can be.");
            }

            document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllBytes(indexPath), IndexJsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLogger.Warn($"Recipe store: the index could not be read ({ex.GetType().Name})");
            issues?.Add(new RecipeLoadIssue(RecipeLoadIssueCode.IndexUnreadable, IndexFileName, null));
            return false;
        }

        if (document is null || document.SchemaVersion > IndexSchemaVersion || document.Recipes is null)
        {
            issues?.Add(new RecipeLoadIssue(RecipeLoadIssueCode.IndexUnreadable, IndexFileName, null));
            return false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (IndexItem? item in document.Recipes)
        {
            if (!TryBuildEntry(item, out RecipeStoreEntry? entry) || !seen.Add(entry.Id))
            {
                issues?.Add(new RecipeLoadIssue(RecipeLoadIssueCode.IndexEntryInvalid, item?.FileName ?? IndexFileName, item?.Id));
                if (forWrite)
                {
                    entries = [];
                    damaged = true;
                    return false;
                }

                continue;
            }

            entries.Add(entry);
        }

        return true;
    }

    private static bool TryBuildEntry(IndexItem? item, out RecipeStoreEntry entry)
    {
        entry = null!;
        if (item is null
            || !IsSafeIdText(item.Id)
            || string.IsNullOrEmpty(item.Sha256)
            || string.IsNullOrEmpty(item.ResolvedProgramPath)
            || item.ImportedAtUtc is not { } importedAt
            || !string.Equals(item.FileName, FileNameFor(item.Id!), StringComparison.Ordinal))
        {
            return false;
        }

        entry = new RecipeStoreEntry(item.Id!, item.FileName!, item.Sha256, item.ResolvedProgramPath, importedAt);
        return true;
    }

    // Before an id is used to build a path it must be plain id text: lowercase letters, digits,
    // hyphen and the one separator. The parser holds a stored file to the full id rules afterwards.
    private static bool IsSafeIdText(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength)
        {
            return false;
        }

        foreach (char c in id)
        {
            bool allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or RecipeFormat.IdSeparator;
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private void EnsureDirectory()
    {
        if (Directory.Exists(_directory))
        {
            return;
        }

        try
        {
            SecureFileWriter.CreateRestrictedDirectory(_directory);
        }
        catch (IOException) when (Directory.Exists(_directory))
        {
            // Created by another writer between the check and the call: nothing to do.
        }
    }

    private void WriteIndex(List<RecipeStoreEntry> entries)
    {
        IndexDocument document = new()
        {
            SchemaVersion = IndexSchemaVersion,
            Recipes = [.. entries.Select(entry => new IndexItem
            {
                Id = entry.Id,
                FileName = entry.FileName,
                Sha256 = entry.Sha256,
                ResolvedProgramPath = entry.ResolvedProgramPath,
                ImportedAtUtc = entry.ImportedAtUtc,
            })],
        };

        EnsureDirectory();
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, IndexJsonOptions);
        SecureFileWriter.WriteBytesAtomic(Path.Combine(_directory, IndexFileName), json);
    }

    private sealed class IndexDocument
    {
        public int SchemaVersion { get; set; }

        public List<IndexItem?>? Recipes { get; set; }
    }

    private sealed class IndexItem
    {
        public string? Id { get; set; }

        public string? FileName { get; set; }

        public string? Sha256 { get; set; }

        public string? ResolvedProgramPath { get; set; }

        public DateTimeOffset? ImportedAtUtc { get; set; }
    }
}
