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

namespace Heimdall.Core.CronJob;

/// <summary>
/// Calculates upcoming cron run times from classic five-field schedules.
/// </summary>
/// <remarks>
/// Times are computed in the clock of the <c>now</c> argument, which the app passes as the
/// workstation's local time: the schedule's own time zone (a server's) is not known here.
/// </remarks>
public static class CronScheduleCalculator
{
    private const int MaxIterations = 525960;

    private const int FirstMonth = 1;
    private const int LastMonth = 12;
    private const int FirstDayOfMonth = 1;
    private const int LastDayOfMonth = 31;
    private const int Sunday = 0;
    private const int Saturday = 6;
    private const int SundayAlias = 7;
    private const char StarToken = '*';

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static IReadOnlyList<string> CalculateNextRuns(string[] fields, int count, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Length < 5 || count <= 0)
        {
            return Array.Empty<string>();
        }

        var validMinutes = ExpandField(fields[0], 0, 59, false, null, 0);
        var validHours = ExpandField(fields[1], 0, 23, false, null, 0);
        var validDoms = ExpandField(fields[2], FirstDayOfMonth, LastDayOfMonth, false, null, 0);
        var validMonths = ExpandField(fields[3], FirstMonth, LastMonth, false, MonthNames, FirstMonth);
        var validDows = ExpandField(fields[4], Sunday, Saturday, true, DayNames, Sunday);

        if (validMinutes is null || validHours is null || validDoms is null || validMonths is null || validDows is null)
        {
            return Array.Empty<string>();
        }

        // Like cron: when both day fields are restricted a day matches if EITHER does; as soon as
        // one of them starts with a star, both must match (the star side matches every day).
        var bothDaysRestricted = !StartsWithStar(fields[2]) && !StartsWithStar(fields[4]);

        var current = now.AddMinutes(1);
        current = new DateTime(
            current.Year,
            current.Month,
            current.Day,
            current.Hour,
            current.Minute,
            0,
            current.Kind);

        var results = new List<string>(count);
        var iterations = 0;
        while (results.Count < count && iterations < MaxIterations)
        {
            iterations++;

            var domMatches = validDoms.Contains(current.Day);
            var dowMatches = validDows.Contains((int)current.DayOfWeek);
            var dayMatches = bothDaysRestricted ? domMatches || dowMatches : domMatches && dowMatches;

            if (validMonths.Contains(current.Month) &&
                dayMatches &&
                validHours.Contains(current.Hour) &&
                validMinutes.Contains(current.Minute))
            {
                results.Add(current.ToString("yyyy-MM-dd HH:mm (ddd)", CultureInfo.InvariantCulture));
            }

            current = current.AddMinutes(1);
        }

        return results;
    }

    private static bool StartsWithStar(string field) =>
        field.Length > 0 && field[0] == StarToken;

    /// <summary>
    /// Expands one field into the values it selects, or null when the field is malformed.
    /// </summary>
    /// <param name="names">
    /// Three-letter names the field accepts (month or day names, case-insensitive), the first
    /// one standing for <paramref name="firstNameValue"/>; null when the field takes numbers only.
    /// </param>
    private static HashSet<int>? ExpandField(
        string field,
        int min,
        int max,
        bool sundayWrap,
        string[]? names,
        int firstNameValue)
    {
        var values = new HashSet<int>();

        if (field == "*")
        {
            for (var i = min; i <= max; i++)
            {
                values.Add(i);
            }

            if (sundayWrap)
            {
                values.Add(Sunday);
            }

            return values;
        }

        foreach (var part in field.Split(','))
        {
            var rangeText = part;
            var step = 1;
            var hasStep = false;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                rangeText = part[..slash];
                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                {
                    return null;
                }

                hasStep = true;
            }

            int start;
            int end;
            if (rangeText == "*")
            {
                start = min;
                end = max;
            }
            else
            {
                var dash = rangeText.IndexOf('-');
                if (dash >= 0)
                {
                    if (!TryParseValue(rangeText[..dash], names, firstNameValue, out start)
                        || !TryParseValue(rangeText[(dash + 1)..], names, firstNameValue, out end))
                    {
                        return null;
                    }

                    // "FRI-SUN": Sunday closes the week here, so it reads as day 7.
                    if (sundayWrap && end == Sunday && start > Sunday)
                    {
                        end = SundayAlias;
                    }
                }
                else
                {
                    if (!TryParseValue(rangeText, names, firstNameValue, out start))
                    {
                        return null;
                    }

                    // "a/n" runs from a to the end of the field; a bare "a" is just a.
                    end = hasStep ? max : start;
                }
            }

            for (var i = start; i <= end; i += step)
            {
                AddValue(values, i, min, max, sundayWrap);
            }
        }

        return values;
    }

    private static bool TryParseValue(string text, string[]? names, int firstNameValue, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (names is not null)
        {
            var index = Array.FindIndex(names, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                value = firstNameValue + index;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static void AddValue(HashSet<int> values, int value, int min, int max, bool sundayWrap)
    {
        if (sundayWrap && value == SundayAlias)
        {
            values.Add(Sunday);
            return;
        }

        if (value >= min && value <= max)
        {
            values.Add(value);
        }
    }
}
