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

using System.Text.Json;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace TwinShell.Core.Services;

/// <summary>
/// Shows an action in the operator's language: replaces the pivot text with a translation,
/// field by field, on a copy.
/// </summary>
/// <remarks>
/// <para>
/// Applied at presentation only. An action handed to a save, an export or a sync is always
/// the pivot with its translations beside it; a localized copy written back would turn the
/// French into the pivot and lose the English for good.
/// </para>
/// <para>
/// The copy is deep because the caller binds it: a parameter label rewritten in place would
/// rewrite the stored action the repository handed out, and the next culture switch would
/// start from the wrong text.
/// </para>
/// </remarks>
public static class ActionLocalizer
{
    /// <summary>The culture the pivot text is written in.</summary>
    public const string PivotCulture = "en";

    // A copy through the serializer follows every property the model gains without this
    // class having to be told, which a hand-written member copy would not.
    private static readonly JsonSerializerOptions CopyOptions = JsonOptionsHelper.CompactStorage;

    /// <summary>
    /// Returns <paramref name="action"/> in <paramref name="culture"/>.
    /// </summary>
    /// <param name="action">The stored action, pivot text and translations.</param>
    /// <param name="culture">
    /// The culture to show, two-letter or longer ("fr", "fr-FR"); only the language part is
    /// used.
    /// </param>
    /// <returns>
    /// A deep copy carrying the translated text where the translation has some and the pivot
    /// text everywhere else; or <paramref name="action"/> itself, not a copy, when the culture
    /// is the pivot or the action has no translation for it.
    /// </returns>
    public static ActionModel Localize(ActionModel action, string? culture)
    {
        ArgumentNullException.ThrowIfNull(action);

        string language = ActionTranslationsJsonConverter.NormalizeCulture(culture);
        if (language.Length == 0 || string.Equals(language, PivotCulture, StringComparison.Ordinal))
        {
            return action;
        }

        ActionTranslation? translation = action.Translations?.LastOrDefault(candidate =>
            string.Equals(
                ActionTranslationsJsonConverter.NormalizeCulture(candidate.CultureCode),
                language,
                StringComparison.Ordinal));
        if (translation is null)
        {
            return action;
        }

        ActionModel copy = DeepCopy(action);

        copy.Title = Pick(translation.Title, copy.Title)!;
        copy.Description = Pick(translation.Description, copy.Description)!;
        copy.Notes = Pick(translation.Notes, copy.Notes);

        ApplyParameters(copy.WindowsCommandTemplate, translation.WindowsParameters);
        ApplyParameters(copy.LinuxCommandTemplate, translation.LinuxParameters);

        ApplyExamples(copy.Examples, translation.ExampleDescriptions);
        ApplyExamples(copy.WindowsExamples, translation.ExampleDescriptions);
        ApplyExamples(copy.LinuxExamples, translation.ExampleDescriptions);

        return copy;
    }

    /// <summary>
    /// Returns every action of <paramref name="actions"/> in <paramref name="culture"/>, in the
    /// same order. See <see cref="Localize"/>.
    /// </summary>
    public static List<ActionModel> LocalizeAll(IEnumerable<ActionModel> actions, string? culture)
    {
        ArgumentNullException.ThrowIfNull(actions);

        return actions.Select(action => Localize(action, culture)).ToList();
    }

    private static ActionModel DeepCopy(ActionModel action)
    {
        string json = JsonSerializer.Serialize(action, CopyOptions);
        ActionModel copy = JsonSerializer.Deserialize<ActionModel>(json, CopyOptions)
            ?? throw new InvalidOperationException("An action copy came back empty.");

        // Neither id is guaranteed to survive a round trip unchanged in shape, and both are
        // identity: carry them across as they are.
        copy.Id = action.Id;
        copy.PublicId = action.PublicId;
        return copy;
    }

    private static void ApplyParameters(
        CommandTemplate? template,
        Dictionary<string, ParameterTranslation>? translations)
    {
        if (template is null || translations is not { Count: > 0 })
        {
            return;
        }

        foreach (TemplateParameter parameter in template.Parameters)
        {
            if (!translations.TryGetValue(parameter.Name, out ParameterTranslation? translated))
            {
                continue;
            }

            parameter.Label = Pick(translated.Label, parameter.Label)!;
            parameter.Description = Pick(translated.Description, parameter.Description);
        }
    }

    private static void ApplyExamples(
        List<CommandExample>? examples,
        Dictionary<string, string>? descriptions)
    {
        if (examples is null || descriptions is not { Count: > 0 })
        {
            return;
        }

        foreach (CommandExample example in examples)
        {
            if (descriptions.TryGetValue(example.Command, out string? description))
            {
                example.Description = Pick(description, example.Description)!;
            }
        }
    }

    private static string? Pick(string? translated, string? pivot)
        => string.IsNullOrWhiteSpace(translated) ? pivot : translated;
}
