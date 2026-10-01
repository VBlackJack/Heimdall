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

using System.Globalization;

namespace Heimdall.Core.Localization;

/// <summary>
/// Words a count with the key pair its number takes in the current language.
/// </summary>
/// <remarks>
/// <see cref="LocalizationManager"/> is one. Code that localizes through a bare key delegate, the
/// audit engine and the report builders of the tools, takes one of these beside its delegate: a
/// <see cref="Func{T, TResult}"/> from key to text cannot say which language it speaks, and the
/// plural rule depends on it (French takes the singular for 0, English and Spanish do not).
/// </remarks>
public interface ICountLocalizer
{
    /// <summary>
    /// Formats a count with the wording its number takes in the current language.
    /// </summary>
    /// <param name="count">The number the wording agrees with.</param>
    /// <param name="oneKey">The key of the singular wording; it names its number with a placeholder.</param>
    /// <param name="otherKey">The key of the plural wording.</param>
    /// <param name="args">Format arguments, the count included where the wording places it.</param>
    string FormatCount(long count, string oneKey, string otherKey, params object[] args);
}

/// <summary>
/// A count localizer over a key delegate and a way to read the current language.
/// </summary>
/// <remarks>
/// For the callers that hold a key delegate and no <see cref="LocalizationManager"/>: tests, and
/// the passthrough defaults of the engines, which speak the delegate's language under the English
/// rule. The language is read at each call, so a language switch made after construction is
/// followed.
/// </remarks>
public sealed class DelegateCountLocalizer : ICountLocalizer
{
    private readonly Func<string, string> _localize;
    private readonly Func<string> _locale;

    /// <summary>Creates a count localizer over <paramref name="localize"/>.</summary>
    /// <param name="localize">Maps a key to its text in the current language.</param>
    /// <param name="locale">Reads the current language, such as "en" or "fr".</param>
    public DelegateCountLocalizer(Func<string, string> localize, Func<string> locale)
    {
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(locale);

        _localize = localize;
        _locale = locale;
    }

    /// <summary>A count localizer over <paramref name="localize"/> under the English plural rule.</summary>
    public static DelegateCountLocalizer English(Func<string, string> localize) =>
        new(localize, static () => PluralRules.EnglishLocale);

    /// <inheritdoc />
    public string FormatCount(long count, string oneKey, string otherKey, params object[] args)
    {
        string template = _localize(PluralRules.IsOne(_locale(), count) ? oneKey : otherKey);
        if (args.Length == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // A malformed template is shown as it stands, the way LocalizationManager.Format does.
            return template;
        }
    }
}
