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
using System.Globalization;

namespace Heimdall.Core.Recipes;

/// <summary>
/// Turns the JSON tree into a <see cref="Recipe"/>, refusing unknown properties and wrong types.
/// It also records where every entry sits, so the validator can attach positions to its errors.
/// </summary>
internal sealed class RecipeMapper
{
    private readonly List<RecipeError> _errors;

    private readonly Dictionary<string, RecipePosition> _positions = new(StringComparer.Ordinal);

    public RecipeMapper(List<RecipeError> errors)
    {
        _errors = errors;
    }

    /// <summary>The position of an entry, or of the nearest enclosing entry that has one.</summary>
    public RecipePosition? PositionOf(string path)
    {
        string current = path;
        while (true)
        {
            if (_positions.TryGetValue(current, out RecipePosition position))
                return position;

            if (current.Length == 0)
                return null;

            current = RecipePath.Parent(current);
        }
    }

    /// <summary>Maps the root. Returns null when a structural error makes a recipe impossible.</summary>
    public Recipe? Map(JsonNode rootNode)
    {
        Record(string.Empty, rootNode);
        if (rootNode.Kind != JsonKind.Object)
        {
            Wrong(string.Empty, rootNode);
            return null;
        }

        View root = new(rootNode);

        // A file written for a newer format may hold properties this build cannot know: say that
        // first, and only that, instead of listing them as unknown.
        JsonNode? versionNode = root.Peek(RecipePath.SchemaVersion);
        if (versionNode is not null && TryReadInt(versionNode, out int declared) && declared > RecipeFormat.SupportedSchemaVersion)
        {
            Record(RecipePath.SchemaVersion, versionNode);
            _errors.Add(new RecipeError(
                RecipeErrorCode.UnsupportedSchemaVersion,
                versionNode.Position,
                RecipePath.SchemaVersion,
                Actual: declared,
                Limit: RecipeFormat.SupportedSchemaVersion));
            return null;
        }

        int schemaVersion = RequiredInt(root, RecipePath.SchemaVersion, string.Empty);
        string id = RequiredString(root, RecipePath.Id, string.Empty);
        string version = RequiredString(root, RecipePath.Version, string.Empty);
        string author = RequiredString(root, RecipePath.Author, string.Empty);
        string name = RequiredString(root, RecipePath.Name, string.Empty);
        string description = RequiredString(root, RecipePath.Description, string.Empty);
        IReadOnlyDictionary<string, RecipeTranslation> translations = ReadTranslations(root.Take(RecipePath.Translations));
        string program = RequiredString(root, RecipePath.Program, string.Empty);
        IReadOnlyList<RecipeArgument> arguments = ReadArguments(root.Take(RecipePath.Arguments));
        IReadOnlyList<RecipeInput> inputs = ReadInputs(root.Take(RecipePath.Inputs));
        RecipeOutputFormat output = OptionalEnum(root, RecipePath.Output, RecipeFormat.OutputFormatNames, RecipeOutputFormat.Text);
        RecipeOutputEncoding encoding = OptionalEnum(root, RecipePath.OutputEncoding, RecipeFormat.OutputEncodingNames, RecipeOutputEncoding.Utf8);
        RecipeStderrMode stderr = OptionalEnum(root, RecipePath.Stderr, RecipeFormat.StderrModeNames, RecipeStderrMode.Separate);
        RecipeCsvSeparator csvSeparator = ReadCsvSeparator(root, output);
        IReadOnlyList<int> exitCodes = ReadExitCodes(root.Take(RecipePath.SuccessExitCodes));
        int timeout = OptionalInt(root, RecipePath.TimeoutSeconds, RecipeLimits.DefaultTimeoutSeconds);
        string? icon = OptionalString(root, RecipePath.Icon);

        RejectUnknown(root, string.Empty);

        if (_errors.Count > 0)
            return null;

        return new Recipe(
            schemaVersion,
            id,
            version,
            author,
            name,
            description,
            translations,
            program,
            arguments,
            inputs,
            output,
            encoding,
            stderr,
            exitCodes,
            timeout,
            icon)
        {
            CsvSeparator = csvSeparator,
        };
    }

    /// <summary>
    /// Reads <c>csvSeparator</c>. It is refused, even as the default <c>comma</c>, when the output is not
    /// <c>csv</c>: only the mapper can see that the property was written.
    /// </summary>
    private RecipeCsvSeparator ReadCsvSeparator(View root, RecipeOutputFormat output)
    {
        JsonNode? node = root.Peek(RecipePath.CsvSeparator);
        RecipeCsvSeparator separator = OptionalEnum(root, RecipePath.CsvSeparator, RecipeFormat.CsvSeparatorNames, RecipeCsvSeparator.Comma);
        if (node is not null && output != RecipeOutputFormat.Csv)
        {
            _errors.Add(new RecipeError(
                RecipeErrorCode.CsvSeparatorWithoutCsvOutput,
                node.Position,
                RecipePath.CsvSeparator));
        }

        return separator;
    }

