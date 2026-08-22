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
using Heimdall.App.Localization;

using Heimdall.App.ViewModels;

namespace Heimdall.App.Converters;

/// <summary>
/// Maps an internal session status key (e.g. "Disconnected") to a localized
/// display string. The raw status value remains the canonical logic key used by
/// state comparisons elsewhere; only the displayed text is localized.
/// Null, empty, whitespace, and unknown/free-form values pass through unchanged
/// so a diagnostic status message is never hidden.
/// </summary>
public sealed class SessionStatusToDisplayConverter : IValueConverter
{
    private readonly Func<string, string> _localize;

    public SessionStatusToDisplayConverter()
        : this(key => LocalizationSource.Instance[key])
    {
    }

    public SessionStatusToDisplayConverter(Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        _localize = localize;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string status || string.IsNullOrWhiteSpace(status))
        {
            return value;
        }

        string? key = SessionStatusDisplay.ResolveKey(status);
        return key is null ? status : _localize(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
