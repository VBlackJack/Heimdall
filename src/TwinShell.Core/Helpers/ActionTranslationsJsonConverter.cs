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
using System.Text.Json.Serialization;
using TwinShell.Core.Models;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Reads and writes an action's translations as the object the shipped seeds carry under
/// <c>translations</c>: keyed by two-letter culture code, each value holding <c>title</c>,
/// <c>description</c>, <c>notes</c>, <c>parameters.windows</c> / <c>parameters.linux</c>
/// (keyed by parameter name) and <c>examples</c> (keyed by the exact example command).
/// </summary>
/// <remarks>
/// <para>
/// The property names are written here rather than taken from the serializer's naming
/// policy: the seeds, an export and a Git sync folder must all carry the same shape whatever
/// options the caller serializes with. Reading matches the names without regard to case, as
/// the import door does for everything else.
/// </para>
/// <para>
/// Empty fields are left out on write, so a translation that only carries a title writes
/// only a title; a missing field falls back to the pivot when the action is shown.
/// </para>
/// </remarks>
public sealed class ActionTranslationsJsonConverter : JsonConverter<List<ActionTranslation>>
{
    private const string TitleName = "title";
    private const string DescriptionName = "description";
    private const string NotesName = "notes";
    private const string ParametersName = "parameters";
    private const string WindowsName = "windows";
    private const string LinuxName = "linux";
    private const string LabelName = "label";
    private const string ExamplesName = "examples";

    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override List<ActionTranslation> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var translations = new List<ActionTranslation>();

        if (reader.TokenType == JsonTokenType.Null)
        {
            return translations;
        }

        ExpectToken(ref reader, JsonTokenType.StartObject);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string culture = NormalizeCulture(reader.GetString());
            reader.Read();

            if (reader.TokenType == JsonTokenType.Null)
            {
                continue;
            }

            ActionTranslation translation = ReadTranslation(ref reader, culture);

            // A culture named twice keeps its last occurrence, as an object lookup would; the
            // database holds one row per action and culture.
            translations.RemoveAll(existing =>
                string.Equals(existing.CultureCode, culture, StringComparison.Ordinal));
            translations.Add(translation);
        }

        return translations;
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        List<ActionTranslation>? value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        foreach (ActionTranslation translation in value ?? [])
        {
            if (string.IsNullOrWhiteSpace(translation.CultureCode))
            {
                continue;
            }

            writer.WritePropertyName(NormalizeCulture(translation.CultureCode));
            WriteTranslation(writer, translation);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Brings a culture name to the two-letter lower-case form the contract keys on, so
    /// "fr-FR" and "FR" both land on "fr".
    /// </summary>
    public static string NormalizeCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return string.Empty;
        }

        string trimmed = culture.Trim();
        int separator = trimmed.IndexOfAny(['-', '_']);
        string language = separator > 0 ? trimmed[..separator] : trimmed;
        return language.ToLowerInvariant();
    }

    private static ActionTranslation ReadTranslation(ref Utf8JsonReader reader, string culture)
    {
        ExpectToken(ref reader, JsonTokenType.StartObject);

        var translation = new ActionTranslation { CultureCode = culture };

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            if (Is(name, TitleName))
            {
                translation.Title = ReadString(ref reader) ?? string.Empty;
            }
            else if (Is(name, DescriptionName))
            {
                translation.Description = ReadString(ref reader) ?? string.Empty;
            }
            else if (Is(name, NotesName))
            {
                translation.Notes = ReadString(ref reader);
            }
            else if (Is(name, ParametersName) && reader.TokenType == JsonTokenType.StartObject)
            {
                ReadParameterSets(ref reader, translation);
            }
            else if (Is(name, ExamplesName) && reader.TokenType == JsonTokenType.StartObject)
            {
                ReadStringMap(ref reader, translation.ExampleDescriptions);
            }
            else
            {
                reader.Skip();
            }
        }

        return translation;
    }

    private static void ReadParameterSets(ref Utf8JsonReader reader, ActionTranslation translation)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string name = reader.GetString() ?? string.Empty;
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            if (Is(name, WindowsName))
            {
                ReadParameters(ref reader, translation.WindowsParameters);
            }
            else if (Is(name, LinuxName))
            {
                ReadParameters(ref reader, translation.LinuxParameters);
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadParameters(
        ref Utf8JsonReader reader,
        Dictionary<string, ParameterTranslation> target)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string parameterName = reader.GetString() ?? string.Empty;
            reader.Read();

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string? label = null;
            string? description = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string field = reader.GetString() ?? string.Empty;
                reader.Read();

                if (Is(field, LabelName))
                {
                    label = ReadString(ref reader);
                }
                else if (Is(field, DescriptionName))
                {
                    description = ReadString(ref reader);
                }
                else
                {
                    reader.Skip();
                }
            }

            target[parameterName] = new ParameterTranslation(label, description);
        }
    }

    private static void ReadStringMap(ref Utf8JsonReader reader, Dictionary<string, string> target)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString() ?? string.Empty;
            reader.Read();

            string? text = ReadString(ref reader);
            if (text is not null)
            {
                target[key] = text;
            }
        }
    }

    private static string? ReadString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        reader.Skip();
        return null;
    }

    private static void WriteTranslation(Utf8JsonWriter writer, ActionTranslation translation)
    {
        writer.WriteStartObject();

        WriteIfPresent(writer, TitleName, translation.Title);
        WriteIfPresent(writer, DescriptionName, translation.Description);
        WriteIfPresent(writer, NotesName, translation.Notes);

        bool hasWindows = translation.WindowsParameters is { Count: > 0 };
        bool hasLinux = translation.LinuxParameters is { Count: > 0 };
        if (hasWindows || hasLinux)
        {
            writer.WritePropertyName(ParametersName);
            writer.WriteStartObject();

            if (hasWindows)
            {
                WriteParameters(writer, WindowsName, translation.WindowsParameters);
            }

            if (hasLinux)
            {
                WriteParameters(writer, LinuxName, translation.LinuxParameters);
            }

            writer.WriteEndObject();
        }

        if (translation.ExampleDescriptions is { Count: > 0 })
        {
            writer.WritePropertyName(ExamplesName);
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> example in translation.ExampleDescriptions)
            {
                writer.WriteString(example.Key, example.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteParameters(
        Utf8JsonWriter writer,
        string platformName,
        Dictionary<string, ParameterTranslation> parameters)
    {
        writer.WritePropertyName(platformName);
        writer.WriteStartObject();

        foreach (KeyValuePair<string, ParameterTranslation> parameter in parameters)
        {
            writer.WritePropertyName(parameter.Key);
            writer.WriteStartObject();
            WriteIfPresent(writer, LabelName, parameter.Value.Label);
            WriteIfPresent(writer, DescriptionName, parameter.Value.Description);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteIfPresent(Utf8JsonWriter writer, string name, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            writer.WriteString(name, text);
        }
    }

    private static bool Is(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static void ExpectToken(ref Utf8JsonReader reader, JsonTokenType expected)
    {
        if (reader.TokenType != expected)
        {
            throw new JsonException(
                "Expected " + expected + " in translations, found " + reader.TokenType + ".");
        }
    }
}
