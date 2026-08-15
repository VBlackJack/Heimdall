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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Converters;

/// <summary>
/// Converts a connection type string (RDP, SSH, SFTP, TOOL:PING, etc.) to the
/// corresponding protocol or tool category accent <see cref="Brush"/>.
/// Tool types resolve to per-category brushes (Network, Security, Encoding, System)
/// via <see cref="ToolRegistry"/> static lookup.
/// </summary>
/// <remarks>
/// Implements both <see cref="IValueConverter"/> (for legacy single-value bindings
/// and direct code-behind use) and <see cref="IMultiValueConverter"/> (so XAML can
/// pass a <c>ThemeRevision</c> trigger as a second value to force re-evaluation
/// when the active theme dictionary is swapped at runtime).
/// </remarks>
public sealed class ConnectionTypeToColorConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return ResolveBrush(value);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;

    public object? Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        // values[0] = connection type; values[1..] = theme revision triggers (ignored here,
        // they only exist to force WPF to re-run this converter on theme swap).
        if (values.Length == 0 || values[0] == DependencyProperty.UnsetValue)
        {
            return Brushes.Gray;
        }

        return ResolveBrush(values[0]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => [DependencyProperty.UnsetValue];

    private static Brush ResolveBrush(object? value)
    {
        string typeStr = value?.ToString()?.ToUpperInvariant() ?? string.Empty;

        string resourceKey;
        if (ConnectionTypeCatalog.IsToolConnectionType(typeStr))
        {
            resourceKey = ToolRegistry.GetCategoryBrushKey(typeStr);
        }
        else
        {
            resourceKey = typeStr switch
            {
                "RDP" => "ProtocolRdpBrush",
                "SSH" => "ProtocolSshBrush",
                "WINRM" => "ProtocolWinRmBrush",
                "SFTP" => "ProtocolSftpBrush",
                "VNC" => "ProtocolVncBrush",
                "TELNET" => "ProtocolTelnetBrush",
                "FTP" => "ProtocolFtpBrush",
                "CITRIX" => "ProtocolCitrixBrush",
                "LOCAL" => "ProtocolLocalBrush",
                _ => "TextSecondaryBrush"
            };
        }

        return Application.Current.TryFindResource(resourceKey) as Brush
            ?? Brushes.Gray;
    }
}
