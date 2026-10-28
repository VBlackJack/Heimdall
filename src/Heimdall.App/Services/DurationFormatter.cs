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

namespace Heimdall.App.Services;

/// <summary>
/// Writes an elapsed time with the units of the interface language. The unit text lives in the
/// locale catalogues, not here: the number is formatted invariantly and slotted into a pattern
/// the translator owns, so no language inherits a unit that another one chose.
/// </summary>
public static class DurationFormatter
{
    /// <summary>Catalogue key of the pattern for a duration of one hour or more.</summary>
    public const string HoursMinutesSecondsKey = "DurationHoursMinutesSeconds";

    /// <summary>Catalogue key of the pattern for a duration of one minute or more.</summary>
    public const string MinutesSecondsKey = "DurationMinutesSeconds";

    /// <summary>Catalogue key of the pattern for a duration under one minute.</summary>
    public const string SecondsKey = "DurationSeconds";

    private const string TenthsFormat = "F1";

    /// <summary>
    /// Formats <paramref name="duration"/> as hours, minutes and seconds, dropping the leading
    /// units that are zero. Seconds are whole seconds.
    /// </summary>
    /// <param name="duration">The elapsed time.</param>
    /// <param name="localize">Resolves a catalogue key to its pattern in the interface language.</param>
    public static string Format(TimeSpan duration, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        if (duration.TotalHours >= 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                localize(HoursMinutesSecondsKey),
                (int)duration.TotalHours,
                duration.Minutes,
                duration.Seconds);
        }

        if (duration.TotalMinutes >= 1)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                localize(MinutesSecondsKey),
                duration.Minutes,
                duration.Seconds);
        }

        return string.Format(CultureInfo.InvariantCulture, localize(SecondsKey), duration.Seconds);
    }

    /// <summary>
    /// Formats a duration under one minute with a tenth of a second, the way the audit footer
    /// shows a quick run; a longer one falls back to <see cref="Format"/>.
    /// </summary>
    /// <param name="duration">The elapsed time.</param>
    /// <param name="localize">Resolves a catalogue key to its pattern in the interface language.</param>
    /// <param name="culture">The culture that writes the decimal separator.</param>
    public static string FormatWithTenths(TimeSpan duration, Func<string, string> localize, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(culture);

        if (duration.TotalMinutes >= 1)
        {
            return Format(duration, localize);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            localize(SecondsKey),
            duration.TotalSeconds.ToString(TenthsFormat, culture));
    }
}