    private IReadOnlyDictionary<string, RecipeTranslation> ReadTranslations(JsonNode? node)
    {
        Dictionary<string, RecipeTranslation> translations = new(StringComparer.Ordinal);
        if (node is null)
            return translations.ToFrozenDictionary(StringComparer.Ordinal);

        Record(RecipePath.Translations, node);
        if (node.Kind != JsonKind.Object)
        {
            Wrong(RecipePath.Translations, node);
            return translations.ToFrozenDictionary(StringComparer.Ordinal);
        }

        foreach (JsonMember member in node.Members)
        {
            string path = RecipePath.Property(RecipePath.Translations, member.Name);
            Record(path, member.Value);
            if (member.Value.Kind != JsonKind.Object)
            {
                Wrong(path, member.Value);
                continue;
            }

            View view = new(member.Value);
            string? translatedName = OptionalString(view, RecipePath.Name, path);
            string? translatedDescription = OptionalString(view, RecipePath.Description, path);
            Dictionary<string, string> fieldLabels = ReadTextMap(view.Take(RecipePath.FieldLabels), RecipePath.Property(path, RecipePath.FieldLabels));
            Dictionary<string, string> fieldHints = ReadTextMap(view.Take(RecipePath.FieldHints), RecipePath.Property(path, RecipePath.FieldHints));
            RejectUnknown(view, path);
            translations[member.Name] = new RecipeTranslation(
                translatedName,
                translatedDescription,
                fieldLabels.ToFrozenDictionary(StringComparer.Ordinal))
            {
                FieldHints = fieldHints.ToFrozenDictionary(StringComparer.Ordinal),
            };
        }

        return translations.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private Dictionary<string, string> ReadTextMap(JsonNode? node, string path)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        if (node is null)
            return fields;

        Record(path, node);
        if (node.Kind != JsonKind.Object)
        {
            Wrong(path, node);
            return fields;
        }

        foreach (JsonMember member in node.Members)
        {
            string text = ReadString(member.Value, RecipePath.Property(path, member.Name)) ?? string.Empty;
            fields[member.Name] = text;
        }

        return fields;
    }

    private IReadOnlyList<RecipeArgument> ReadArguments(JsonNode? node)
    {
        List<RecipeArgument> arguments = [];
        if (node is null)
            return arguments;

        Record(RecipePath.Arguments, node);
        if (node.Kind != JsonKind.Array)
        {
            Wrong(RecipePath.Arguments, node);
            return arguments;
        }

        for (int index = 0; index < node.Items.Count; index++)
        {
            JsonNode item = node.Items[index];
            string path = RecipePath.Index(RecipePath.Arguments, index);
            Record(path, item);

            if (item.Kind == JsonKind.String)
            {
                arguments.Add(RecipeArgument.Bare(item.Text));
            }
            else if (item.Kind == JsonKind.Array)
            {
                List<string> tokens = ReadStrings(item, path);
                arguments.Add(RecipeArgument.Group(tokens));
            }
            else
            {
                Wrong(path, item);
            }
        }

        return arguments;
    }

    private List<string> ReadStrings(JsonNode array, string path)
    {
        List<string> values = [];
        for (int index = 0; index < array.Items.Count; index++)
        {
            string? text = ReadString(array.Items[index], RecipePath.Index(path, index));
            if (text is not null)
                values.Add(text);
        }

        return values;
    }

    private IReadOnlyList<RecipeInput> ReadInputs(JsonNode? node)
    {
        List<RecipeInput> inputs = [];
        if (node is null)
            return inputs;

        Record(RecipePath.Inputs, node);
        if (node.Kind != JsonKind.Array)
        {
            Wrong(RecipePath.Inputs, node);
            return inputs;
        }

        for (int index = 0; index < node.Items.Count; index++)
        {
            JsonNode item = node.Items[index];
            string path = RecipePath.Index(RecipePath.Inputs, index);
            Record(path, item);
            if (item.Kind != JsonKind.Object)
            {
                Wrong(path, item);
                continue;
            }

            inputs.Add(ReadInput(new View(item), path));
        }

        return inputs;
    }

