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

using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>One list of values a sample is expanded with, and where the session is, written in an expected file.</summary>
/// <param name="Values">The typed values by input name.</param>
/// <param name="Host">The host of the selected session, or null.</param>
/// <param name="Port">The port of the selected session, or null.</param>
internal sealed record FormatLockExpansion(IReadOnlyDictionary<string, string?> Values, string? Host, string? Port);

/// <summary>
/// The pieces of the format lock: the canonical text of a parsed recipe and of the expansions of a sample, the
/// hash of a file that does not depend on how a checkout wrote its line endings, and the place the samples live in.
/// </summary>
internal static class FormatLockSupport
{
    public const string AcceptedFolder = "accepted";

    public const string RefusedFolder = "refused";

    public const string ManifestFileName = "MANIFEST.sha256";

    public const string RecipeSuffix = ".heimdall-recipe.json";

    public const string ExpectedSuffix = ".expected.json";

    private const string Indentation = "  ";

    private const char NewLine = '\n';

    private const int FirstNonAscii = 0x7F;

    /// <summary>The folder of the version 1 samples, next to the test assembly.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Recipes", "FormatLock", "v1");

    /// <summary>The names, without folder or suffix, of the samples of one folder, in order.</summary>
    public static IReadOnlyList<string> SampleNames(string folder)
    {
        string path = Path.Combine(Root, folder);
        if (!Directory.Exists(path))
            return [];

        return [.. Directory.EnumerateFiles(path, "*" + RecipeSuffix)
            .Select(file => Path.GetFileName(file)[..^RecipeSuffix.Length])
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The SHA-256 of a file as lower-case hexadecimal, with every CRLF read as an LF.</summary>
    public static string Hash(byte[] bytes)
    {
        List<byte> normalized = new(bytes.Length);
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r' && index + 1 < bytes.Length && bytes[index + 1] == (byte)'\n')
                continue;

            normalized.Add(bytes[index]);
        }

        return Convert.ToHexStringLower(SHA256.HashData([.. normalized]));
    }

    /// <summary>The text of an expected file with every CRLF read as an LF.</summary>
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>Reads the expansions an expected file lists.</summary>
    public static IReadOnlyList<FormatLockExpansion> ReadExpansions(string expectedText)
    {
        using JsonDocument document = JsonDocument.Parse(expectedText);
        List<FormatLockExpansion> expansions = [];
        foreach (JsonElement entry in document.RootElement.GetProperty("expansions").EnumerateArray())
        {
            Dictionary<string, string?> values = new(StringComparer.Ordinal);
            foreach (JsonProperty value in entry.GetProperty("values").EnumerateObject())
                values[value.Name] = value.Value.ValueKind == JsonValueKind.Null ? null : value.Value.GetString();

            expansions.Add(new FormatLockExpansion(values, StringOrNull(entry, "host"), StringOrNull(entry, "port")));
        }

        return expansions;
    }

    private static string? StringOrNull(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The whole expected text of an accepted sample: the canonical recipe, then the expansions.</summary>
    public static string Compose(Recipe recipe, IReadOnlyList<FormatLockExpansion> expansions)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = NewWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("recipe");
            WriteRecipe(writer, recipe);
            writer.WritePropertyName("expansions");
            writer.WriteStartArray();
            foreach (FormatLockExpansion expansion in expansions)
                WriteExpansion(writer, recipe, expansion);

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Finish(stream);
    }

    /// <summary>The whole expected text of a refused sample: the code and the path of every error, in order.</summary>
    public static string ComposeErrors(IEnumerable<RecipeError> errors)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = NewWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("errors");
            writer.WriteStartArray();
            foreach (RecipeError error in errors)
            {
                writer.WriteStartObject();
                writer.WriteString("code", error.Code.ToString());
                writer.WriteString("path", error.Path);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Finish(stream);
    }

    private static Utf8JsonWriter NewWriter(Stream stream)
        => new(stream, new JsonWriterOptions
        {
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = Indentation.Length,
            NewLine = NewLine.ToString(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    // Every character outside ASCII is written as an escape, so that an expected file is plain ASCII.
    private static string Finish(MemoryStream stream)
    {
        string text = Encoding.UTF8.GetString(stream.ToArray());
        StringBuilder ascii = new(text.Length + 1);
        foreach (char c in text)
        {
            if (c >= FirstNonAscii)
                ascii.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            else
                ascii.Append(c);
        }

        return ascii.Append(NewLine).ToString();
    }

    private static void WriteRecipe(Utf8JsonWriter writer, Recipe recipe)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", recipe.SchemaVersion);
        writer.WriteString("id", recipe.Id);
        writer.WriteString("version", recipe.Version);
        writer.WriteString("author", recipe.Author);
        writer.WriteString("name", recipe.Name);
        writer.WriteString("description", recipe.Description);

        writer.WritePropertyName("translations");
        writer.WriteStartObject();
        foreach ((string tag, RecipeTranslation translation) in recipe.Translations.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(tag);
            writer.WriteStartObject();
            writer.WriteString("name", translation.Name);
            writer.WriteString("description", translation.Description);
            WriteMap(writer, "fieldLabels", translation.FieldLabels);
            WriteMap(writer, "fieldHints", translation.FieldHints);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        writer.WriteString("program", recipe.Program);
        writer.WritePropertyName("arguments");
        writer.WriteStartArray();
        foreach (RecipeArgument argument in recipe.Arguments)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("group", argument.IsGroup);
            WriteStrings(writer, "tokens", argument.Tokens);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("inputs");
        writer.WriteStartArray();
        foreach (RecipeInput input in recipe.Inputs)
            WriteInput(writer, input);

        writer.WriteEndArray();

        writer.WriteString("output", recipe.Output.ToString());
        writer.WriteString("outputEncoding", recipe.OutputEncoding.ToString());
        writer.WriteString("stderr", recipe.Stderr.ToString());
        writer.WriteString("csvSeparator", recipe.CsvSeparator.ToString());
        writer.WritePropertyName("successExitCodes");
        writer.WriteStartArray();
        foreach (int code in recipe.SuccessExitCodes)
            writer.WriteNumberValue(code);

        writer.WriteEndArray();
        writer.WriteNumber("timeoutSeconds", recipe.TimeoutSeconds);
        writer.WriteString("icon", recipe.Icon);
        writer.WriteEndObject();
    }

    private static void WriteInput(Utf8JsonWriter writer, RecipeInput input)
    {
        writer.WriteStartObject();
        writer.WriteString("name", input.Name);
        writer.WriteString("type", input.Type.ToString());
        writer.WriteString("label", input.Label);
        writer.WriteBoolean("required", input.Required);
        writer.WriteString("default", input.Default);
        writer.WriteString("pattern", input.Pattern);
        if (input.MaxLength is int maxLength)
            writer.WriteNumber("maxLength", maxLength);
        else
            writer.WriteNull("maxLength");

        WriteStrings(writer, "choices", input.Choices);
        WriteStrings(writer, "whenTrue", input.WhenTrue);
        WriteStrings(writer, "whenFalse", input.WhenFalse);
        writer.WriteString("min", input.Min);
        writer.WriteString("max", input.Max);
        writer.WriteString("hint", input.Hint);
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (string value in values)
            writer.WriteStringValue(value);

        writer.WriteEndArray();
    }

    private static void WriteMap(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> map)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach ((string key, string value) in map.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.WriteString(key, value);

        writer.WriteEndObject();
    }

    private static void WriteExpansion(Utf8JsonWriter writer, Recipe recipe, FormatLockExpansion expansion)
    {
        RecipeExpansion result = RecipeExpander.Expand(recipe, expansion.Values, expansion.Host, expansion.Port, FakeCharacterConverter.Plain);

        writer.WriteStartObject();
        writer.WritePropertyName("values");
        writer.WriteStartObject();
        foreach ((string key, string? value) in expansion.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.WriteString(key, value);

        writer.WriteEndObject();
        writer.WriteString("host", expansion.Host);
        writer.WriteString("port", expansion.Port);
        if (result.Succeeded)
        {
            WriteStrings(writer, "arguments", result.Arguments);
        }
        else
        {
            writer.WritePropertyName("refusals");
            writer.WriteStartArray();
            foreach (RecipeRefusal refusal in result.Refusals)
            {
                writer.WriteStartObject();
                writer.WriteString("reason", refusal.Reason.ToString());
                writer.WriteString("subject", refusal.Subject);
                writer.WriteString("path", refusal.Path);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
