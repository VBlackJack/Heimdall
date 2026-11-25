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
using System.Text;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>
/// Writes the text of the import preview, the screen the user reads before a recipe is stored. It
/// is the real security barrier of the feature, so it says things in plain words and never as one
/// formatted command line: the resolved program on a line of its own, then every argument token on
/// a line of its own, exactly as written in the file, placeholders visible. Everything that comes
/// from the recipe is shown as plain text with the characters that could hide something escaped;
/// nothing from the recipe is looked up as a locale key.
/// </summary>
public static class RecipeImportPreview
{
    /// <summary>The key of the title of the dialog of a new recipe.</summary>
    public const string ImportTitleKey = "RecipeImportPreviewTitle";

    /// <summary>The key of the title of the dialog of a replacement.</summary>
    public const string ReplaceTitleKey = "RecipeImportReplaceTitle";

    /// <summary>The key of the label of the button that imports a new recipe.</summary>
    public const string ImportConfirmKey = "RecipeImportConfirm";

    /// <summary>The key of the label of the button that replaces a recipe.</summary>
    public const string ReplaceConfirmKey = "RecipeImportReplaceConfirm";

    /// <summary>The key of the label of the button that cancels; it is the one Enter takes.</summary>
    public const string CancelKey = "BtnCancel";

    /// <summary>The unit of the indentation of the lines under a heading.</summary>
    private const string Indent = "  ";

    /// <summary>The key of the title of the dialog for the plan.</summary>
    /// <param name="plan">The plan.</param>
    public static string TitleKey(RecipeImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsReplace ? ReplaceTitleKey : ImportTitleKey;
    }

    /// <summary>The key of the label of the confirming button for the plan.</summary>
    /// <param name="plan">The plan.</param>
    public static string ConfirmKey(RecipeImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.IsReplace ? ReplaceConfirmKey : ImportConfirmKey;
    }

    /// <summary>The whole preview text for a plan.</summary>
    /// <param name="localizer">The localizer of the current language.</param>
    /// <param name="plan">The recipe that would be imported.</param>
    public static string Compose(LocalizationManager localizer, RecipeImportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(plan);

        Recipe recipe = plan.Recipe;
        StringBuilder text = new();

        text.AppendLine(localizer["RecipeImportPreviewWarning"]);
        text.AppendLine();

        if (plan.IsReplace)
        {
            AppendReplacement(localizer, text, plan);
            text.AppendLine();
        }

        // What decides the answer comes first, because the dialog is small and scrolls: the program,
        // then its arguments, then who wrote it and the rest.
        text.AppendLine(localizer["RecipeImportPreviewProgram"]);
        text.AppendLine(Indent + Exact(plan.ResolvedProgramPath));
        text.AppendLine(Indent + localizer.Format("RecipeImportPreviewProgramAsWritten", Exact(recipe.Program)));
        text.AppendLine();

        AppendArguments(localizer, text, recipe);
        text.AppendLine();

        text.AppendLine(localizer.Format("RecipeImportPreviewName", Plain(recipe.Name)));
        text.AppendLine(localizer.Format("RecipeImportPreviewAuthor", Plain(recipe.Author)));
        text.AppendLine(localizer.Format("RecipeImportPreviewId", Plain(recipe.Id)));
        text.AppendLine(localizer.Format("RecipeImportPreviewVersion", Plain(recipe.Version)));
        text.AppendLine(localizer.Format("RecipeImportPreviewDescription", Plain(recipe.Description)));
        text.AppendLine();

        AppendInputs(localizer, text, recipe);
        text.AppendLine();

        AppendTranslations(localizer, text, recipe);
        text.AppendLine();

        text.AppendLine(localizer.Format(
            "RecipeImportPreviewSession",
            YesNo(localizer, plan.SessionUse.UsesHost),
            YesNo(localizer, plan.SessionUse.UsesPort)));
        text.AppendLine(DescribeOutput(localizer, recipe));
        text.AppendLine(localizer.Format("RecipeImportPreviewEncoding", localizer[KeyFor(recipe.OutputEncoding)]));
        text.AppendLine(localizer.Format("RecipeImportPreviewStderr", localizer[KeyFor(recipe.Stderr)]));
        text.AppendLine(localizer.Format(
            "RecipeImportPreviewExitCodes",
            string.Join(
                localizer["RecipeImportPreviewListSeparator"],
                recipe.SuccessExitCodes.Select(code => code.ToString(CultureInfo.InvariantCulture)))));
        text.AppendLine(DescribeIcon(localizer, recipe.Icon));
        text.Append(localizer.FormatCount(
            recipe.TimeoutSeconds,
            "RecipeImportPreviewTimeoutOne",
            "RecipeImportPreviewTimeout",
            recipe.TimeoutSeconds));

        return text.ToString();
    }