    private RecipeInput ReadInput(View view, string path)
    {
        string name = RequiredString(view, RecipePath.Name, path);
        RecipeInputType type = RequiredEnum(view, RecipePath.Type, path, RecipeFormat.InputTypeNames, RecipeInputType.String);
        string label = RequiredString(view, RecipePath.Label, path);
        bool required = ReadRequired(view, path, type, name);
        string? defaultValue = ReadDefault(view.Take(RecipePath.Default), RecipePath.Property(path, RecipePath.Default), type);
        string? pattern = OptionalString(view, RecipePath.Pattern, path);
        int? maxLength = OptionalNullableInt(view, RecipePath.MaxLength, path);
        IReadOnlyList<string>? choices = OptionalStringList(view, RecipePath.Choices, path);
        IReadOnlyList<string>? whenTrue = OptionalStringList(view, RecipePath.WhenTrue, path);
        IReadOnlyList<string>? whenFalse = OptionalStringList(view, RecipePath.WhenFalse, path);
        string? min = OptionalBound(view, RecipePath.Min, path);
        string? max = OptionalBound(view, RecipePath.Max, path);
        string? hint = OptionalString(view, RecipePath.Hint, path);
        RejectUnknown(view, path);

        return new RecipeInput(name, type, label, required, defaultValue, pattern, maxLength, choices, whenTrue)
        {
            WhenFalse = whenFalse,
            Min = min,
            Max = max,
            Hint = hint,
        };
    }

    /// <summary>
    /// Reads <c>required</c>. A <c>bool</c> input never takes it, whatever its value: only the mapper can see
    /// that the property was written, since a missing one and <c>false</c> look the same afterwards.
    /// </summary>
    private bool ReadRequired(View view, string path, RecipeInputType type, string name)
    {
        JsonNode? node = view.Peek(RecipePath.Required);
        bool required = OptionalBool(view, RecipePath.Required, path);
        if (node is null)
            return required;

        string requiredPath = RecipePath.Property(path, RecipePath.Required);
        if (type == RecipeInputType.Bool)
        {
            _errors.Add(new RecipeError(RecipeErrorCode.PropertyNotAllowedForType, node.Position, requiredPath, Fragment: RecipePath.Required));
            return false;
        }

        // A field with a default is never left empty, so "required" says nothing, whether it is true or false:
        // only the reader can see that the property was written next to a default.
        if (view.Peek(RecipePath.Default) is not null)
        {
            _errors.Add(new RecipeError(RecipeErrorCode.RequiredWithDefault, node.Position, requiredPath, Name: RecipeText.MakeSafe(name)));
            return false;
        }

        return required;
    }

    /// <summary>Reads a <c>min</c> or <c>max</c>: a JSON number or a string, kept exactly as written.</summary>
    private string? OptionalBound(View view, string name, string parent)
    {
        JsonNode? node = view.Take(name);
        if (node is null)
            return null;

        string path = RecipePath.Property(parent, name);
        Record(path, node);
        if (node.Kind is JsonKind.String or JsonKind.Number)
            return node.Text;

        Wrong(path, node);
        return null;
    }

    private string? ReadDefault(JsonNode? node, string path, RecipeInputType type)
    {
        if (node is null)
            return null;

        Record(path, node);
        switch (node.Kind)
        {
            case JsonKind.String when type != RecipeInputType.Bool:
                return node.Text;
            case JsonKind.Number when type is RecipeInputType.Int or RecipeInputType.Number:
                return node.Text;
            case JsonKind.True when type == RecipeInputType.Bool:
                return RecipeFormat.TrueText;
            case JsonKind.False when type == RecipeInputType.Bool:
                return RecipeFormat.FalseText;
            default:
                Wrong(path, node);
                return null;
        }
    }

    private IReadOnlyList<int> ReadExitCodes(JsonNode? node)
    {
        if (node is null)
            return [0];

        Record(RecipePath.SuccessExitCodes, node);
        if (node.Kind != JsonKind.Array)
        {
            Wrong(RecipePath.SuccessExitCodes, node);
            return [0];
        }

        List<int> codes = [];
        for (int index = 0; index < node.Items.Count; index++)
        {
            JsonNode item = node.Items[index];
            string path = RecipePath.Index(RecipePath.SuccessExitCodes, index);
            Record(path, item);
            if (TryReadInt(item, out int code))
            {
                codes.Add(code);
            }
            else
            {
                Wrong(path, item);
            }
        }

        return codes;
    }

    private string RequiredString(View view, string name, string parent)
    {
        string path = RecipePath.Property(parent, name);
        JsonNode? node = view.Take(name);
        if (node is null)
        {
            Missing(view, path, name);
            return string.Empty;
        }

        return ReadString(node, path) ?? string.Empty;
    }

    private int RequiredInt(View view, string name, string parent)
    {
        string path = RecipePath.Property(parent, name);
        JsonNode? node = view.Take(name);
        if (node is null)
        {
            Missing(view, path, name);
            return 0;
        }

        Record(path, node);
        if (TryReadInt(node, out int value))
            return value;

        Wrong(path, node);
        return 0;
    }

