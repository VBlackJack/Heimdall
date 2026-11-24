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

using System.Text;
using System.Text.Json;

namespace Heimdall.Core.Recipes;

/// <summary>The JSON value kinds the recipe format distinguishes.</summary>
internal enum JsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>A property of a JSON object, with where its name sits.</summary>
internal sealed record JsonMember(string Name, RecipePosition NamePosition, JsonNode Value);

/// <summary>A JSON value that remembers where it started.</summary>
internal sealed class JsonNode
{
    public JsonNode(JsonKind kind, RecipePosition position)
    {
        Kind = kind;
        Position = position;
    }

    public JsonKind Kind { get; }

    public RecipePosition Position { get; }

    /// <summary>The string value, or the raw text of a number.</summary>
    public string Text { get; init; } = string.Empty;

    public List<JsonMember> Members { get; } = [];

    public List<JsonNode> Items { get; } = [];
}

/// <summary>Maps byte offsets of a UTF-8 text to one-based line and column.</summary>
internal sealed class LineIndex
{
    private const byte NewLine = (byte)'\n';

    private const byte ContinuationMask = 0xC0;

    private const byte ContinuationBits = 0x80;

    private readonly byte[] _bytes;

    private readonly List<int> _lineStarts = [0];

    public LineIndex(byte[] bytes)
    {
        _bytes = bytes;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == NewLine)
                _lineStarts.Add(index + 1);
        }
    }

    /// <summary>The position of a byte offset; the column counts characters, not bytes.</summary>
    public RecipePosition At(long offset)
    {
        int clamped = (int)Math.Clamp(offset, 0, _bytes.Length);
        int line = _lineStarts.BinarySearch(clamped);
        if (line < 0)
            line = ~line - 1;

        int column = 1;
        for (int index = _lineStarts[line]; index < clamped; index++)
        {
            if ((_bytes[index] & ContinuationMask) != ContinuationBits)
                column++;
        }

        return new RecipePosition(line + 1, column);
    }

    /// <summary>The position given by a zero-based line number and a byte position in that line.</summary>
    public RecipePosition AtLine(long zeroBasedLine, long bytePositionInLine)
    {
        int line = (int)Math.Clamp(zeroBasedLine, 0, _lineStarts.Count - 1);
        return At(_lineStarts[line] + bytePositionInLine);
    }
}

/// <summary>
/// Reads UTF-8 JSON into a <see cref="JsonNode"/> tree that keeps positions, refuses duplicate
/// property names, and never throws for bad input.
/// </summary>
internal static class RecipeJsonTree
{
    /// <summary>Builds the tree. On a syntax problem the tree is null and one error is added.</summary>
    public static JsonNode? Build(byte[] utf8, List<RecipeError> errors)
    {
        LineIndex index = new(utf8);
        JsonReaderOptions options = new() { MaxDepth = RecipeLimits.MaxJsonDepth };
        Utf8JsonReader reader = new(utf8, isFinalBlock: true, new JsonReaderState(options));

        try
        {
            if (!reader.Read())
            {
                errors.Add(new RecipeError(RecipeErrorCode.EmptyDocument));
                return null;
            }

            JsonNode root = ReadValue(ref reader, index, errors);
            if (reader.Read())
            {
                errors.Add(new RecipeError(RecipeErrorCode.SyntaxError, index.At(reader.TokenStartIndex)));
                return null;
            }

            return root;
        }
        catch (JsonException ex)
        {
            RecipePosition? position = ex.LineNumber is long line && ex.BytePositionInLine is long bytePosition
                ? index.AtLine(line, bytePosition)
                : index.At(reader.BytesConsumed);
            errors.Add(new RecipeError(RecipeErrorCode.SyntaxError, position));
            return null;
        }
        catch (InvalidOperationException)
        {
            // A string with an unpaired surrogate escape cannot be decoded.
            errors.Add(new RecipeError(RecipeErrorCode.SyntaxError, index.At(reader.TokenStartIndex)));
            return null;
        }
    }

    private static JsonNode ReadValue(ref Utf8JsonReader reader, LineIndex index, List<RecipeError> errors)
    {
        RecipePosition start = index.At(reader.TokenStartIndex);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return ReadObject(ref reader, index, errors, start);
            case JsonTokenType.StartArray:
                return ReadArray(ref reader, index, errors, start);
            case JsonTokenType.String:
                return new JsonNode(JsonKind.String, start) { Text = reader.GetString() ?? string.Empty };
            case JsonTokenType.Number:
                return new JsonNode(JsonKind.Number, start) { Text = Encoding.UTF8.GetString(reader.ValueSpan) };
            case JsonTokenType.True:
                return new JsonNode(JsonKind.True, start);
            case JsonTokenType.False:
                return new JsonNode(JsonKind.False, start);
            default:
                return new JsonNode(JsonKind.Null, start);
        }
    }

    private static JsonNode ReadObject(ref Utf8JsonReader reader, LineIndex index, List<RecipeError> errors, RecipePosition start)
    {
        JsonNode node = new(JsonKind.Object, start);
        HashSet<string> seen = new(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? string.Empty;
            RecipePosition namePosition = index.At(reader.TokenStartIndex);
            reader.Read();
            JsonNode value = ReadValue(ref reader, index, errors);

            if (seen.Add(name))
            {
                node.Members.Add(new JsonMember(name, namePosition, value));
            }
            else
            {
                errors.Add(new RecipeError(
                    RecipeErrorCode.DuplicateProperty,
                    namePosition,
                    Name: RecipeText.MakeSafe(name)));
            }
        }

        return node;
    }

    private static JsonNode ReadArray(ref Utf8JsonReader reader, LineIndex index, List<RecipeError> errors, RecipePosition start)
    {
        JsonNode node = new(JsonKind.Array, start);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            node.Items.Add(ReadValue(ref reader, index, errors));

        return node;
    }
}
