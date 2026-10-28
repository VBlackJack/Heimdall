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

using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Heimdall.App.Converters;

/// <summary>
/// Converts a hex color string (e.g., "#RRGGBB") to a <see cref="SolidColorBrush"/>.
/// Returns the fallback value when the input is null, empty, or not a valid color.
/// </summary>
/// <remarks>
/// A tab group or a folder paints one colour on every row it owns, so the same string is
/// converted again for each of them. The brushes are frozen and kept per colour string, which
/// makes them shareable and spares the allocation; the cache is bounded so a stream of distinct
/// strings cannot grow it without limit.
/// </remarks>
public sealed class StringToBrushConverter : IValueConverter
{
    /// <summary>Most colour strings whose brush is kept. Past it, a brush is made and not kept.</summary>
    internal const int MaxCachedBrushes = 256;

    private readonly ConcurrentDictionary<string, SolidColorBrush> _brushes = new(StringComparer.Ordinal);

    /// <summary>How many brushes are currently kept.</summary>
    internal int CachedBrushCount => _brushes.Count;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string colorStr && !string.IsNullOrWhiteSpace(colorStr))
        {
            if (_brushes.TryGetValue(colorStr, out SolidColorBrush? cached))
            {
                return cached;
            }

            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorStr);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                if (_brushes.Count < MaxCachedBrushes)
                {
                    _brushes.TryAdd(colorStr, brush);
                }

                return brush;
            }
            catch (FormatException)
            {
                // Invalid color string, fall through
            }
        }

        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}
