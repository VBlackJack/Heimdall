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

using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// Decides which locale keys no source text refers to, whether by their full name or by the
/// literal head of a key that is composed at runtime.
/// </summary>
/// <remarks>
/// A key reaches the catalogue lookup in two ways. Most are written out in full, and a plain
/// substring search finds them. Some are built from a literal prefix and a runtime part, and
/// no substring of the source is the key. The families below are the shapes of that second
/// way that this codebase has used or could use; each one has a test in
/// <c>DeadLocaleKeyDetectorTests</c> that fails when the shape is no longer recognised.
/// <para>
/// A key built from a runtime head and a literal tail (<c>$"{state}Label"</c>) has no literal
/// head to learn, so the guard cannot recognise it. Such a key has to be spelled in full
/// somewhere, or composed from a literal prefix.
/// </para>
/// </remarks>
internal static class DeadLocaleKeyDetector
{
    /// <summary>
    /// The shortest literal head that counts as the prefix of a family of keys. A shorter one
    /// is a word, not a family: <c>"Error" + x</c> in unrelated code would otherwise pardon
    /// every key that starts with it and quietly retire the guard for that whole area.
    /// </summary>
    internal const int MinComposedPrefixLength = 6;

    /// <summary>
    /// What a failing guard tells the author of a key that reads as dead. It names the length
    /// floor, so that nobody deletes a live key composed from a short head just because the guard
    /// cannot see it.
    /// </summary>
    internal static readonly string NewDeadKeyAdvice =
        "Either wire them up, or delete them from both locales. If a key really is resolved at "
        + "runtime, give it a literal head of at least "
        + MinComposedPrefixLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " characters (for example \"ToolDesc\" + id) and the guard exempts its family. "
        + "A composed head shorter than that is NOT recognised, so a key built from one reads "
        + "as dead although it is live: do not delete it, lengthen its head, which is the "
        + "smallest change.";

    /// <summary>The marker that makes a catalogue entry a comment line of the JSON file.</summary>
    internal const string CommentKeyPrefix = "_comment";

    private const string PrefixPattern = "[A-Z][A-Za-z0-9_.]*";

    // Regular string literals throughout, not verbatim ones: a verbatim literal that opens with a
    // quote character reads as a raw string to the test suite's source blanker.
    private const string InterpolationOpener = "(?:\\$@?|@\\$)\"";

    // $"Prefix{x}" and the verbatim forms $@"Prefix{x}" and @$"Prefix{x}".
    private static readonly Regex s_interpolatedPrefix = new(
        InterpolationOpener + "(" + PrefixPattern + ")\\{",
        RegexOptions.Compiled);

    // "Prefix" + x
    private static readonly Regex s_literalPlusExpression = new(
        "\"(" + PrefixPattern + ")\"\\s*\\+",
        RegexOptions.Compiled);

    // string.Format("Prefix{0}", x): the template is the key.
    private static readonly Regex s_formatTemplate = new(
        "(?:string|String)\\.Format\\(\\s*\"(" + PrefixPattern + ")\\{",
        RegexOptions.Compiled);

    // string.Concat("Prefix", x)
    private static readonly Regex s_concatLiteral = new(
        "(?:string|String)\\.Concat\\(\\s*\"(" + PrefixPattern + ")\"\\s*,",
        RegexOptions.Compiled);

    // nameof(Prefix) + "Tail" and $"{nameof(Prefix)}Tail"
    private static readonly Regex s_nameofPlus = new(
        "nameof\\(\\s*(" + PrefixPattern + ")\\s*\\)\\s*\\+",
        RegexOptions.Compiled);

    private static readonly Regex s_nameofInterpolated = new(
        InterpolationOpener + "\\{nameof\\(\\s*(" + PrefixPattern + ")\\s*\\)\\}",
        RegexOptions.Compiled);

    private static readonly Regex[] s_composedShapes =
    [
        s_interpolatedPrefix,
        s_literalPlusExpression,
        s_formatTemplate,
        s_concatLiteral,
        s_nameofPlus,
        s_nameofInterpolated,
    ];

    /// <summary>
    /// The literal heads of the key families <paramref name="source"/> composes at runtime.
    /// A head pardons only the keys that it opens (see <see cref="FindDeadKeys"/>), so one that
    /// opens none of the catalogue's keys changes nothing.
    /// </summary>
    internal static HashSet<string> FindComposedPrefixes(string source)
    {
        return s_composedShapes
            .SelectMany(shape => shape.Matches(source).Select(match => match.Groups[1].Value))
            .Where(prefix => prefix.Length >= MinComposedPrefixLength)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The keys of <paramref name="keys"/> that <paramref name="source"/> neither spells out
    /// nor composes, in ordinal order. Comment entries of the catalogue are not keys anyone looks up.
    /// </summary>
    internal static List<string> FindDeadKeys(IReadOnlyList<string> keys, string source)
    {
        HashSet<string> prefixes = FindComposedPrefixes(source);

        return keys
            .Where(key => !IsCommentKey(key))
            .Where(key => !source.Contains(key, StringComparison.Ordinal))
            .Where(key => !prefixes.Any(prefix => IsFamilyMember(key, prefix)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
    }

    internal static bool IsCommentKey(string key) =>
        key.StartsWith(CommentKeyPrefix, StringComparison.Ordinal);

    private static bool IsFamilyMember(string key, string prefix) =>
        key.Length > prefix.Length && key.StartsWith(prefix, StringComparison.Ordinal);
}
