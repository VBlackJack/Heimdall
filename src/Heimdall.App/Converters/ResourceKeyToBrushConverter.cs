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
using System.Windows.Media;

namespace Heimdall.App.Converters;

/// <summary>
/// Resolves a theme resource key (string) to the corresponding <see cref="Brush"/>
/// from the active application <see cref="ResourceDictionary"/>.
/// </summary>
/// <remarks>
/// Implements both <see cref="IValueConverter"/> (for legacy single-value bindings)
/// and <see cref="IMultiValueConverter"/> so XAML can pass a <c>ThemeRevision</c>
/// trigger as a second value. The trigger forces WPF to re-run the converter on
/// runtime theme swap - the value itself is ignored.
/// </remarks>
public sealed class ResourceKeyToBrushConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ResolveBrush(value as string);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] == DependencyProperty.UnsetValue)
        {
            return Brushes.Transparent;
        }

        return ResolveBrush(values[0] as string);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [DependencyProperty.UnsetValue];

    private static Brush ResolveBrush(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Brushes.Transparent;
        }

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }
}
