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

namespace Heimdall.Core.Localization;

/// <summary>
/// Decides whether a whole number takes the singular wording in a language.
/// </summary>
/// <remarks>
/// The integer part of the CLDR "one" category, which is all a count needs. French takes the
/// singular for 0 and 1; English and Spanish for 1 only, and so does any language not listed
/// here until it is.
/// </remarks>
public static class PluralRules
{
    /// <summary>The language of the code fallbacks and of a delegate that cannot name its own.</summary>
    public const string EnglishLocale = "en";

    private const string FrenchLanguage = "fr";

    /// <summary>The separator between a language and its region in a locale identifier.</summary>
    private const char RegionSeparator = '-';

    /// <summary>
    /// Picks the English wording of a count, for the code literals shown when no localizer is
    /// available.
    /// </summary>
    /// <param name="count">The number the wording agrees with.</param>
    /// <param name="one">The singular wording, already formatted.</param>
    /// <param name="other">The plural wording, already formatted.</param>
    public static string SelectEnglish(long count, string one, string other) =>
        IsOne(EnglishLocale, count) ? one : other;

    /// <summary>
    /// Whether <paramref name="count"/> takes the singular wording in <paramref name="locale"/>.
    /// </summary>
    /// <param name="locale">A locale identifier such as "en", "fr" or "fr-CA".</param>
    /// <param name="count">The number the wording agrees with.</param>
    public static bool IsOne(string locale, long count)
    {
        ArgumentNullException.ThrowIfNull(locale);

        return IsLanguage(locale, FrenchLanguage)
            ? count is >= -1 and <= 1
            : count is 1 or -1;
    }

    private static bool IsLanguage(string locale, string language) =>
        locale.StartsWith(language, StringComparison.OrdinalIgnoreCase)
        && (locale.Length == language.Length || locale[language.Length] == RegionSeparator);
}
