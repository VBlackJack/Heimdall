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

using System.Globalization;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>
/// The one place that turns the reasons a recipe cannot be imported into localized words: every
/// member of every enum Core and the store report is mapped to its key here, with the keys written
/// out so that a test can walk each enum against each language and check that the placeholders of
/// a text match in all three. The Core types carry codes only.
/// </summary>
/// <remarks>
/// Every error text takes the same five arguments: {0} the place in the recipe (a structural path
/// such as <c>arguments[2][0]</c>), {1} the property, input or fragment involved, {2} the offending
/// size or number, {3} the bound it broke and {4} the offending text. A text uses the ones it needs.
/// </remarks>
public static class RecipeImportText
{
    /// <summary>The key of the title of a refusal dialog.</summary>
    public const string RefusedTitleKey = "RecipeImportRefusedTitle";

    /// <summary>The key of the sentence that introduces the list of problems.</summary>
    public const string RefusedIntroKey = "RecipeImportRefusedIntro";

    /// <summary>The key of the line that says how many more problems there are.</summary>
    public const string MoreProblemsKey = "RecipeImportRefusedMore";

    /// <summary>The key of the line that says there is one more problem.</summary>
    public const string MoreProblemsOneKey = "RecipeImportRefusedMoreOne";

    /// <summary>The key of the bullet that starts each problem line.</summary>
    public const string BulletKey = "RecipeImportErrorBullet";

    /// <summary>The most problems listed in one refusal; the rest are counted.</summary>
    public const int MaxListedProblems = 10;

    /// <summary>The key of the generic line shown when an imported recipe did not load and no specific issue was recorded.</summary>
    public const string UnavailableFallbackKey = "SettingsRecipeItemNotLoaded";

    /// <summary>The key of why an imported recipe in Settings did not load.</summary>
    /// <param name="code">The load issue reported by the store, or null when none was recorded for the entry.</param>
    public static string KeyForLoadIssue(RecipeLoadIssueCode? code) => code switch
    {
        RecipeLoadIssueCode.HashMismatch => "SettingsRecipeItemUnavailable",
        RecipeLoadIssueCode.Invalid => "SettingsRecipeItemInvalid",
        RecipeLoadIssueCode.FileMissing => "SettingsRecipeItemFileMissing",
        RecipeLoadIssueCode.Unreadable => "SettingsRecipeItemUnreadable",
        _ => UnavailableFallbackKey,
    };

