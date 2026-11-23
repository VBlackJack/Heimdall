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

namespace Heimdall.Core.Recipes;

/// <summary>
/// Compiles and runs the regular expression of a <c>string</c> input. The expression always has to
/// match the whole value, and every match runs under <see cref="RecipeLimits.PatternMatchTimeout"/>.
/// </summary>
internal static class RecipePatterns
{
    private const string WholeValuePrefix = @"\A(?:";

    private const string WholeValueSuffix = @")\z";

    private const RegexOptions Options = RegexOptions.CultureInvariant;

    /// <summary>True when the pattern compiles.</summary>
    public static bool IsValid(string pattern)
    {
        try
        {
            // The bare pattern must compile on its own: that proves its parentheses balance, so the
            // wrapping below cannot be closed early by a pattern such as "a)|(b".
            _ = new Regex(pattern, Options, RecipeLimits.PatternMatchTimeout);
            _ = new Regex(Wrap(pattern), Options, RecipeLimits.PatternMatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>True when the whole value matches. Throws <see cref="RegexMatchTimeoutException"/> on timeout.</summary>
    public static bool IsFullMatch(string pattern, string value)
        => Regex.IsMatch(value, Wrap(pattern), Options, RecipeLimits.PatternMatchTimeout);

    private static string Wrap(string pattern) => WholeValuePrefix + pattern + WholeValueSuffix;
}
