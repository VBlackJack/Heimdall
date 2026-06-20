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

namespace TwinShell.Core.Helpers;

/// <summary>
/// Shared, pure predicates describing where a parameter placeholder sits inside a
/// command pattern. Centralized here so persistence-time seeding and import-time
/// validation apply the exact same quoting rule.
/// </summary>
public static class CommandPatternQuoting
{
    /// <summary>
    /// Returns <c>true</c> when the first occurrence of the
    /// <paramref name="parameterName"/> placeholder ("{name}") inside
    /// <paramref name="commandPattern"/> falls within an open single-quoted span,
    /// i.e. an odd number of single quotes precede it. This is the precondition
    /// for <c>QuotingMode.InlineInQuotes</c>, which only escapes the value and
    /// relies on surrounding quotes already present in the pattern.
    /// </summary>
    /// <param name="commandPattern">The command pattern containing the placeholder.</param>
    /// <param name="parameterName">The parameter name without braces.</param>
    /// <returns>
    /// <c>true</c> when the placeholder is inside a single-quoted span;
    /// <c>false</c> when it is outside one or absent from the pattern.
    /// </returns>
    public static bool IsPlaceholderInsideSingleQuotedSpan(string commandPattern, string parameterName)
    {
        if (string.IsNullOrEmpty(commandPattern) || string.IsNullOrEmpty(parameterName))
        {
            return false;
        }

        string placeholder = "{" + parameterName + "}";
        int placeholderIndex = commandPattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return false;
        }

        int quoteCount = 0;
        for (int index = 0; index < placeholderIndex; index++)
        {
            if (commandPattern[index] == '\'')
            {
                quoteCount++;
            }
        }

        return quoteCount % 2 == 1;
    }
}