    /// <summary>The key of the text of a recipe error.</summary>
    /// <param name="code">The error code.</param>
    public static string KeyFor(RecipeErrorCode code) => code switch
    {
        RecipeErrorCode.FileTooLarge => "RecipeErrorFileTooLarge",
        RecipeErrorCode.EmptyDocument => "RecipeErrorEmptyDocument",
        RecipeErrorCode.SyntaxError => "RecipeErrorSyntaxError",
        RecipeErrorCode.UnsupportedSchemaVersion => "RecipeErrorUnsupportedSchemaVersion",
        RecipeErrorCode.UnknownProperty => "RecipeErrorUnknownProperty",
        RecipeErrorCode.DuplicateProperty => "RecipeErrorDuplicateProperty",
        RecipeErrorCode.MissingProperty => "RecipeErrorMissingProperty",
        RecipeErrorCode.WrongType => "RecipeErrorWrongType",
        RecipeErrorCode.InvalidEnumValue => "RecipeErrorInvalidEnumValue",
        RecipeErrorCode.ValueOutOfRange => "RecipeErrorValueOutOfRange",
        RecipeErrorCode.TooLong => "RecipeErrorTooLong",
        RecipeErrorCode.TooMany => "RecipeErrorTooMany",
        RecipeErrorCode.EmptyValue => "RecipeErrorEmptyValue",
        RecipeErrorCode.ControlCharacter => "RecipeErrorControlCharacter",
        RecipeErrorCode.InvalidIdShape => "RecipeErrorInvalidIdShape",
        RecipeErrorCode.InvalidVersion => "RecipeErrorInvalidVersion",
        RecipeErrorCode.InvalidInputName => "RecipeErrorInvalidInputName",
        RecipeErrorCode.DuplicateInputName => "RecipeErrorDuplicateInputName",
        RecipeErrorCode.InvalidLanguageTag => "RecipeErrorInvalidLanguageTag",
        RecipeErrorCode.UnknownTranslationField => "RecipeErrorUnknownTranslationField",
        RecipeErrorCode.InvalidIconKey => "RecipeErrorInvalidIconKey",
        RecipeErrorCode.PropertyNotAllowedForType => "RecipeErrorPropertyNotAllowedForType",
        RecipeErrorCode.MissingChoices => "RecipeErrorMissingChoices",
        RecipeErrorCode.DuplicateChoice => "RecipeErrorDuplicateChoice",
        RecipeErrorCode.InvalidChoiceDefault => "RecipeErrorInvalidChoiceDefault",
        RecipeErrorCode.InvalidDefault => "RecipeErrorInvalidDefault",
        RecipeErrorCode.InvalidPattern => "RecipeErrorInvalidPattern",
        RecipeErrorCode.MissingSwitchArguments => "RecipeErrorMissingSwitchArguments",
        RecipeErrorCode.UnreferencedInput => "RecipeErrorUnreferencedInput",
        RecipeErrorCode.EmptyToken => "RecipeErrorEmptyToken",
        RecipeErrorCode.EmptyGroup => "RecipeErrorEmptyGroup",
        RecipeErrorCode.UnknownPlaceholder => "RecipeErrorUnknownPlaceholder",
        RecipeErrorCode.MalformedPlaceholder => "RecipeErrorMalformedPlaceholder",
        RecipeErrorCode.UnknownInput => "RecipeErrorUnknownInput",
        RecipeErrorCode.OptionalInputInBareToken => "RecipeErrorOptionalInputInBareToken",
        RecipeErrorCode.BoolInputInTokenText => "RecipeErrorBoolInputInTokenText",
        RecipeErrorCode.ForbiddenBackslashPair => "RecipeErrorForbiddenBackslashPair",
        RecipeErrorCode.ForbiddenDoubleSlash => "RecipeErrorForbiddenDoubleSlash",
        RecipeErrorCode.ForbiddenUrl => "RecipeErrorForbiddenUrl",
        RecipeErrorCode.ForbiddenPercent => "RecipeErrorForbiddenPercent",
        RecipeErrorCode.ForbiddenLeadingAt => "RecipeErrorForbiddenLeadingAt",
        RecipeErrorCode.FixedValueStartsWithOptionCharacter => "RecipeErrorFixedValueStartsWithOptionCharacter",
        RecipeErrorCode.ForbiddenMixedSeparators => "RecipeErrorForbiddenMixedSeparators",
        RecipeErrorCode.ForbiddenNtPathPrefix => "RecipeErrorForbiddenNtPathPrefix",
        RecipeErrorCode.ForbiddenLookAlikeCharacter => "RecipeErrorForbiddenLookAlikeCharacter",
        RecipeErrorCode.FixedValueRefusedAtRun => "RecipeErrorFixedValueRefusedAtRun",
        RecipeErrorCode.InvalidBound => "RecipeErrorInvalidBound",
        RecipeErrorCode.MinAboveMax => "RecipeErrorMinAboveMax",
        RecipeErrorCode.DefaultOutOfBounds => "RecipeErrorDefaultOutOfBounds",
        RecipeErrorCode.RequiredWithDefault => "RecipeErrorRequiredWithDefault",
        RecipeErrorCode.OptionalInputInSwitchArguments => "RecipeErrorOptionalInputInSwitchArguments",
        RecipeErrorCode.StderrMergeWithStructuredOutput => "RecipeErrorStderrMergeWithStructuredOutput",
        RecipeErrorCode.CsvSeparatorWithoutCsvOutput => "RecipeErrorCsvSeparatorWithoutCsvOutput",
        RecipeErrorCode.DuplicateLanguageTag => "RecipeErrorDuplicateLanguageTag",
        RecipeErrorCode.DuplicateExitCode => "RecipeErrorDuplicateExitCode",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "An error without a text."),
    };

    /// <summary>The key of why a program may not run, or null for <see cref="RecipeProgramRefusal.None"/>.</summary>
    /// <param name="refusal">The refusal.</param>
    public static string? KeyFor(RecipeProgramRefusal refusal) => refusal switch
    {
        RecipeProgramRefusal.None => null,
        RecipeProgramRefusal.Empty => "RecipeProgramRefusalEmpty",
        RecipeProgramRefusal.InvalidShape => "RecipeProgramRefusalInvalidShape",
        RecipeProgramRefusal.ForbiddenCharacter => "RecipeProgramRefusalForbiddenCharacter",
        RecipeProgramRefusal.NotAnExecutable => "RecipeProgramRefusalNotAnExecutable",
        RecipeProgramRefusal.NotFound => "RecipeProgramRefusalNotFound",
        RecipeProgramRefusal.OutsideAllowedRoots => "RecipeProgramRefusalOutsideAllowedRoots",
        RecipeProgramRefusal.LinkLeavesAllowedRoots => "RecipeProgramRefusalLinkLeavesAllowedRoots",
        RecipeProgramRefusal.CannotResolve => "RecipeProgramRefusalCannotResolve",
        RecipeProgramRefusal.ShellOrInterpreter => "RecipeProgramRefusalShellOrInterpreter",
        RecipeProgramRefusal.ShellOrInterpreterByOriginalName => "RecipeProgramRefusalShellOrInterpreterByOriginalName",
        RecipeProgramRefusal.WritableByUser => "RecipeProgramRefusalWritableByUser",
        RecipeProgramRefusal.CannotReadPermissions => "RecipeProgramRefusalCannotReadPermissions",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "A refusal without a text."),
    };

    /// <summary>The key of why a file cannot be offered for import, or null for <see cref="RecipeImportFailure.None"/>.</summary>
    /// <param name="failure">The failure.</param>
    /// <remarks>
    /// An invalid recipe and a refused program have their own texts per code; this key is the
    /// sentence that stands for the failure as a whole.
    /// </remarks>
    public static string? KeyFor(RecipeImportFailure failure) => failure switch
    {
        RecipeImportFailure.None => null,
        RecipeImportFailure.PackForbiddenByPolicy => "RecipeImportFailurePackForbiddenByPolicy",
        RecipeImportFailure.FileUnreadable => "RecipeImportFailureFileUnreadable",
        RecipeImportFailure.InvalidRecipe => "RecipeImportFailureInvalidRecipe",
        RecipeImportFailure.ProgramRefused => "RecipeImportFailureProgramRefused",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure without a text."),
    };

    /// <summary>The key of why the store refused a change, or null for <see cref="RecipeStoreFailure.None"/>.</summary>
    /// <param name="failure">The failure.</param>
    public static string? KeyFor(RecipeStoreFailure failure) => failure switch
    {
        RecipeStoreFailure.None => null,
        RecipeStoreFailure.InvalidRecipe => "RecipeStoreFailureInvalidRecipe",
        RecipeStoreFailure.AlreadyExists => "RecipeStoreFailureAlreadyExists",
        RecipeStoreFailure.NotFound => "RecipeStoreFailureNotFound",
        RecipeStoreFailure.InvalidProgramPath => "RecipeStoreFailureInvalidProgramPath",
        RecipeStoreFailure.TooManyRecipes => "RecipeStoreFailureTooManyRecipes",
        RecipeStoreFailure.StorageError => "RecipeStoreFailureStorageError",
        RecipeStoreFailure.IndexDamaged => "RecipeStoreFailureIndexDamaged",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "A failure without a text."),
    };

    /// <summary>The key of why a folder may not be added, or null for <see cref="RecipeFolderRefusal.None"/>.</summary>
    /// <param name="refusal">The refusal.</param>
    public static string? KeyFor(RecipeFolderRefusal refusal) => refusal switch
    {
        RecipeFolderRefusal.None => null,
        RecipeFolderRefusal.Empty => "RecipeFolderRefusalEmpty",
        RecipeFolderRefusal.TooLong => "RecipeFolderRefusalTooLong",
        RecipeFolderRefusal.Unc => "RecipeFolderRefusalUnc",
        RecipeFolderRefusal.Device => "RecipeFolderRefusalDevice",
        RecipeFolderRefusal.NotAbsolute => "RecipeFolderRefusalNotAbsolute",
        RecipeFolderRefusal.ForbiddenCharacter => "RecipeFolderRefusalForbiddenCharacter",
        RecipeFolderRefusal.DriveRoot => "RecipeFolderRefusalDriveRoot",
        RecipeFolderRefusal.AlreadyListed => "RecipeFolderRefusalAlreadyListed",
        RecipeFolderRefusal.ListFull => "RecipeFolderRefusalListFull",
        RecipeFolderRefusal.LeadsToDriveRoot => "RecipeFolderRefusalLeadsToDriveRoot",
        RecipeFolderRefusal.LeadsToNetwork => "RecipeFolderRefusalLeadsToNetwork",
        RecipeFolderRefusal.LeadsToUnallowedLocation => "RecipeFolderRefusalLeadsToUnallowedLocation",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "A refusal without a text."),
    };

    /// <summary>The words for one problem of a recipe file, with where it is when that is known.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="error">The problem.</param>
    public static string Describe(LocalizationManager localizer, RecipeError error)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(error);

        string message = localizer.Format(
            KeyFor(error.Code),
            error.Path ?? string.Empty,
            error.Name ?? error.Fragment ?? string.Empty,
            (error.Actual ?? 0).ToString(CultureInfo.CurrentCulture),
            (error.Limit ?? 0).ToString(CultureInfo.CurrentCulture),
            error.Fragment ?? string.Empty);

        bool hasPath = !string.IsNullOrEmpty(error.Path);
        if (error.Position is { } position)
        {
            return hasPath
                ? localizer.Format("RecipeImportErrorWhereBoth", message, error.Path!, position.Line, position.Column)
                : localizer.Format("RecipeImportErrorWherePosition", message, position.Line, position.Column);
        }

        return hasPath ? localizer.Format("RecipeImportErrorWherePath", message, error.Path!) : message;
    }

    /// <summary>The words for a program that may not run; the text of the refusal, with the offending program text.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="verdict">A refused verdict.</param>
    public static string Describe(LocalizationManager localizer, RecipeProgramVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(verdict);

        return KeyFor(verdict.Refusal) is { } key ? localizer.Format(key, verdict.Fragment) : string.Empty;
    }

    /// <summary>The words for a folder that may not be added; empty for <see cref="RecipeFolderRefusal.None"/>.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="refusal">The refusal.</param>
    /// <param name="maxFolders">The most folders the list keeps, named by the full-list text.</param>
    public static string Describe(LocalizationManager localizer, RecipeFolderRefusal refusal, int maxFolders)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return KeyFor(refusal) is { } key ? localizer.Format(key, maxFolders) : string.Empty;
    }

    /// <summary>The words for a store that refused a change; empty for <see cref="RecipeStoreFailure.None"/>.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="result">The store's answer.</param>
    public static string Describe(LocalizationManager localizer, RecipeStoreResult result)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Failure == RecipeStoreFailure.InvalidRecipe && result.Errors.Count > 0)
        {
            return DescribeProblems(localizer, result.Errors);
        }

        // The damaged-index text names the index file, which the store knows and a locale should not repeat.
        if (result.Failure == RecipeStoreFailure.IndexDamaged)
        {
            return localizer.Format("RecipeStoreFailureIndexDamaged", RecipeStore.IndexFileName);
        }

        return KeyFor(result.Failure) is { } key ? localizer[key] : string.Empty;
    }

    /// <summary>
    /// The whole refusal text for a file that cannot be imported: one sentence for the failure, then
    /// each problem (at most <see cref="MaxListedProblems"/>) on its own line.
    /// </summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="outcome">An outcome without a plan.</param>
    public static string Describe(LocalizationManager localizer, RecipeImportOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Failure switch
        {
            RecipeImportFailure.InvalidRecipe => DescribeProblems(localizer, outcome.Errors),
            RecipeImportFailure.ProgramRefused when outcome.Verdict is { } verdict
                => localizer[RefusedIntroKey] + Environment.NewLine + Bullet(localizer, Describe(localizer, verdict)),
            _ => KeyFor(outcome.Failure) is { } key ? localizer[key] : string.Empty,
        };
    }

    private static string DescribeProblems(LocalizationManager localizer, IReadOnlyList<RecipeError> errors)
    {
        List<string> lines = [localizer[RefusedIntroKey]];
        foreach (RecipeError error in errors.Take(MaxListedProblems))
        {
            lines.Add(Bullet(localizer, Describe(localizer, error)));
        }

        int rest = errors.Count - MaxListedProblems;
        if (rest > 0)
        {
            lines.Add(localizer.FormatCount(rest, MoreProblemsOneKey, MoreProblemsKey, rest));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Bullet(LocalizationManager localizer, string text) => localizer.Format(BulletKey, text);
}
