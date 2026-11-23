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

namespace Heimdall.Core.Recipes;

/// <summary>
/// Reads a recipe file. The reader is strict: unknown properties, duplicate properties and wrong
/// types are refused, the size is capped before anything is parsed, a newer format is refused with
/// both version numbers, and every error carries a line and column when one is known. Bad input
/// never throws; the outcome is a <see cref="RecipeParseResult"/>.
/// </summary>
public static class RecipeParser
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Parses and validates a recipe from text.</summary>
    public static RecipeParseResult Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return RecipeParseResult.Failure([new RecipeError(RecipeErrorCode.EmptyDocument)]);

        // Every character takes at least one byte, so a longer text is certainly over the cap.
        if (text.Length > RecipeLimits.MaxFileBytes)
            return Oversize(Encoding.UTF8.GetByteCount(text));

        return Parse(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Parses and validates a recipe from UTF-8 bytes (a byte order mark is tolerated).</summary>
    public static RecipeParseResult Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > RecipeLimits.MaxFileBytes)
            return Oversize(utf8.Length);

        if (utf8.StartsWith(Utf8Bom))
            utf8 = utf8[Utf8Bom.Length..];

        if (utf8.IsEmpty)
            return RecipeParseResult.Failure([new RecipeError(RecipeErrorCode.EmptyDocument)]);

        List<RecipeError> errors = [];
        JsonNode? root = RecipeJsonTree.Build(utf8.ToArray(), errors);
        if (root is null)
            return RecipeParseResult.Failure(Cap(errors));

        RecipeMapper mapper = new(errors);
        Recipe? recipe = mapper.Map(root);

        // A structural problem (including a duplicate property noticed while reading) stops here:
        // validating a half-mapped recipe would only add noise.
        if (recipe is null || errors.Count > 0)
            return RecipeParseResult.Failure(Cap(errors));

        IReadOnlyList<RecipeError> problems = RecipeValidator.Validate(recipe, mapper.PositionOf);
        return problems.Count == 0
            ? RecipeParseResult.Success(recipe)
            : RecipeParseResult.Failure(Cap(problems));
    }

    private static RecipeParseResult Oversize(int actual)
        => RecipeParseResult.Failure(
            [new RecipeError(RecipeErrorCode.FileTooLarge, Actual: actual, Limit: RecipeLimits.MaxFileBytes)]);

    private static IReadOnlyList<RecipeError> Cap(IReadOnlyList<RecipeError> errors)
        => errors.Count <= RecipeLimits.MaxReportedErrors
            ? errors
            : [.. errors.Take(RecipeLimits.MaxReportedErrors)];
}
