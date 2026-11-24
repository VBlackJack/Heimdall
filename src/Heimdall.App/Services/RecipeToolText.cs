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
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>The texts of a recipe in the language of the user, with the English base as the fallback.</summary>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
/// <param name="FieldLabels">The label of each input, keyed by input name.</param>
public sealed record RecipeTexts(string Name, string Description, IReadOnlyDictionary<string, string> FieldLabels);

/// <summary>
/// What every surface says about a tool that comes from a recipe: the recipe's own name, the word
/// "Recipe" and the author marked as unverified, in one short localized format. Written once so the
/// Tools tab card, the sidebar row, the palette row and the tab cannot drift apart. A recipe is
/// text written by someone else: none of it is looked up as a locale key or read as markup.
/// </summary>
public static class RecipeToolText
{
    /// <summary>The key of the provenance format, for example "Recipe by {0} (unverified)".</summary>
    public const string ProvenanceKey = "RecipeProvenanceFormat";

    /// <summary>The key of the format that joins a name and its provenance into a tab title.</summary>
    public const string TabTitleKey = "RecipeTabTitleFormat";

    /// <summary>The key of the format that joins a name and its provenance into an accessible name.</summary>
    public const string AccessibleNameKey = "RecipeAccessibleNameFormat";

    /// <summary>The key of the heading of the Tools tab group the recipe tools are listed under.</summary>
    public const string GroupHeadingKey = "SettingsToolPackRecipesLabel";

    private const char LanguageSeparator = '-';

    /// <summary>"Recipe by Acme (unverified)": who wrote it, never vouched for.</summary>
    /// <param name="author">The author written in the recipe file.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    public static string Provenance(string author, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        return Format(localize(ProvenanceKey), author ?? string.Empty);
    }

    /// <summary>The provenance of a recipe tool, or null for any other tool.</summary>
    /// <param name="descriptor">The descriptor of a tool.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    public static string? ProvenanceOf(ToolDescriptor descriptor, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.RecipeAuthor is { } author ? Provenance(author, localize) : null;
    }

    /// <summary>The name of a tool as an assistive technology should read it.</summary>
    /// <param name="descriptor">The descriptor of a tool.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    public static string AccessibleName(ToolDescriptor descriptor, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localize);

        string name = descriptor.ResolveLabel(localize);
        return ProvenanceOf(descriptor, localize) is { } provenance
            ? Format(localize(AccessibleNameKey), name, provenance)
            : name;
    }

    /// <summary>The title of the tab of a tool: the name, then the provenance for a recipe.</summary>
    /// <param name="descriptor">The descriptor of a tool.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    public static string TabTitle(ToolDescriptor descriptor, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localize);

        string name = descriptor.ResolveLabel(localize);
        return ProvenanceOf(descriptor, localize) is { } provenance
            ? Format(localize(TabTitleKey), name, provenance)
            : name;
    }

    /// <summary>
    /// The texts of a recipe for a language: the translation whose tag equals the language, else the
    /// first whose primary subtag does, else the English base. A missing part falls back by itself.
    /// </summary>
    /// <param name="recipe">The recipe.</param>
    /// <param name="language">The tag of the current UI language, for example <c>fr</c>.</param>
    public static RecipeTexts Resolve(Recipe recipe, string language)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        RecipeLabels? labels = PickLabels(recipe, language);
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (RecipeInput input in recipe.Inputs)
        {
            fields[input.Name] = labels is not null
                && labels.Fields.TryGetValue(input.Name, out string? translated)
                && !string.IsNullOrWhiteSpace(translated)
                    ? translated
                    : input.Label;
        }

        return new RecipeTexts(
            !string.IsNullOrWhiteSpace(labels?.Name) ? labels.Name : recipe.Name,
            !string.IsNullOrWhiteSpace(labels?.Description) ? labels.Description : recipe.Description,
            fields);
    }

    private static RecipeLabels? PickLabels(Recipe recipe, string language)
    {
        if (recipe.Labels.Count == 0 || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        foreach ((string tag, RecipeLabels labels) in recipe.Labels)
        {
            if (string.Equals(tag, language, StringComparison.OrdinalIgnoreCase))
            {
                return labels;
            }
        }

        string primary = PrimarySubtag(language);
        foreach (string tag in recipe.Labels.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(PrimarySubtag(tag), primary, StringComparison.OrdinalIgnoreCase))
            {
                return recipe.Labels[tag];
            }
        }

        return null;
    }

    private static string PrimarySubtag(string tag)
    {
        int separator = tag.IndexOf(LanguageSeparator, StringComparison.Ordinal);
        return separator < 0 ? tag : tag[..separator];
    }

    private static string Format(string template, params object[] arguments)
    {
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            // A malformed template in a locale file reads as itself rather than failing the surface.
            return template;
        }
    }
}
