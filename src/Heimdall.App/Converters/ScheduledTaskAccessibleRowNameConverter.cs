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
using System.Windows.Data;

namespace Heimdall.App.Converters;

/// <summary>
/// Names a Scheduled grid row for a screen reader: the server, the connection type and the
/// schedule described in the current language.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AccessibleRowNameConverter"/> fills a localized format with the values bound after
/// it, and the schedule is itself computed from several bound values and two localized formats.
/// A <c>MultiBinding</c> cannot nest another one: WPF's binding collection accepts only
/// <c>Binding</c> items and the window fails to load. So the schedule values are bound flat,
/// next to the others, and this converter describes the schedule and then formats the row name
/// exactly as <see cref="AccessibleRowNameConverter"/> does. Every value is bound, the two
/// localized formats included, so a language change re-evaluates the whole name.
/// </para>
/// <para>
/// Values, in order: the row format, the server name, the connection type, then the six values
/// <see cref="ScheduledTaskScheduleConverter"/> takes.
/// </para>
/// </remarks>
public sealed class ScheduledTaskAccessibleRowNameConverter : IMultiValueConverter
{
    /// <summary>The values that precede the schedule's own: the row format, server and type.</summary>
    public const int LeadingValueCount = 3;

    /// <summary>
    /// How many bindings the converter takes in all, which is not the number of slots in the row
    /// format: three of the format's slots are filled from <see cref="LeadingValueCount"/> values
    /// and the schedule slot from <see cref="ScheduledTaskScheduleConverter.ValueCount"/>.
    /// </summary>
    public const int ValueCount = LeadingValueCount + ScheduledTaskScheduleConverter.ValueCount;

    private const int RowFormatIndex = 0;
    private const int ServerNameIndex = 1;
    private const int ConnectionTypeIndex = 2;

    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < ValueCount)
        {
            return string.Empty;
        }

        object schedule = new ScheduledTaskScheduleConverter().Convert(
            values[LeadingValueCount..], typeof(string), parameter, culture);

        return AccessibleRowNameConverter.Format(
            [values[RowFormatIndex], values[ServerNameIndex], values[ConnectionTypeIndex], schedule],
            culture);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, System.Windows.Data.Binding.DoNothing);
        return values;
    }
}
