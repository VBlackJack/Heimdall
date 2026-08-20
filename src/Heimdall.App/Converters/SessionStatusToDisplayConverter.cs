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

        string? key = ResolveKey(status);
        return key is null ? status : _localize(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string? ResolveKey(string status)
    {
        if (string.Equals(status, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusConnected";
        }

        if (string.Equals(status, "RemoteSessionHandedOff", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusRemoteSessionHandedOff";
        }

        if (string.Equals(status, "Connecting", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusConnecting";
        }

        if (string.Equals(status, "Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusDisconnected";
        }

        if (string.Equals(status, "Disconnecting", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusDisconnecting";
        }

        if (string.Equals(status, "Reconnecting", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusReconnecting";
        }

        if (string.Equals(status, "Error", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusError";
        }

        if (string.Equals(status, "LaunchedExternalClient", StringComparison.OrdinalIgnoreCase))
        {
            return "SessionStatusLaunchedExternalClient";
        }

        return null;
    }
}