    private T RequiredEnum<T>(View view, string name, string parent, FrozenDictionary<string, T> names, T fallback)
        where T : struct, Enum
    {
        string path = RecipePath.Property(parent, name);
        JsonNode? node = view.Take(name);
        if (node is null)
        {
            Missing(view, path, name);
            return fallback;
        }

        return ReadEnum(node, path, names, fallback);
    }

    private T OptionalEnum<T>(View view, string name, FrozenDictionary<string, T> names, T fallback)
        where T : struct, Enum
    {
        JsonNode? node = view.Take(name);
        return node is null ? fallback : ReadEnum(node, RecipePath.Property(string.Empty, name), names, fallback);
    }

    private T ReadEnum<T>(JsonNode node, string path, FrozenDictionary<string, T> names, T fallback)
        where T : struct, Enum
    {
        string? text = ReadString(node, path);
        if (text is null)
            return fallback;

        if (names.TryGetValue(text, out T value))
            return value;

        _errors.Add(new RecipeError(RecipeErrorCode.InvalidEnumValue, node.Position, path, Fragment: RecipeText.MakeSafe(text)));
        return fallback;
    }

    private string? OptionalString(View view, string name, string parent = "")
    {
        JsonNode? node = view.Take(name);
        return node is null ? null : ReadString(node, RecipePath.Property(parent, name));
    }

    private bool OptionalBool(View view, string name, string parent)
    {
        string path = RecipePath.Property(parent, name);
        JsonNode? node = view.Take(name);
        if (node is null)
            return false;

        Record(path, node);
        if (node.Kind is JsonKind.True or JsonKind.False)
            return node.Kind == JsonKind.True;

        Wrong(path, node);
        return false;
    }

    private int OptionalInt(View view, string name, int fallback)
    {
        JsonNode? node = view.Take(name);
        if (node is null)
            return fallback;

        string path = RecipePath.Property(string.Empty, name);
        Record(path, node);
        if (TryReadInt(node, out int value))
            return value;

        Wrong(path, node);
        return fallback;
    }

    private int? OptionalNullableInt(View view, string name, string parent)
    {
        JsonNode? node = view.Take(name);
        if (node is null)
            return null;

        string path = RecipePath.Property(parent, name);
        Record(path, node);
        if (TryReadInt(node, out int value))
            return value;

        Wrong(path, node);
        return null;
    }

    private IReadOnlyList<string>? OptionalStringList(View view, string name, string parent)
    {
        JsonNode? node = view.Take(name);
        if (node is null)
            return null;

        string path = RecipePath.Property(parent, name);
        Record(path, node);
        if (node.Kind != JsonKind.Array)
        {
            Wrong(path, node);
            return null;
        }

        return ReadStrings(node, path);
    }

    private string? ReadString(JsonNode node, string path)
    {
        Record(path, node);
        if (node.Kind == JsonKind.String)
            return node.Text;

        Wrong(path, node);
        return null;
    }

    private static bool TryReadInt(JsonNode node, out int value)
    {
        value = 0;
        return node.Kind == JsonKind.Number
            && int.TryParse(node.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private void RejectUnknown(View view, string path)
    {
        foreach (JsonMember member in view.Untaken)
        {
            _errors.Add(new RecipeError(
                RecipeErrorCode.UnknownProperty,
                member.NamePosition,
                path,
                Name: RecipeText.MakeSafe(member.Name)));
        }
    }

    private void Missing(View view, string path, string name)
        => _errors.Add(new RecipeError(RecipeErrorCode.MissingProperty, view.Node.Position, path, Name: name));

    private void Wrong(string path, JsonNode node)
        => _errors.Add(new RecipeError(RecipeErrorCode.WrongType, node.Position, path));

    private void Record(string path, JsonNode node) => _positions[path] = node.Position;

    /// <summary>The members of one object, with a note of which were read.</summary>
    private sealed class View
    {
        private readonly Dictionary<string, JsonMember> _members = new(StringComparer.Ordinal);

        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public View(JsonNode node)
        {
            Node = node;
            foreach (JsonMember member in node.Members)
                _members[member.Name] = member;
        }

        public JsonNode Node { get; }

        public IEnumerable<JsonMember> Untaken => Node.Members.Where(member => !_taken.Contains(member.Name));

        public JsonNode? Peek(string name) => _members.TryGetValue(name, out JsonMember? member) ? member.Value : null;

        public JsonNode? Take(string name)
        {
            if (!_members.TryGetValue(name, out JsonMember? member))
                return null;

            _taken.Add(name);
            return member.Value;
        }
    }
}
