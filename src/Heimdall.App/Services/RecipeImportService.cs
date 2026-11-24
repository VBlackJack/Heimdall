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
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>Why a recipe file cannot be offered for import.</summary>
public enum RecipeImportFailure
{
    /// <summary>The file can be offered for import.</summary>
    None,

    /// <summary>The organisation policy forbids the Recipes pack, so nothing is imported.</summary>
    PackForbiddenByPolicy,

    /// <summary>The file could not be read.</summary>
    FileUnreadable,

    /// <summary>The file is not a valid recipe; <see cref="RecipeImportOutcome.Errors"/> says why.</summary>
    InvalidRecipe,

    /// <summary>The recipe is valid but its program may not run; <see cref="RecipeImportOutcome.Verdict"/> says why.</summary>
    ProgramRefused,
}

/// <summary>
/// A recipe that passed every check and is ready to be shown in the preview. Nothing has been
/// stored: the plan is only what the user is asked to confirm.
/// </summary>
/// <param name="Recipe">The parsed and validated recipe.</param>
/// <param name="Bytes">The exact bytes of the file; these are what is stored and what is exported later.</param>
/// <param name="ResolvedProgramPath">The absolute path the program resolved to now.</param>
/// <param name="SessionUse">Whether the recipe reads the host or the port of the selected session.</param>
/// <param name="Existing">The index entry of the recipe this one replaces, or null for a new recipe.</param>
/// <param name="Previous">The recipe being replaced, or null when there is none or its stored file can no longer be read.</param>
public sealed record RecipeImportPlan(
    Recipe Recipe,
    byte[] Bytes,
    string ResolvedProgramPath,
    RecipeSessionUse SessionUse,
    RecipeStoreEntry? Existing,
    Recipe? Previous)
{
    /// <summary>True when importing replaces a recipe with the same id.</summary>
    public bool IsReplace => Existing is not null;
}

/// <summary>The outcome of preparing a file for import: a plan, or the reason there is none.</summary>
public sealed class RecipeImportOutcome
{
    private RecipeImportOutcome(
        RecipeImportFailure failure,
        RecipeImportPlan? plan,
        IReadOnlyList<RecipeError> errors,
        RecipeProgramVerdict? verdict)
    {
        Failure = failure;
        Plan = plan;
        Errors = errors;
        Verdict = verdict;
    }

    /// <summary>Why there is no plan, or <see cref="RecipeImportFailure.None"/>.</summary>
    public RecipeImportFailure Failure { get; }

    /// <summary>The plan to show in the preview; null when <see cref="Failure"/> is set.</summary>
    public RecipeImportPlan? Plan { get; }

    /// <summary>The problems of the file when <see cref="Failure"/> is <see cref="RecipeImportFailure.InvalidRecipe"/>; empty otherwise.</summary>
    public IReadOnlyList<RecipeError> Errors { get; }

    /// <summary>The refused program when <see cref="Failure"/> is <see cref="RecipeImportFailure.ProgramRefused"/>; null otherwise.</summary>
    public RecipeProgramVerdict? Verdict { get; }

    /// <summary>True when there is a plan.</summary>
    public bool Succeeded => Plan is not null;

    internal static RecipeImportOutcome Ready(RecipeImportPlan plan) => new(RecipeImportFailure.None, plan, [], null);

    internal static RecipeImportOutcome Refused(RecipeImportFailure failure) => new(failure, null, [], null);

    internal static RecipeImportOutcome Invalid(IReadOnlyList<RecipeError> errors)
        => new(RecipeImportFailure.InvalidRecipe, null, errors, null);

    internal static RecipeImportOutcome ProgramNotAllowed(RecipeProgramVerdict verdict)
        => new(RecipeImportFailure.ProgramRefused, null, [], verdict);
}

