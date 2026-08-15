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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Converters;

/// <summary>
/// Returns the protocol badge brush for active sessions and a transparent brush for
/// disconnected or unknown states.
/// </summary>
public sealed class PaletteActiveIndicatorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] == DependencyProperty.UnsetValue
            || values[1] == DependencyProperty.UnsetValue)
        {
            return Brushes.Transparent;
        }

        string connectionType = values[0]?.ToString()?.ToUpperInvariant() ?? string.Empty;
        string connectionState = values[1]?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(connectionState)
            || string.Equals(connectionState, "Disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return Brushes.Transparent;
        }

        return ResolveBadgeBrush(connectionType);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => [DependencyProperty.UnsetValue, DependencyProperty.UnsetValue];

    private static Brush ResolveBadgeBrush(string connectionType)
    {
        string resourceKey = ConnectionTypeCatalog.IsToolConnectionType(connectionType)
            ? "ToolBadgeBrush"
            : connectionType switch
            {
                "RDP" => "RdpBadgeBrush",
                "SSH" => "SshBadgeBrush",
                "SFTP" => "SftpBadgeBrush",
                "FTP" => "FtpBadgeBrush",
                "VNC" => "VncBadgeBrush",
                "TELNET" => "TelnetBadgeBrush",
                "CITRIX" => "CitrixBadgeBrush",
                "LOCAL" => "LocalBadgeBrush",
                _ => "BorderBrush"
            };

        return Application.Current?.TryFindResource(resourceKey) as Brush
            ?? Brushes.Transparent;
    }
}
