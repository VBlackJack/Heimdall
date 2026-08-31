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
/// Builds a grid row's accessible name from a localized format and the row's own values.
/// </summary>
/// <remarks>
/// <para>
/// The first value is the format, bound from <c>LocalizationSource.Instance</c> so a locale
/// change re-evaluates the whole binding; every later value fills one of its slots. This is the
/// shape MainWindow already uses to build an <c>AutomationProperties.Name</c> out of a state and
/// two localized strings, applied to a row container instead of a button.
/// </para>
/// <para>
/// It exists because <c>IAccessibleItemViewModel</c> cannot reach most grid rows. Thirteen of the
/// twenty-one remaining grids are bound to types declared in <c>Heimdall.Core</c> - or, in one
/// case, to <see cref="System.Data.DataRowView" /> - and <c>Heimdall.Core</c> holds no project
/// reference, so those types cannot implement an interface that lives in <c>Heimdall.App</c>.
/// Pushing the interface down into Core would make transport and system-info records carry
/// presentation text they have no localizer to produce.
/// </para>
/// <para>
/// So the rule is: a row type that belongs to the application implements
/// <c>IAccessibleItemViewModel</c> and the container style enables
/// <c>ItemContainerAccessibilityBehavior</c>; a row type that belongs to Core or to the framework
/// is named declaratively from the container style through this converter. Both put the name on
/// the row container, which is the part that matters - a name set inside a cell template lands on
/// an element with no automation peer and never reaches a screen reader.
/// </para>
/// </remarks>
public sealed class AccessibleRowNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length == 0)
        {
            return string.Empty;
        }

        string? format = values[0] as string;
        object[] arguments = new object[Math.Max(0, values.Length - 1)];
        for (int i = 1; i < values.Length; i++)
        {
            // DependencyProperty.UnsetValue arrives while a container is being recycled, and
            // ToString() on it would put "{DependencyProperty.UnsetValue}" in front of a screen
            // reader.
            object? value = values[i];
            arguments[i - 1] = value == System.Windows.DependencyProperty.UnsetValue
                ? string.Empty
                : value ?? string.Empty;
        }

        if (string.IsNullOrEmpty(format))
        {
            // A missing key still leaves the row identifiable. Announcing the values without
            // their labels is worse than the format, and far better than the type name that the
            // absence of any name would produce.
            return string.Join(", ", arguments.Select(a => a.ToString()).Where(s => !string.IsNullOrEmpty(s)));
        }

        try
        {
            return string.Format(culture, format, arguments);
        }
        catch (FormatException)
        {
            // A format whose slot count exceeds the bindings supplied. Falling back keeps the row
            // named rather than throwing inside a binding, where the exception would be swallowed
            // and the row would silently go back to announcing its type.
            return string.Join(", ", arguments.Select(a => a.ToString()).Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        var values = new object[targetTypes.Length];
        Array.Fill(values, System.Windows.Data.Binding.DoNothing);
        return values;
    }
}
