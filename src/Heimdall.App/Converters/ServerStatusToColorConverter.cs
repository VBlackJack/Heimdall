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
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Converters;

/// <summary>
/// Multi-value converter that merges connection type and connection state into a single
/// status indicator color. When connected/connecting/error, the state color wins;
/// when disconnected, the color reflects the connection type (RDP/SSH/SFTP).
/// Brush values are resolved from theme resources so they adapt to the active theme.
/// </summary>
/// <remarks>
/// Accepts 2 to 4 binding values: <c>[0]</c> connection type, <c>[1]</c> connection state,
/// optionally <c>[2]</c> the live <see cref="HealthState"/> from
/// <see cref="SessionHealthMonitor"/>, and optionally <c>[3]</c> a <c>ThemeRevision</c>
/// trigger that forces WPF to re-run the converter after a runtime theme swap
/// (the trigger value itself is ignored). The HealthState slot is back-compatible
/// - when absent or null, the disconnected branch falls back to the legacy
/// connection-type palette so any existing site that still passes only 2/3
/// values keeps rendering correctly.
/// </remarks>
public sealed class ServerStatusToColorConverter : IMultiValueConverter
{
    private readonly Func<string, Brush?> _resolveBrush;

    public ServerStatusToColorConverter()
        : this(key => Application.Current?.TryFindResource(key) as Brush)
    {
    }

    internal ServerStatusToColorConverter(Func<string, Brush?> resolveBrush)
    {
        _resolveBrush = resolveBrush ?? throw new ArgumentNullException(nameof(resolveBrush));
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2
            || values[0] == DependencyProperty.UnsetValue
            || values[1] == DependencyProperty.UnsetValue)
        {
            return ResolveBrush("NeutralIndicatorBrush", Brushes.Gray);
        }

        string connectionType = values[0]?.ToString()?.ToUpperInvariant() ?? string.Empty;
        string connectionState = values[1]?.ToString()?.ToLowerInvariant() ?? string.Empty;
        var healthState = values.Length >= 3 && values[2] is HealthState hs ? hs : null;
        // The optional ThemeRevision trigger (values[3]) is ignored - it only exists
        // to force WPF to re-evaluate the binding after a theme swap.

        // State-based colors take priority over type-based colors
        return connectionState switch
        {
            "connected" => ResolveBrush("SuccessIndicatorBrush", Brushes.Green),
            "remotesessionhandedoff" => ResolveBrush("InfoIndicatorBrush", Brushes.Cyan),
            "launchedexternalclient" => ResolveBrush("WarningIndicatorBrush", Brushes.Orange),
            "error" => ResolveBrush("ErrorIndicatorBrush", Brushes.Red),
            "initializing" or "validatingconfig" or "establishingtunnel"
                or "tunnelestablished" or "launchingrdp" or "launchingssh"
                or "launchingsftp" or "launchingftp" or "launchingvnc"
                or "launchingtelnet" or "launchinglocal" or "launchingcitrix" or "launchingwinrm"
                or "launchingweb"
                or "disconnecting" => ResolveBrush("WarningIndicatorBrush", Brushes.Orange),
            // Disconnected (or unknown state): prefer the live health verdict when
            // available, otherwise fall back to the legacy connection-type palette.
            _ => healthState is not null
                ? ResolveHealthBrush(healthState.Status)
                : ResolveConnectionTypeBrush(connectionType)
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => [DependencyProperty.UnsetValue, DependencyProperty.UnsetValue, DependencyProperty.UnsetValue, DependencyProperty.UnsetValue];

    private Brush ResolveHealthBrush(HealthStatus status) => status switch
    {
        HealthStatus.Up => ResolveBrush("SuccessIndicatorBrush", Brushes.Green),
        HealthStatus.Down => ResolveBrush("ErrorIndicatorBrush", Brushes.Red),
        HealthStatus.Probing => ResolveBrush("WarningIndicatorBrush", Brushes.Orange),
        _ => ResolveBrush("NeutralIndicatorBrush", Brushes.Gray)
    };

    private Brush ResolveConnectionTypeBrush(string connectionType) => connectionType switch
    {
        "RDP" => ResolveBrush("InfoIndicatorBrush", Brushes.Cyan),
        "SSH" => ResolveBrush("SuccessIndicatorBrush", Brushes.Green),
        "SFTP" => ResolveBrush("WarningIndicatorBrush", Brushes.Orange),
        "FTP" => ResolveBrush("WarningIndicatorBrush", Brushes.Orange),
        "VNC" => ResolveBrush("InfoIndicatorBrush", Brushes.Cyan),
        "WEB" => ResolveBrush("InfoIndicatorBrush", Brushes.Cyan),
        "TELNET" => ResolveBrush("SuccessIndicatorBrush", Brushes.Green),
        "CITRIX" => ResolveBrush("InfoIndicatorBrush", Brushes.Cyan),
        "LOCAL" => ResolveBrush("NeutralIndicatorBrush", Brushes.Gray),
        _ => ResolveBrush("NeutralIndicatorBrush", Brushes.Gray)
    };

    private Brush ResolveBrush(string resourceKey, Brush fallback)
        => _resolveBrush(resourceKey) ?? fallback;
}
