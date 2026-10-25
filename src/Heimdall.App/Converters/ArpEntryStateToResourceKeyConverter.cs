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
using Heimdall.App.ViewModels.Tools;

namespace Heimdall.App.Converters;

/// <summary>
/// Maps <see cref="ArpEntryState"/> values to themed brush resource keys.
/// </summary>
public sealed class ArpEntryStateToResourceKeyConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ArpEntryState state)
        {
            return DependencyProperty.UnsetValue;
        }

        return state switch
        {
            ArpEntryState.New => "SuccessTextBrush",
            ArpEntryState.Changed => "WarningTextBrush",
            ArpEntryState.Gone => "ErrorTextBrush",
            _ => "TextSecondaryBrush",
        };
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
