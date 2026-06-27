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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Validates configuration objects against expected schemas and constraints.
/// </summary>
public static partial class SchemaValidator
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MinResolution = 640;
    private const int MaxResolution = 7680;
    private const int MinRdpFixedDimension = 200;
    private const int MaxRdpFixedWidth = 7680;
    private const int MaxRdpFixedHeight = 4320;
    private const int MinColorDepth = 8;
    private const int MaxColorDepth = 32;
    private const int DisabledRdpResizeDelayMs = 0;
    private const int MinRdpResizeDelayMs = 1000;
    private const int MaxRdpResizeDelayMs = 60000;
    private const int DisabledRdpConnectWatchdogTimeoutMs = 0;
    private const int MinRdpConnectWatchdogTimeoutMs = 5000;
    private const int MaxRdpConnectWatchdogTimeoutMs = 600000;
    private const int MinRdpKeepAliveIntervalMs = 5000;
    private const int MaxRdpKeepAliveIntervalMs = 300000;
    private const int MaxEmbeddedSessionsLimit = 20;
    private const int MinAntiIdleInterval = 10;
    private const int MaxAntiIdleInterval = 600;

    private static readonly HashSet<string> ValidLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "fr"
    };

    private static readonly HashSet<string> ValidThemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dracula", "Drakul", "Striga", "Cinder", "Bracken", "Tarn", "Mortis", "Slate",
        "Magellan", "Voivode", "Carmilla", "Whitby", "Vesper", "Parchment", "Folio",
        "Wormwood", "Sconce"
    };

    private static readonly HashSet<string> ValidConnectionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RDP", "SSH", "SFTP"
    };

    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "External", "Embedded"
    };

    private static readonly HashSet<string> ValidAspectRatios = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stretch", "Auto", "16:9", "4:3", "21:9"
    };

    /// <summary>
    /// Validates an <see cref="AppSettings"/> instance against expected constraints.
    /// </summary>
    public static ValidationResult ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<string>();

        ValidateRange(errors, settings.DefaultResolutionWidth,
            MinResolution, MaxResolution, nameof(settings.DefaultResolutionWidth));
        ValidateRange(errors, settings.DefaultResolutionHeight,
            MinResolution, MaxResolution, nameof(settings.DefaultResolutionHeight));

        if (!ValidLocales.Contains(settings.DefaultLocale))
        {
            errors.Add($"{nameof(settings.DefaultLocale)}: unsupported locale '{settings.DefaultLocale}'.");
        }

        if (!ValidThemes.Contains(settings.DefaultTheme))
        {
            errors.Add($"{nameof(settings.DefaultTheme)}: unsupported theme '{settings.DefaultTheme}'.");
        }

        ValidateRange(errors, settings.TunnelEstablishmentDelayMs, 0, 60000,
            nameof(settings.TunnelEstablishmentDelayMs));
        ValidateRange(errors, settings.TunnelRetryDelayMs, 0, 60000,
            nameof(settings.TunnelRetryDelayMs));
        ValidateRange(errors, settings.ProcessKillTimeoutMs, 0, 60000,
            nameof(settings.ProcessKillTimeoutMs));

        ValidateRange(errors, settings.HostKeyProbeTimeoutMs, 1000, 120000,
            nameof(settings.HostKeyProbeTimeoutMs));
        ValidateRange(errors, settings.TelnetConnectTimeoutMs, 1000, 120000,
            nameof(settings.TelnetConnectTimeoutMs));
        ValidateRange(errors, settings.CredentialProviderTimeoutMs, 1000, 120000,
            nameof(settings.CredentialProviderTimeoutMs));
        ValidateRange(errors, settings.WindowsHelloGraceMinutes, 0, 1440,
            nameof(settings.WindowsHelloGraceMinutes));
        ValidateRange(errors, settings.RdpCredentialAutofillTimeoutMs, 5000, 300000,
            nameof(settings.RdpCredentialAutofillTimeoutMs));
        ValidateRange(errors, settings.RdpArtifactCleanupDelayMs, 1000, 60000,
            nameof(settings.RdpArtifactCleanupDelayMs));
        ValidateRdpResizeDelay(errors, settings.RdpResizeEnableDelayMs,
            nameof(settings.RdpResizeEnableDelayMs));
        ValidateRdpConnectWatchdogTimeout(errors, settings.RdpConnectWatchdogTimeoutMs,
            nameof(settings.RdpConnectWatchdogTimeoutMs));
        ValidateRange(errors, settings.RdpKeepAliveIntervalMs,
            MinRdpKeepAliveIntervalMs, MaxRdpKeepAliveIntervalMs,
            nameof(settings.RdpKeepAliveIntervalMs));
        ValidateRange(errors, settings.SshKeepAliveIntervalSeconds, 5, 600,
            nameof(settings.SshKeepAliveIntervalSeconds));
        ValidateRange(errors, settings.SshAutoReconnectAttempts, 1, 10,
            nameof(settings.SshAutoReconnectAttempts));
        ValidateRange(errors, settings.SshAutoReconnectFirstDelaySeconds, 1, 600,
            nameof(settings.SshAutoReconnectFirstDelaySeconds));
        ValidateRange(errors, settings.SshAutoReconnectSecondDelaySeconds, 1, 600,
            nameof(settings.SshAutoReconnectSecondDelaySeconds));
        ValidateRange(errors, settings.SshAutoReconnectSubsequentDelaySeconds, 1, 600,
            nameof(settings.SshAutoReconnectSubsequentDelaySeconds));
        ValidateRange(errors, settings.SshConnectTimeExitWindowSeconds, 0, 600,
            nameof(settings.SshConnectTimeExitWindowSeconds));
        ValidateRange(errors, settings.SshTmoutResetIntervalSeconds, 0, 3600,
            nameof(settings.SshTmoutResetIntervalSeconds));
        ValidateRange(errors, settings.PlinkPortCheckIntervalMs, 500, 30000,
            nameof(settings.PlinkPortCheckIntervalMs));
        ValidateRange(errors, settings.PlinkKillGracePeriodMs, 500, 30000,
            nameof(settings.PlinkKillGracePeriodMs));
        ValidateRange(errors, settings.SftpUploadDebounceMs, 500, 30000,
            nameof(settings.SftpUploadDebounceMs));
        ValidateRange(errors, settings.ServerShutdownTimeoutMs, 500, 30000,
            nameof(settings.ServerShutdownTimeoutMs));
        ValidateRange(errors, settings.SleepPreventionIntervalSeconds, 10, 600,
            nameof(settings.SleepPreventionIntervalSeconds));
        ValidateRange(errors, settings.FileLoggerFlushIntervalMs, 500, 30000,
            nameof(settings.FileLoggerFlushIntervalMs));
        ValidateRange(errors, settings.DefaultRdpTunnelPort, 1, 65535,
            nameof(settings.DefaultRdpTunnelPort));
        ValidateRange(errors, settings.DefaultSshTunnelPort, 1, 65535,
            nameof(settings.DefaultSshTunnelPort));
        ValidateRange(errors, settings.EphemeralHttpPort, 1, 65535,
            nameof(settings.EphemeralHttpPort));
        ValidateRange(errors, settings.EphemeralTftpPort, 1, 65535,
            nameof(settings.EphemeralTftpPort));

        if (!string.IsNullOrWhiteSpace(settings.PowerShellExecutionPolicy)
            && !Security.InputValidator.IsValidExecutionPolicy(settings.PowerShellExecutionPolicy))
        {
            errors.Add($"{nameof(settings.PowerShellExecutionPolicy)}: unknown policy '{settings.PowerShellExecutionPolicy}'.");
        }

        if (!ValidModes.Contains(settings.SshDefaultMode))
        {
            errors.Add($"{nameof(settings.SshDefaultMode)}: must be 'External' or 'Embedded'.");
        }

        if (!ValidModes.Contains(settings.RdpDefaultMode))
        {
            errors.Add($"{nameof(settings.RdpDefaultMode)}: must be 'External' or 'Embedded'.");
        }

        ValidateRange(errors, settings.AntiIdleIntervalSeconds,
            MinAntiIdleInterval, MaxAntiIdleInterval, nameof(settings.AntiIdleIntervalSeconds));
        ValidateRange(errors, settings.RdpDefaultColorDepth,
            MinColorDepth, MaxColorDepth, nameof(settings.RdpDefaultColorDepth));
        ValidateRange(errors, settings.MaxEmbeddedSessions,
            1, MaxEmbeddedSessionsLimit, nameof(settings.MaxEmbeddedSessions));
        ValidateRange(errors, settings.SidebarWidth, 0, 1000, nameof(settings.SidebarWidth));

        ValidateVault(errors, settings);

        return new ValidationResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Validates the master-password vault settings for internal consistency.
    /// </summary>
    private static void ValidateVault(List<string> errors, AppSettings settings)
    {
        if (settings.VaultEnabled && string.IsNullOrEmpty(settings.VaultWrappedDek))
        {
            errors.Add(
                $"{nameof(settings.VaultEnabled)}: an enabled vault requires a non-empty {nameof(settings.VaultWrappedDek)}.");
        }

        if (!Enum.IsDefined(settings.VaultMigrationState))
        {
            errors.Add($"{nameof(settings.VaultMigrationState)}: unknown migration state.");
        }
    }

    /// <summary>
    /// Validates an <see cref="ServerProfileDto"/> instance against expected constraints.
    /// </summary>
    public static ValidationResult ValidateServer(ServerProfileDto server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(server.Id))
        {
            errors.Add($"{nameof(server.Id)}: required.");
        }

        if (string.IsNullOrWhiteSpace(server.DisplayName))
        {
            errors.Add($"{nameof(server.DisplayName)}: required.");
        }

        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            errors.Add($"{nameof(server.RemoteServer)}: required.");
        }
        else if (!HostnameRegex().IsMatch(server.RemoteServer))
        {
            errors.Add($"{nameof(server.RemoteServer)}: invalid hostname or IP address.");
        }

        ValidatePort(errors, server.RemotePort, nameof(server.RemotePort));
        ValidatePort(errors, server.LocalPort, nameof(server.LocalPort));
        ValidatePort(errors, server.SshPort, nameof(server.SshPort));

        if (!ValidConnectionTypes.Contains(server.ConnectionType))
        {
            errors.Add($"{nameof(server.ConnectionType)}: must be 'RDP', 'SSH', or 'SFTP'.");
        }

        if (!ValidModes.Contains(server.SshMode))
        {
            errors.Add($"{nameof(server.SshMode)}: must be 'External' or 'Embedded'.");
        }

        if (!ValidModes.Contains(server.RdpMode))
        {
            errors.Add($"{nameof(server.RdpMode)}: must be 'External' or 'Embedded'.");
        }

        if (!ValidAspectRatios.Contains(server.RdpAspectRatio))
        {
            errors.Add($"{nameof(server.RdpAspectRatio)}: unsupported aspect ratio '{server.RdpAspectRatio}'.");
        }

        ValidateRange(errors, server.RdpColorDepth,
            MinColorDepth, MaxColorDepth, nameof(server.RdpColorDepth));
        ValidateRange(errors, server.RdpAudioMode, 0, 2, nameof(server.RdpAudioMode));
        ValidateRdpResolutionProfile(errors, server);

        return new ValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateRdpResolutionProfile(List<string> errors, ServerProfileDto server)
    {
        if (server.RdpResolutionMode == RdpResolutionMode.Fixed)
        {
            ValidateRange(errors, server.RdpFixedWidth,
                MinRdpFixedDimension, MaxRdpFixedWidth, nameof(server.RdpFixedWidth));
            ValidateRange(errors, server.RdpFixedHeight,
                MinRdpFixedDimension, MaxRdpFixedHeight, nameof(server.RdpFixedHeight));
        }
        else if (server.RdpFixedWidth < 0)
        {
            errors.Add($"{nameof(server.RdpFixedWidth)}: value {server.RdpFixedWidth} must be zero or positive.");
        }
        else if (server.RdpFixedHeight < 0)
        {
            errors.Add($"{nameof(server.RdpFixedHeight)}: value {server.RdpFixedHeight} must be zero or positive.");
        }

        if (server.RdpResizeEnableDelayMs.HasValue)
        {
            ValidateRdpResizeDelay(errors, server.RdpResizeEnableDelayMs.Value,
                nameof(server.RdpResizeEnableDelayMs));
        }
    }

    private static void ValidateRdpResizeDelay(List<string> errors, int value, string fieldName)
    {
        // Zero explicitly disables the resize lockout; 1..999 ms is too short to be meaningful.
        if (value == DisabledRdpResizeDelayMs)
        {
            return;
        }

        ValidateRange(errors, value, MinRdpResizeDelayMs, MaxRdpResizeDelayMs, fieldName);
    }

    private static void ValidateRdpConnectWatchdogTimeout(List<string> errors, int value, string fieldName)
    {
        if (value == DisabledRdpConnectWatchdogTimeoutMs)
        {
            return;
        }

        ValidateRange(
            errors,
            value,
            MinRdpConnectWatchdogTimeoutMs,
            MaxRdpConnectWatchdogTimeoutMs,
            fieldName);
    }

    /// <summary>
    /// Validates an <see cref="SshGatewayDto"/> instance against expected constraints.
    /// </summary>
    public static ValidationResult ValidateGateway(SshGatewayDto gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(gateway.Id))
        {
            errors.Add($"{nameof(gateway.Id)}: required.");
        }

        if (string.IsNullOrWhiteSpace(gateway.Name))
        {
            errors.Add($"{nameof(gateway.Name)}: required.");
        }

        if (string.IsNullOrWhiteSpace(gateway.Host))
        {
            errors.Add($"{nameof(gateway.Host)}: required.");
        }
        else if (!HostnameRegex().IsMatch(gateway.Host))
        {
            errors.Add($"{nameof(gateway.Host)}: invalid hostname or IP address.");
        }

        ValidatePort(errors, gateway.Port, nameof(gateway.Port));

        if (string.IsNullOrWhiteSpace(gateway.User))
        {
            errors.Add($"{nameof(gateway.User)}: required.");
        }

        if (!string.IsNullOrEmpty(gateway.KeyPath) && !File.Exists(gateway.KeyPath))
        {
            errors.Add($"{nameof(gateway.KeyPath)}: file not found '{gateway.KeyPath}'.");
        }

        if (gateway.ParentGatewayId == gateway.Id && !string.IsNullOrEmpty(gateway.Id))
        {
            errors.Add($"{nameof(gateway.ParentGatewayId)}: gateway cannot be its own parent.");
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateRange(List<string> errors, int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name}: value {value} is outside the valid range [{min}..{max}].");
        }
    }

    private static void ValidatePort(List<string> errors, int port, string name)
    {
        ValidateRange(errors, port, MinPort, MaxPort, name);
    }

    /// <summary>
    /// Matches valid hostnames, FQDNs, IPv4, and IPv6 addresses.
    /// </summary>
    [GeneratedRegex(@"^(?:[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?\.)*[a-zA-Z0-9](?:[a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?$|^\d{1,3}(?:\.\d{1,3}){3}$|^\[?[0-9a-fA-F:]+\]?$")]
    private static partial Regex HostnameRegex();
}

/// <summary>
/// Result of a schema validation operation.
/// </summary>
/// <param name="IsValid">Whether the validation passed without errors.</param>
/// <param name="Errors">List of validation error messages (empty if valid).</param>
public sealed record ValidationResult(bool IsValid, List<string> Errors);