/// <summary>
/// The first half of importing a recipe: read the file, parse and validate it, resolve its program
/// against the allowed folders, and find the recipe it would replace. Nothing is written; the second
/// half, <see cref="Commit"/>, is called only after the user confirmed the preview.
/// </summary>
public sealed class RecipeImportService
{
    private readonly IRecipeStore _store;
    private readonly IRecipeProgramLocator _locator;
    private readonly IToolAvailability _availability;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The imported recipes.</param>
    /// <param name="locator">Resolves the program of a recipe to the file that would run.</param>
    /// <param name="availability">Says whether the organisation policy forbids the Recipes pack.</param>
    public RecipeImportService(IRecipeStore store, IRecipeProgramLocator locator, IToolAvailability availability)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _availability = availability ?? throw new ArgumentNullException(nameof(availability));
    }

    /// <summary>True while the organisation policy forbids recipes: nothing may be imported.</summary>
    public bool IsForbiddenByPolicy => _availability.IsPackForbiddenByPolicy(ToolPack.Recipes);

    /// <summary>True while the Recipes pack is on: an imported recipe then shows up as a tool at once.</summary>
    public bool IsPackEnabled => _availability.IsPackEnabled(ToolPack.Recipes);

    /// <summary>Reads a recipe file and prepares it for the preview.</summary>
    /// <param name="path">The file the user picked.</param>
    /// <param name="allowedFolders">The folders the user allowed in Settings.</param>
    public RecipeImportOutcome PrepareFile(string path, IReadOnlyList<string> allowedFolders)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(allowedFolders);

        if (IsForbiddenByPolicy)
        {
            return RecipeImportOutcome.Refused(RecipeImportFailure.PackForbiddenByPolicy);
        }

        byte[] bytes;
        try
        {
            // One read of one open handle, stopped at the cap: nothing is asked of the size first.
            CappedRead read = CappedFileReader.Read(path, RecipeLimits.MaxFileBytes);
            if (read.Bytes is not { } content)
            {
                return RecipeImportOutcome.Invalid(
                [
                    new RecipeError(
                        RecipeErrorCode.FileTooLarge,
                        Actual: (int)Math.Min(read.Length, int.MaxValue),
                        Limit: RecipeLimits.MaxFileBytes),
                ]);
            }

            bytes = content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            FileLogger.Warn("[Recipes] The file picked for import could not be read");
            return RecipeImportOutcome.Refused(RecipeImportFailure.FileUnreadable);
        }

        return Prepare(bytes, allowedFolders);
    }

    /// <summary>Prepares the bytes of a recipe file for the preview.</summary>
    /// <param name="bytes">The exact bytes of the file.</param>
    /// <param name="allowedFolders">The folders the user allowed in Settings.</param>
    public RecipeImportOutcome Prepare(byte[] bytes, IReadOnlyList<string> allowedFolders)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(allowedFolders);

        if (IsForbiddenByPolicy)
        {
            return RecipeImportOutcome.Refused(RecipeImportFailure.PackForbiddenByPolicy);
        }

        RecipeParseResult parsed = RecipeParser.Parse(bytes);
        if (parsed.Recipe is not { } recipe)
        {
            FileLogger.Info($"[Recipes] A recipe file was refused: {parsed.Errors.Count} problem(s)");
            return RecipeImportOutcome.Invalid(parsed.Errors);
        }

        RecipeProgramVerdict verdict = _locator.Resolve(recipe.Program, allowedFolders);
        if (!verdict.IsResolved || verdict.ResolvedPath is not { } resolved)
        {
            FileLogger.Info($"[Recipes] Recipe {recipe.Id} was refused: its program is not allowed ({verdict.Refusal})");
            return RecipeImportOutcome.ProgramNotAllowed(verdict);
        }

        RecipeStoreEntry? existing = _store.List()
            .FirstOrDefault(entry => string.Equals(entry.Id, recipe.Id, StringComparison.Ordinal));
        Recipe? previous = null;
        if (existing is not null && _store.ReadOriginalBytes(recipe.Id) is { } previousBytes)
        {
            previous = RecipeParser.Parse(previousBytes).Recipe;
        }

        return RecipeImportOutcome.Ready(new RecipeImportPlan(
            recipe,
            bytes,
            resolved,
            RecipeSessionUse.Of(recipe),
            existing,
            previous));
    }

    /// <summary>
    /// Stores the recipe of a plan the user confirmed: a replace when the plan replaces one, an add
    /// otherwise. The store parses the bytes again, so the stored file is held to the same rules.
    /// </summary>
    /// <param name="plan">A plan from <see cref="Prepare"/>.</param>
    public RecipeStoreResult Commit(RecipeImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        RecipeStoreResult result = plan.IsReplace
            ? _store.Replace(plan.Bytes, plan.ResolvedProgramPath)
            : _store.Add(plan.Bytes, plan.ResolvedProgramPath);
        FileLogger.Info($"[Recipes] Recipe {plan.Recipe.Id} {(plan.IsReplace ? "replace" : "import")}: {result.Failure}");
        return result;
    }
}
