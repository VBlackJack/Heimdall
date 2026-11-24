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

using System.Collections.Frozen;

namespace Heimdall.Core.Recipes;

/// <summary>
/// The fixed vocabulary of the recipe file format: version, extension, property names, the closed
/// sets and the placeholder syntax. Everything here is part of the public file contract.
/// </summary>
public static class RecipeFormat
{
    /// <summary>The highest <c>schemaVersion</c> this build reads. Any addition to the format bumps it.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>The extension of a recipe file.</summary>
    public const string FileExtension = ".heimdall-recipe.json";

    /// <summary>Separator between the author part and the name part of a recipe id.</summary>
    public const char IdSeparator = '.';

    /// <summary>The session placeholder that stands for the selected session's host.</summary>
    public const string HostPlaceholder = "Host";

    /// <summary>The session placeholder that stands for the selected session's port.</summary>
    public const string PortPlaceholder = "Port";

    /// <summary>The prefix of a placeholder that names a recipe input, up to the colon.</summary>
    public const string InputPlaceholderPrefix = "in:";

    /// <summary>The character that opens a placeholder; doubled, it is a literal brace.</summary>
    public const char PlaceholderOpen = '{';

    /// <summary>The character that closes a placeholder; doubled, it is a literal brace.</summary>
    public const char PlaceholderClose = '}';

    /// <summary>The spelling of a true boolean value, in a default and in a run value.</summary>
    public const string TrueText = "true";

    /// <summary>The spelling of a false boolean value, in a default and in a run value.</summary>
    public const string FalseText = "false";

    /// <summary>The file names of the types of input, as written in a recipe.</summary>
    internal static readonly FrozenDictionary<string, RecipeInputType> InputTypeNames =
        new Dictionary<string, RecipeInputType>(StringComparer.Ordinal)
        {
            ["string"] = RecipeInputType.String,
            ["int"] = RecipeInputType.Int,
            ["number"] = RecipeInputType.Number,
            ["bool"] = RecipeInputType.Bool,
            ["hostname"] = RecipeInputType.Hostname,
            ["ipaddress"] = RecipeInputType.IpAddress,
            ["path"] = RecipeInputType.Path,
            ["choice"] = RecipeInputType.Choice,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The file names of the output formats.</summary>
    internal static readonly FrozenDictionary<string, RecipeOutputFormat> OutputFormatNames =
        new Dictionary<string, RecipeOutputFormat>(StringComparer.Ordinal)
        {
            ["text"] = RecipeOutputFormat.Text,
            ["csv"] = RecipeOutputFormat.Csv,
            ["json"] = RecipeOutputFormat.Json,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The file names of the output encodings.</summary>
    internal static readonly FrozenDictionary<string, RecipeOutputEncoding> OutputEncodingNames =
        new Dictionary<string, RecipeOutputEncoding>(StringComparer.Ordinal)
        {
            ["utf8"] = RecipeOutputEncoding.Utf8,
            ["oem"] = RecipeOutputEncoding.Oem,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The file names of the standard error modes.</summary>
    internal static readonly FrozenDictionary<string, RecipeStderrMode> StderrModeNames =
        new Dictionary<string, RecipeStderrMode>(StringComparer.Ordinal)
        {
            ["separate"] = RecipeStderrMode.Separate,
            ["merge"] = RecipeStderrMode.Merge,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>True when the text is a legal input name: a letter, then letters, digits or underscores, length-capped.</summary>
    public static bool IsInputName(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > RecipeLimits.MaxInputNameLength)
            return false;

        if (!IsAsciiLetter(text[0]))
            return false;

        for (int index = 1; index < text.Length; index++)
        {
            char c = text[index];
            if (!IsAsciiLetter(c) && !IsAsciiDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    /// <summary>True when the text is one half of a recipe id: lowercase letters, digits and hyphens, no edge hyphen.</summary>
    internal static bool IsIdPart(string text)
    {
        if (text.Length == 0 || text[0] == '-' || text[^1] == '-')
            return false;

        foreach (char c in text)
        {
            bool lower = c is >= 'a' and <= 'z';
            if (!lower && !IsAsciiDigit(c) && c != '-')
                return false;
        }

        return true;
    }

    /// <summary>True when the text looks like a language tag: letters and digits in hyphen-separated groups.</summary>
    internal static bool IsLanguageTag(string text)
    {
        if (text.Length == 0 || text.Length > RecipeLimits.MaxLanguageTagLength)
            return false;

        string[] groups = text.Split('-');
        for (int index = 0; index < groups.Length; index++)
        {
            string group = groups[index];
            int minimum = index == 0 ? RecipeLimits.MinLanguageSubtagLength : 1;
            if (group.Length < minimum || group.Length > RecipeLimits.MaxLanguageSubtagLength)
                return false;

            foreach (char c in group)
            {
                if (!IsAsciiLetter(c) && !IsAsciiDigit(c))
                    return false;

                if (index == 0 && !IsAsciiLetter(c))
                    return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
