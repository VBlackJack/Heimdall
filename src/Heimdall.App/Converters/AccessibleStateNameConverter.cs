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

namespace Heimdall.App.Converters;

/// <summary>
/// Builds an accessible name that carries a state the control only shows as a mark: a count badge
/// or a dot.
/// </summary>
/// <remarks>
/// Values: the plain label, the state (an <see cref="int"/> count or a <see cref="bool"/> flag),
/// and a format whose slot 0 is the label and slot 1 the state. With no state to report the name
/// is the plain label, so a screen reader hears nothing extra on a clean tab. The label and the
/// format are bound from <c>LocalizationSource.Instance</c> so a language change re-evaluates it.
/// </remarks>
public sealed class AccessibleStateNameConverter : IMultiValueConverter
{
    /// <inheritdoc />
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => Compose(
            values is { Length: > 0 } ? values[0] as string : null,
            values is { Length: > 1 } ? values[1] : null,
            values is { Length: > 2 } ? values[2] as string : null);

    /// <summary>The name for a label, a state and a format, as the converter composes it.</summary>
    internal static string Compose(string? label, object? state, string? format)
    {
        string plain = label ?? string.Empty;
        bool hasState = state switch
        {
            int count => count > 0,
            bool flag => flag,
            _ => false,
        };

        if (!hasState || string.IsNullOrEmpty(format) || state == DependencyProperty.UnsetValue)
        {
            return plain;
        }

        return string.Format(CultureInfo.CurrentCulture, format, plain, state);
    }

    /// <inheritdoc />
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
