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
/// Converts a connection state value to a status indicator brush.
/// Connected = green, Error = red, transitional states = amber, idle = gray.
/// </summary>
/// <remarks>
/// Dual <see cref="IValueConverter"/> / <see cref="IMultiValueConverter"/> implementation:
/// the multi variant accepts a <c>ThemeRevision</c> trigger so bindings re-resolve brushes
/// when the active theme dictionary is swapped at runtime.
/// </remarks>
public sealed class ConnectionStateToBrushConverter : IValueConverter, IMultiValueConverter
{
    private readonly Func<string, Brush?> _resolveBrush;

    public ConnectionStateToBrushConverter()
        : this(key => Application.Current?.TryFindResource(key) as Brush)
    {
    }

    internal ConnectionStateToBrushConverter(Func<string, Brush?> resolveBrush)
    {
        _resolveBrush = resolveBrush ?? throw new ArgumentNullException(nameof(resolveBrush));
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return ResolveBrush(value);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] == DependencyProperty.UnsetValue)
        {
            return Brushes.Gray;
        }

        return ResolveBrush(values[0]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [DependencyProperty.UnsetValue];

    private Brush ResolveBrush(object? value)
    {
        string state = value?.ToString()?.ToLowerInvariant() ?? string.Empty;
        string resourceKey = state switch
        {
            "connected" => "SuccessIndicatorBrush",
            "error" => "ErrorIndicatorBrush",
            "disconnected" => "TextSecondaryBrush",
            "initializing" or "validatingconfig" or "establishingtunnel"
                or "launchingrdp" or "launchingssh" or "launchingsftp"
                or "launchinglocal" or "launchingvnc" or "launchingftp"
                or "launchingtelnet" or "launchingcitrix" or "launchingwinrm" or "launchingweb"
                or "launchedexternalclient" or "disconnecting" => "WarningIndicatorBrush",
            "tunnelestablished" or "remotesessionhandedoff" => "InfoIndicatorBrush",
            _ => "TextSecondaryBrush"
        };

        return _resolveBrush(resourceKey) ?? Brushes.Gray;
    }
}
