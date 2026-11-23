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

using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>Why a recipe file in the store was not loaded.</summary>
public enum RecipeLoadIssueCode
{
    /// <summary>A recipe file sits in the folder without an index entry: it was dropped there, not imported.</summary>
    NotIndexed,

    /// <summary>The index lists a recipe whose file is gone.</summary>
    FileMissing,

    /// <summary>The file on disk is not the bytes that were imported.</summary>
    HashMismatch,

    /// <summary>The file matches its index entry but is not a valid recipe, or holds another recipe's id.</summary>
    Invalid,

    /// <summary>The file could not be read.</summary>
    Unreadable,

    /// <summary>An index entry is malformed or names a file other than the one its id is stored under.</summary>
    IndexEntryInvalid,

    /// <summary>The index itself could not be read or understood; no recipe is loaded from it.</summary>
    IndexUnreadable,
}

/// <summary>One recipe file that was skipped while loading, with the reason.</summary>
/// <param name="Code">Why it was skipped.</param>
/// <param name="FileName">The file name (never a full path).</param>
/// <param name="RecipeId">The id the index gives it, or null when there is none.</param>
public sealed record RecipeLoadIssue(RecipeLoadIssueCode Code, string FileName, string? RecipeId);

/// <summary>What the index remembers about one imported recipe.</summary>
/// <param name="Id">The recipe id.</param>
/// <param name="FileName">The file the recipe is stored in, derived from the id.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 of the exact imported bytes.</param>
/// <param name="ResolvedProgramPath">The program path resolved at import, shown in the preview and compared at each run.</param>
/// <param name="ImportedAtUtc">When the recipe was imported.</param>
public sealed record RecipeStoreEntry(
    string Id,
    string FileName,
    string Sha256,
    string ResolvedProgramPath,
    DateTimeOffset ImportedAtUtc);

/// <summary>An imported recipe that passed every check at load time.</summary>
/// <param name="Recipe">The parsed and validated recipe.</param>
/// <param name="Entry">Its index entry.</param>
public sealed record LoadedRecipe(Recipe Recipe, RecipeStoreEntry Entry);

/// <summary>The outcome of loading the store: the recipes that may be used, and what was skipped.</summary>
/// <param name="Recipes">The recipes whose index entry exists and whose file matches its hash.</param>
/// <param name="Issues">Every file or entry that was skipped.</param>
public sealed record RecipeLoadResult(IReadOnlyList<LoadedRecipe> Recipes, IReadOnlyList<RecipeLoadIssue> Issues)
{
    /// <summary>A load that found nothing.</summary>
    public static RecipeLoadResult Empty { get; } = new([], []);
}

/// <summary>Why a store change was refused.</summary>
public enum RecipeStoreFailure
{
    /// <summary>The change was made.</summary>
    None,

    /// <summary>The bytes are not a valid recipe; <see cref="RecipeStoreResult.Errors"/> says why.</summary>
    InvalidRecipe,

    /// <summary>An add was refused because a recipe with that id is already imported.</summary>
    AlreadyExists,

    /// <summary>A replace or remove was refused because no recipe with that id is imported.</summary>
    NotFound,

    /// <summary>The resolved program path is not an absolute path.</summary>
    InvalidProgramPath,

    /// <summary>The store already holds the most recipes it keeps.</summary>
    TooManyRecipes,

    /// <summary>The disk refused the write; the previous state was kept as far as it could be.</summary>
    StorageError,
}

/// <summary>The outcome of adding, replacing or removing a recipe.</summary>
public sealed class RecipeStoreResult
{
    private RecipeStoreResult(RecipeStoreFailure failure, RecipeStoreEntry? entry, IReadOnlyList<RecipeError> errors)
    {
        Failure = failure;
        Entry = entry;
        Errors = errors;
    }

    /// <summary>Why the change was refused, or <see cref="RecipeStoreFailure.None"/>.</summary>
    public RecipeStoreFailure Failure { get; }

    /// <summary>The index entry after an add or a replace; null otherwise.</summary>
    public RecipeStoreEntry? Entry { get; }

    /// <summary>The validation errors when <see cref="Failure"/> is <see cref="RecipeStoreFailure.InvalidRecipe"/>; empty otherwise.</summary>
    public IReadOnlyList<RecipeError> Errors { get; }

    /// <summary>True when the change was made.</summary>
    public bool Succeeded => Failure == RecipeStoreFailure.None;

    internal static RecipeStoreResult Success(RecipeStoreEntry? entry = null) => new(RecipeStoreFailure.None, entry, []);

    internal static RecipeStoreResult Fail(RecipeStoreFailure failure) => new(failure, null, []);

    internal static RecipeStoreResult Invalid(IReadOnlyList<RecipeError> errors) => new(RecipeStoreFailure.InvalidRecipe, null, errors);
}

/// <summary>
/// What a recipe view needs to know about the imported recipe it was built from: whether it is
/// still the one that is imported. A replaced or removed recipe leaves open views behind, and those
/// must not run the old recipe.
/// </summary>
public interface IRecipeVersions
{
    /// <summary>Raised after a recipe was added, replaced or removed, on the thread that did it.</summary>
    event Action? Changed;

    /// <summary>The lowercase hexadecimal SHA-256 of the bytes imported under the id, or null when no recipe has that id.</summary>
    /// <param name="id">The recipe id.</param>
    string? CurrentSha256(string id);
}

/// <summary>
/// The imported recipes: one file each in the recipes folder, and an index that records which
/// files were imported. Importing is the trust step, so a file merely placed in the folder is not
/// loaded. The store does not resolve programs and does not run anything.
/// </summary>
public interface IRecipeStore : IRecipeVersions
{
    /// <summary>
    /// Reads every imported recipe. A file that is not indexed, whose bytes changed since the
    /// import, or that is no longer valid is skipped and reported; nothing here throws for bad data.
    /// A folder that does not exist yet is an empty store and is not created.
    /// </summary>
    RecipeLoadResult LoadAll();

    /// <summary>The index entries, whatever the state of the files they point at.</summary>
    IReadOnlyList<RecipeStoreEntry> List();

    /// <summary>
    /// Imports a recipe. The bytes are parsed and validated again here, whatever the caller did
    /// before; the file name comes from the validated id.
    /// </summary>
    /// <param name="bytes">The exact bytes of the recipe file.</param>
    /// <param name="resolvedProgramPath">The absolute path the program resolved to at import.</param>
    RecipeStoreResult Add(byte[] bytes, string resolvedProgramPath);

    /// <summary>Replaces the imported recipe that has the same id as the bytes, with the same checks as <see cref="Add"/>.</summary>
    /// <param name="bytes">The exact bytes of the new recipe file.</param>
    /// <param name="resolvedProgramPath">The absolute path the program resolved to at import.</param>
    RecipeStoreResult Replace(byte[] bytes, string resolvedProgramPath);

    /// <summary>Removes an imported recipe and its file.</summary>
    /// <param name="id">The recipe id.</param>
    RecipeStoreResult Remove(string id);

    /// <summary>
    /// The exact bytes that were imported, for export; null when the recipe is unknown or its file
    /// no longer matches its hash, so a changed file is never handed out as the imported one.
    /// </summary>
    /// <param name="id">The recipe id.</param>
    byte[]? ReadOriginalBytes(string id);
}
