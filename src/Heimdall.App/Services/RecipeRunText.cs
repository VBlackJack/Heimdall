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

using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>
/// The one place that turns the data a recipe run produces into localized words: why a value was
/// refused, how a run ended, why a run was refused before it started, and what a changed program
/// means. The Core types carry codes only; every key is written out here so that a test can check
/// that each member of each enum has its text in every locale.
/// </summary>
public static class RecipeRunText
{
    /// <summary>The key of the timeout wording for exactly one second.</summary>
    public const string TimedOutOneKey = "RecipeStatusTimedOutOne";

    /// <summary>The key of the line that frames a refused run.</summary>
    public const string RefusedFrameKey = "RecipeStatusRefused";

    /// <summary>The key of the word that stands for a run that ended without a status of its own.</summary>
    public const string UnknownStatusKey = "RecipeStatusUnknown";

    /// <summary>The key of a value refusal, or null for <see cref="RecipeValueRefusal.None"/>.</summary>
    public static string? KeyFor(RecipeValueRefusal refusal) => refusal switch
    {
        RecipeValueRefusal.None => null,
        RecipeValueRefusal.Empty => "RecipeRefusalEmpty",
        RecipeValueRefusal.RequiredMissing => "RecipeRefusalRequiredMissing",
        RecipeValueRefusal.SessionValueMissing => "RecipeRefusalSessionValueMissing",
        RecipeValueRefusal.ControlCharacter => "RecipeRefusalControlCharacter",
        RecipeValueRefusal.LeadingDash => "RecipeRefusalLeadingDash",
        RecipeValueRefusal.LeadingSlash => "RecipeRefusalLeadingSlash",
        RecipeValueRefusal.LeadingAt => "RecipeRefusalLeadingAt",
        RecipeValueRefusal.UncPath => "RecipeRefusalUncPath",
        RecipeValueRefusal.DevicePath => "RecipeRefusalDevicePath",
        RecipeValueRefusal.ColonInPath => "RecipeRefusalColonInPath",
        RecipeValueRefusal.TrailingDotOrSpace => "RecipeRefusalTrailingDotOrSpace",
        RecipeValueRefusal.DeviceName => "RecipeRefusalDeviceName",
        RecipeValueRefusal.SurroundingWhitespace => "RecipeRefusalSurroundingWhitespace",
        RecipeValueRefusal.NotAnInteger => "RecipeRefusalNotAnInteger",
        RecipeValueRefusal.NotANumber => "RecipeRefusalNotANumber",
        RecipeValueRefusal.NotABoolean => "RecipeRefusalNotABoolean",
        RecipeValueRefusal.RootedPath => "RecipeRefusalRootedPath",
        RecipeValueRefusal.SeparatorJoin => "RecipeRefusalSeparatorJoin",
        RecipeValueRefusal.ForbiddenSequenceInArgument => "RecipeRefusalForbiddenSequenceInArgument",
        RecipeValueRefusal.InvalidHostname => "RecipeRefusalInvalidHostname",
        RecipeValueRefusal.InvalidIpAddress => "RecipeRefusalInvalidIpAddress",
        RecipeValueRefusal.InvalidPort => "RecipeRefusalInvalidPort",
        RecipeValueRefusal.TooLong => "RecipeRefusalTooLong",
        RecipeValueRefusal.PatternMismatch => "RecipeRefusalPatternMismatch",
        RecipeValueRefusal.NotInChoices => "RecipeRefusalNotInChoices",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "A refusal without a text."),
    };

    /// <summary>
    /// The key of the wording of a run status. For <see cref="RecipeRunStatus.TimedOut"/> this is
    /// the plural wording; <see cref="TimedOutOneKey"/> is the singular.
    /// </summary>
    public static string KeyFor(RecipeRunStatus status) => status switch
    {
        RecipeRunStatus.Succeeded => "RecipeStatusSucceeded",
        RecipeRunStatus.FailedExitCode => "RecipeStatusFailedExitCode",
        RecipeRunStatus.TimedOut => "RecipeStatusTimedOut",
        RecipeRunStatus.Cancelled => "RecipeStatusCancelled",
        RecipeRunStatus.FailedToStart => "RecipeStatusFailedToStart",
        RecipeRunStatus.Refused => RefusedFrameKey,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "A status without a text."),
    };

    /// <summary>The key of a run refusal, or null for <see cref="RecipeRunRefusal.None"/>.</summary>
    public static string? KeyFor(RecipeRunRefusal refusal) => refusal switch
    {
        RecipeRunRefusal.None => null,
        RecipeRunRefusal.ProgramNotAbsolute => "RecipeRunRefusalProgramNotAbsolute",
        RecipeRunRefusal.WorkingDirectoryNotAbsolute => "RecipeRunRefusalWorkingDirectoryNotAbsolute",
        RecipeRunRefusal.WorkingDirectoryMissing => "RecipeRunRefusalWorkingDirectoryMissing",
        RecipeRunRefusal.WorkingDirectoryNotEmpty => "RecipeRunRefusalWorkingDirectoryNotEmpty",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "A refusal without a text."),
    };

    /// <summary>The key of what a changed program means, or null for <see cref="RecipeProgramComparison.Same"/>.</summary>
    public static string? KeyFor(RecipeProgramComparison comparison) => comparison switch
    {
        RecipeProgramComparison.Same => null,
        RecipeProgramComparison.Different => "RecipeProgramDifferent",
        RecipeProgramComparison.NoLongerResolves => "RecipeProgramNoLongerResolves",
        _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, "A comparison without a text."),
    };

    /// <summary>The words for a refused value; empty for <see cref="RecipeValueRefusal.None"/>.</summary>
    public static string Describe(LocalizationManager localizer, RecipeValueRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return KeyFor(refusal) is { } key ? localizer[key] : string.Empty;
    }

    /// <summary>
    /// The words for a changed program. They name the path stored at import and say the recipe
    /// must be imported again; empty for <see cref="RecipeProgramComparison.Same"/>.
    /// </summary>
    public static string Describe(LocalizationManager localizer, RecipeProgramComparison comparison, string storedPath)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(storedPath);
        return KeyFor(comparison) is { } key ? localizer.Format(key, storedPath) : string.Empty;
    }

    /// <summary>The words for how a run ended.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="result">The outcome of the run.</param>
    /// <param name="timeoutSeconds">The timeout of the recipe, named when the run timed out.</param>
    public static string Describe(LocalizationManager localizer, RecipeRunResult result, int timeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(result);

        switch (result.Status)
        {
            case RecipeRunStatus.FailedExitCode:
                return localizer.Format(KeyFor(result.Status), result.ExitCode ?? 0);
            case RecipeRunStatus.TimedOut:
                return localizer.FormatCount(timeoutSeconds, TimedOutOneKey, KeyFor(result.Status), timeoutSeconds);
            case RecipeRunStatus.Refused:
                string reason = KeyFor(result.Refusal) is { } key ? localizer[key] : localizer[UnknownStatusKey];
                return localizer.Format(RefusedFrameKey, reason);
            default:
                return localizer[KeyFor(result.Status)];
        }
    }
}