    /// <summary>The key of the word that stands for a type of input.</summary>
    /// <param name="type">The type.</param>
    public static string KeyFor(RecipeInputType type) => type switch
    {
        RecipeInputType.String => "RecipeImportInputTypeString",
        RecipeInputType.Int => "RecipeImportInputTypeInt",
        RecipeInputType.Number => "RecipeImportInputTypeNumber",
        RecipeInputType.Bool => "RecipeImportInputTypeBool",
        RecipeInputType.Hostname => "RecipeImportInputTypeHostname",
        RecipeInputType.IpAddress => "RecipeImportInputTypeIpAddress",
        RecipeInputType.Path => "RecipeImportInputTypePath",
        RecipeInputType.Choice => "RecipeImportInputTypeChoice",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "An input type without a text."),
    };

    /// <summary>The key of the words that say how the output is shown.</summary>
    /// <param name="format">The output format.</param>
    public static string KeyFor(RecipeOutputFormat format) => format switch
    {
        RecipeOutputFormat.Text => "RecipeImportOutputText",
        RecipeOutputFormat.Csv => "RecipeImportOutputCsv",
        RecipeOutputFormat.Json => "RecipeImportOutputJson",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "An output format without a text."),
    };

    /// <summary>The key of the words that name an output encoding.</summary>
    /// <param name="encoding">The encoding.</param>
    public static string KeyFor(RecipeOutputEncoding encoding) => encoding switch
    {
        RecipeOutputEncoding.Utf8 => "RecipeImportEncodingUtf8",
        RecipeOutputEncoding.Oem => "RecipeImportEncodingOem",
        RecipeOutputEncoding.Ansi => "RecipeImportEncodingAnsi",
        RecipeOutputEncoding.Utf16Le => "RecipeImportEncodingUtf16Le",
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "An output encoding without a text."),
    };

    /// <summary>The key of the words that name the separator of a table read from CSV.</summary>
    /// <param name="separator">The separator.</param>
    public static string KeyFor(RecipeCsvSeparator separator) => separator switch
    {
        RecipeCsvSeparator.Comma => "RecipeImportCsvSeparatorComma",
        RecipeCsvSeparator.Semicolon => "RecipeImportCsvSeparatorSemicolon",
        RecipeCsvSeparator.Tab => "RecipeImportCsvSeparatorTab",
        _ => throw new ArgumentOutOfRangeException(nameof(separator), separator, "A separator without a text."),
    };

    /// <summary>The key of the words that say what happens to standard error.</summary>
    /// <param name="mode">The mode.</param>
    public static string KeyFor(RecipeStderrMode mode) => mode switch
    {
        RecipeStderrMode.Separate => "RecipeImportStderrSeparate",
        RecipeStderrMode.Merge => "RecipeImportStderrMerge",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A standard error mode without a text."),
    };

    private static string OutputWords(LocalizationManager localizer, RecipeOutputFormat format) => localizer[KeyFor(format)];

    // A table read from CSV also says which character separates its cells: it decides how the output is cut.
    private static string DescribeOutput(LocalizationManager localizer, Recipe recipe)
        => recipe.Output == RecipeOutputFormat.Csv
            ? localizer.Format("RecipeImportPreviewOutputSeparated", OutputWords(localizer, recipe.Output), localizer[KeyFor(recipe.CsvSeparator)])
            : localizer.Format("RecipeImportPreviewOutput", OutputWords(localizer, recipe.Output));

    // An icon key is any word of the right shape: one this version has no glyph for is said so, and the default glyph is shown.
    private static string DescribeIcon(LocalizationManager localizer, string? icon)
    {
        if (icon is null)
        {
            return localizer.Format("RecipeImportPreviewIcon", localizer["RecipeImportPreviewNone"]);
        }

        return localizer.Format(RecipeIcons.IsKnown(icon) ? "RecipeImportPreviewIcon" : "RecipeImportPreviewIconUnknown", Exact(icon));
    }

    private static string YesNo(LocalizationManager localizer, bool value) => localizer[value ? "BtnYes" : "BtnNo"];

    /// <summary>Text from the recipe for a place where look-alike letters are harmless: only the characters that could hide something are escaped.</summary>
    private static string Plain(string? text) => RecipeText.Escape(text, escapeNonAscii: false);

    /// <summary>Text from the recipe for a program or an argument: everything outside printable ASCII is escaped, so a look-alike letter cannot pass for the real one.</summary>
    private static string Exact(string? text) => RecipeText.Escape(text, escapeNonAscii: true);

    private static string Token(LocalizationManager localizer, string key, int number, string token)
        => localizer.Format(key, number, Exact(token));

    private static void AppendReplacement(LocalizationManager localizer, StringBuilder text, RecipeImportPlan plan)
    {
        text.AppendLine(localizer["RecipeImportReplaceHeading"]);

        if (plan.Previous is not { } previous)
        {
            text.AppendLine(Indent + localizer["RecipeImportReplaceUnreadable"]);
        }
        else
        {
            text.AppendLine(Indent + localizer.Format("RecipeImportReplaceVersion", Plain(previous.Version), Plain(plan.Recipe.Version)));
            text.AppendLine(Indent + localizer[SameArguments(previous, plan.Recipe)
                ? "RecipeImportReplaceArgumentsSame"
                : "RecipeImportReplaceArgumentsChanged"]);
            text.AppendLine(Indent + localizer[SameFields(previous, plan.Recipe)
                ? "RecipeImportReplaceFieldsSame"
                : "RecipeImportReplaceFieldsChanged"]);
        }

        string? oldPath = plan.Existing?.ResolvedProgramPath;
        text.AppendLine(Indent + (string.Equals(oldPath, plan.ResolvedProgramPath, StringComparison.OrdinalIgnoreCase)
            ? localizer["RecipeImportReplaceProgramSame"]
            : localizer.Format("RecipeImportReplaceProgramChanged", Exact(oldPath), Exact(plan.ResolvedProgramPath))));

        if (plan.Previous is { } old)
        {
            AppendUnchangedOrChanged(localizer, text, string.Equals(old.Author, plan.Recipe.Author, StringComparison.Ordinal), "RecipeImportReplaceAuthorSame", "RecipeImportReplaceAuthorChanged");
            AppendUnchangedOrChanged(localizer, text, string.Equals(old.Name, plan.Recipe.Name, StringComparison.Ordinal), "RecipeImportReplaceNameSame", "RecipeImportReplaceNameChanged");
            AppendUnchangedOrChanged(localizer, text, string.Equals(old.Description, plan.Recipe.Description, StringComparison.Ordinal), "RecipeImportReplaceDescriptionSame", "RecipeImportReplaceDescriptionChanged");
            AppendUnchangedOrChanged(localizer, text, SameTranslations(old, plan.Recipe), "RecipeImportReplaceTranslationsSame", "RecipeImportReplaceTranslationsChanged");
            AppendUnchangedOrChanged(
                localizer,
                text,
                old.Output == plan.Recipe.Output && old.OutputEncoding == plan.Recipe.OutputEncoding && old.CsvSeparator == plan.Recipe.CsvSeparator,
                "RecipeImportReplaceOutputSame",
                "RecipeImportReplaceOutputChanged");
            AppendUnchangedOrChanged(localizer, text, old.Stderr == plan.Recipe.Stderr, "RecipeImportReplaceStderrSame", "RecipeImportReplaceStderrChanged");
            AppendUnchangedOrChanged(localizer, text, old.SuccessExitCodes.SequenceEqual(plan.Recipe.SuccessExitCodes), "RecipeImportReplaceExitCodesSame", "RecipeImportReplaceExitCodesChanged");
            AppendUnchangedOrChanged(localizer, text, old.TimeoutSeconds == plan.Recipe.TimeoutSeconds, "RecipeImportReplaceTimeoutSame", "RecipeImportReplaceTimeoutChanged");
            AppendUnchangedOrChanged(localizer, text, string.Equals(old.Icon, plan.Recipe.Icon, StringComparison.Ordinal), "RecipeImportReplaceIconSame", "RecipeImportReplaceIconChanged");
        }
    }

    // One line per property, and only the words "unchanged" or "changed": what changed is read in the
    // preview below, which shows the new recipe whole.
    private static void AppendUnchangedOrChanged(LocalizationManager localizer, StringBuilder text, bool same, string sameKey, string changedKey)
        => text.AppendLine(Indent + localizer[same ? sameKey : changedKey]);

    /// <summary>Whether two recipes carry the same translations, compared as parsed: every language, name, description and field label.</summary>
    private static bool SameTranslations(Recipe left, Recipe right)
        => left.Translations.Count == right.Translations.Count
            && left.Translations.All(pair =>
                right.Translations.TryGetValue(pair.Key, out RecipeTranslation? other)
                && string.Equals(pair.Value.Name, other.Name, StringComparison.Ordinal)
                && string.Equals(pair.Value.Description, other.Description, StringComparison.Ordinal)
                && SameTexts(pair.Value.FieldLabels, other.FieldLabels)
                && SameTexts(pair.Value.FieldHints, other.FieldHints));

    private static bool SameTexts(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count
            && left.All(entry =>
                right.TryGetValue(entry.Key, out string? text)
                && string.Equals(entry.Value, text, StringComparison.Ordinal));

    /// <summary>Whether two recipes pass the same arguments, compared as parsed (groups and tokens), not as bytes.</summary>
    private static bool SameArguments(Recipe left, Recipe right)
        => left.Arguments.Count == right.Arguments.Count
            && left.Arguments.Zip(right.Arguments).All(pair =>
                pair.First.IsGroup == pair.Second.IsGroup
                && pair.First.Tokens.SequenceEqual(pair.Second.Tokens, StringComparer.Ordinal));

    /// <summary>Whether two recipes ask for the same fields, compared as parsed (every property, the lists item by item).</summary>
    private static bool SameFields(Recipe left, Recipe right)
        => left.Inputs.Count == right.Inputs.Count
            && left.Inputs.Zip(right.Inputs).All(pair =>
                pair.First.Name == pair.Second.Name
                && pair.First.Type == pair.Second.Type
                && pair.First.Label == pair.Second.Label
                && pair.First.Required == pair.Second.Required
                && pair.First.Default == pair.Second.Default
                && pair.First.Pattern == pair.Second.Pattern
                && pair.First.MaxLength == pair.Second.MaxLength
                && (pair.First.Choices ?? []).SequenceEqual(pair.Second.Choices ?? [], StringComparer.Ordinal)
                && (pair.First.WhenTrue ?? []).SequenceEqual(pair.Second.WhenTrue ?? [], StringComparer.Ordinal)
                && (pair.First.WhenFalse ?? []).SequenceEqual(pair.Second.WhenFalse ?? [], StringComparer.Ordinal)
                && pair.First.Min == pair.Second.Min
                && pair.First.Max == pair.Second.Max
                && pair.First.Hint == pair.Second.Hint);

    private static void AppendArguments(LocalizationManager localizer, StringBuilder text, Recipe recipe)
    {
        text.AppendLine(localizer["RecipeImportPreviewArgumentsHeading"]);
        if (recipe.Arguments.Count == 0)
        {
            text.AppendLine(Indent + localizer["RecipeImportPreviewNone"]);
            return;
        }

        int number = 0;
        foreach (RecipeArgument argument in recipe.Arguments)
        {
            if (!argument.IsGroup)
            {
                foreach (string token in argument.Tokens)
                {
                    number++;
                    text.AppendLine(Token(localizer, "RecipeImportPreviewArgument", number, token));
                }

                continue;
            }

            text.AppendLine(localizer["RecipeImportPreviewGroup"]);
            foreach (string token in argument.Tokens)
            {
                number++;
                text.AppendLine(Token(localizer, "RecipeImportPreviewGroupArgument", number, token));
            }
        }
    }

    // Every translation is shown, because the view shows it instead of the English text when the
    // interface is in that language: a label that was never read here could say anything.
    private static void AppendTranslations(LocalizationManager localizer, StringBuilder text, Recipe recipe)
    {
        text.AppendLine(localizer["RecipeImportPreviewTranslationsHeading"]);
        if (recipe.Translations.Count == 0)
        {
            text.AppendLine(Indent + localizer["RecipeImportPreviewNone"]);
            return;
        }

        foreach ((string tag, RecipeTranslation translation) in recipe.Translations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.AppendLine(localizer.Format("RecipeImportPreviewTranslationsLanguage", Plain(tag)));
            if (translation.Name is not null)
            {
                text.AppendLine(localizer.Format("RecipeImportPreviewTranslationsName", Plain(translation.Name)));
            }

            if (translation.Description is not null)
            {
                text.AppendLine(localizer.Format("RecipeImportPreviewTranslationsDescription", Plain(translation.Description)));
            }

            foreach ((string field, string label) in translation.FieldLabels.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                text.AppendLine(localizer.Format("RecipeImportPreviewTranslationsFieldLabel", Exact(field), Plain(label)));
            }

            foreach ((string field, string hint) in translation.FieldHints.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                text.AppendLine(localizer.Format("RecipeImportPreviewTranslationsFieldHint", Exact(field), Plain(hint)));
            }
        }
    }

    private static void AppendInputs(LocalizationManager localizer, StringBuilder text, Recipe recipe)
    {
        text.AppendLine(localizer["RecipeImportPreviewInputsHeading"]);
        if (recipe.Inputs.Count == 0)
        {
            text.AppendLine(Indent + localizer["RecipeImportPreviewNone"]);
            return;
        }

        foreach (RecipeInput input in recipe.Inputs)
        {
            List<string> details = [localizer.Format("RecipeImportPreviewInputType", localizer[KeyFor(input.Type)])];

            // A switch is never required and never optional: it is on or off, so it says neither.
            if (input.Type != RecipeInputType.Bool)
            {
                details.Add(localizer[input.Required ? "RecipeImportPreviewInputRequired" : "RecipeImportPreviewInputOptional"]);
            }

            if (!string.IsNullOrEmpty(input.Default))
            {
                details.Add(localizer.Format("RecipeImportPreviewInputDefault", Exact(input.Default)));
            }

            if (input.Choices is { Count: > 0 } choices)
            {
                details.Add(localizer.Format(
                    "RecipeImportPreviewInputChoices",
                    string.Join(localizer["RecipeImportPreviewListSeparator"], choices.Select(Exact))));
            }

            if (!string.IsNullOrEmpty(input.Pattern))
            {
                details.Add(localizer.Format("RecipeImportPreviewInputPattern", Exact(input.Pattern)));
            }

            if (input.MaxLength is { } maxLength)
            {
                details.Add(localizer.Format("RecipeImportPreviewInputMaxLength", maxLength.ToString(CultureInfo.CurrentCulture)));
            }

            // The bounds are shown as written in the file.
            if (input.Min is not null && input.Max is not null)
            {
                details.Add(localizer.Format("RecipeImportPreviewInputRange", Exact(input.Min), Exact(input.Max)));
            }
            else if (input.Min is not null)
            {
                details.Add(localizer.Format("RecipeImportPreviewInputMinimum", Exact(input.Min)));
            }
            else if (input.Max is not null)
            {
                details.Add(localizer.Format("RecipeImportPreviewInputMaximum", Exact(input.Max)));
            }

            text.AppendLine(localizer.Format(
                "RecipeImportPreviewInput",
                Exact(input.Name),
                Plain(input.Label),
                string.Join(localizer["RecipeImportPreviewListSeparator"], details)));

            if (!string.IsNullOrEmpty(input.Hint))
            {
                text.AppendLine(localizer.Format("RecipeImportPreviewInputHint", Plain(input.Hint)));
            }

            if (input.WhenTrue is { Count: > 0 } whenTrue)
            {
                text.AppendLine(localizer["RecipeImportPreviewWhenTrue"]);
                foreach (string token in whenTrue)
                {
                    text.AppendLine(localizer.Format("RecipeImportPreviewWhenTrueArgument", Exact(token)));
                }
            }

            if (input.WhenFalse is { Count: > 0 } whenFalse)
            {
                text.AppendLine(localizer["RecipeImportPreviewWhenFalse"]);
                foreach (string token in whenFalse)
                {
                    text.AppendLine(localizer.Format("RecipeImportPreviewWhenTrueArgument", Exact(token)));
                }
            }
        }
    }
}
