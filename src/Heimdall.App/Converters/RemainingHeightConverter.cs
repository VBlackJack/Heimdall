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

using System;
using System.Globalization;
using System.Windows.Data;

namespace Heimdall.App.Converters;

/// <summary>
/// Gives a panel the height its container has left once another region has kept its share,
/// never more than the panel's own cap.
/// </summary>
/// <remarks>
/// <para>Bound as a <c>MultiBinding</c> on <c>MaxHeight</c>, with three values in order: the
/// container's height, the height reserved for the other region, and the panel's cap.</para>
/// <para>A panel in an auto-sized row is served before a star-sized neighbour, so a cap alone
/// lets it take everything in a short window and push the neighbour out of view. This is what
/// lets the auto row give way instead.</para>
/// </remarks>
public sealed class RemainingHeightConverter : IMultiValueConverter
{
    private const int AvailableIndex = 0;
    private const int ReservedIndex = 1;
    private const int CapIndex = 2;
    private const int ValueCount = 3;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < ValueCount
            || values[CapIndex] is not double cap)
        {
            return double.PositiveInfinity;
        }

        if (values[AvailableIndex] is not double available
            || double.IsNaN(available)
            || available <= 0
            || values[ReservedIndex] is not double reserved)
        {
            // A container that has not been laid out yet has nothing to share. Answering zero here
            // would collapse the panel for the first frame and open it on the second.
            return cap;
        }

        return Math.Max(0, Math.Min(cap, available - reserved));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
