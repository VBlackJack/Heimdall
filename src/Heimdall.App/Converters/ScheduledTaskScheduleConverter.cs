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
using System.Windows;
using System.Windows.Data;
using Heimdall.App.Services;
using Heimdall.Core.Models;

namespace Heimdall.App.Converters;

/// <summary>
/// Describes when a scheduled task runs, in the current language, from the data the task stores.
/// </summary>
/// <remarks>
/// <para>
/// The text used to be built in English when the task was saved and stored in
/// <c>ScheduledTaskDto.Schedule</c>, so a French or Spanish user read "Every 30 min" in the grid
/// and the file carried the language of the session that wrote it. The schedule is now described
/// here, at display time, from <c>ScheduleType</c>, <c>IntervalMinutes</c> and
/// <c>TimeOfDay</c>.
/// </para>
/// <para>
/// Values, in order: the interval format, the daily format, the schedule type, the interval in
/// minutes, the time of day and the legacy text of a file written by an earlier version. The two
/// formats are bound from <c>LocalizationSource.Instance</c>, so a language change re-evaluates
/// the binding and the grid follows without reloading the tasks.
/// </para>
/// </remarks>
public sealed class ScheduledTaskScheduleConverter : IMultiValueConverter
{
    /// <summary>How many bound values the converter takes.</summary>
    public const int ValueCount = 6;
    private const int IntervalFormatIndex = 0;
    private const int DailyFormatIndex = 1;
    private const int ScheduleTypeIndex = 2;
    private const int IntervalMinutesIndex = 3;
    private const int TimeOfDayIndex = 4;
    private const int LegacyTextIndex = 5;

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < ValueCount)
        {
            return string.Empty;
        }

        return Describe(
            Text(values[IntervalFormatIndex]),
            Text(values[DailyFormatIndex]),
            Text(values[ScheduleTypeIndex]),
            values[IntervalMinutesIndex] is int minutes ? minutes : 0,
            Text(values[TimeOfDayIndex]),
            Text(values[LegacyTextIndex]));
    }

    /// <summary>
    /// The schedule text for a task, mirroring the cadence <c>TaskSchedulerService</c> applies:
    /// an Interval task with no positive interval runs hourly, and a Daily task with no
    /// parseable time runs at the default time.
    /// </summary>
    /// <param name="intervalFormat">The localized "every N minutes" format, slot 0 the minutes.</param>
    /// <param name="dailyFormat">The localized "daily at" format, slot 0 the time.</param>
    /// <param name="scheduleType">The stored schedule type.</param>
    /// <param name="intervalMinutes">The stored interval, in minutes.</param>
    /// <param name="timeOfDay">The stored time of day, HH:mm.</param>
    /// <param name="legacyText">The text an earlier version stored in the schedule field.</param>
    internal static string Describe(
        string intervalFormat,
        string dailyFormat,
        string scheduleType,
        int intervalMinutes,
        string timeOfDay,
        string legacyText)
    {
        if (string.Equals(scheduleType, nameof(ScheduleType.Interval), StringComparison.OrdinalIgnoreCase))
        {
            int interval = intervalMinutes > 0 ? intervalMinutes : TaskSchedulerService.DefaultIntervalMinutes;
            return string.Format(CultureInfo.InvariantCulture, intervalFormat, interval);
        }

        // A file written before the type and the time were stored carries only the text. Showing
        // it as it is beats inventing a time the task never had.
        if (string.IsNullOrWhiteSpace(timeOfDay) && !string.IsNullOrWhiteSpace(legacyText))
        {
            return legacyText;
        }

        string time = string.IsNullOrWhiteSpace(timeOfDay) ? TaskSchedulerService.DefaultTimeOfDay : timeOfDay;
        return string.Format(CultureInfo.InvariantCulture, dailyFormat, time);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, System.Windows.Data.Binding.DoNothing);
        return values;
    }

    // DependencyProperty.UnsetValue arrives while a container is being recycled.
    private static string Text(object? value)
        => value is string text && value != DependencyProperty.UnsetValue ? text : string.Empty;
}
