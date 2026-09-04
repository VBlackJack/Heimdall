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
using Heimdall.Core.Models;
using Heimdall.Core.Rdp;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Validates configuration objects against expected schemas and constraints.
/// </summary>
public static partial class SchemaValidator
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int MinRdpFixedDimension = RdpDisplayLimits.MinimumFixedDimension;
    private const int MaxRdpFixedWidth = RdpDisplayLimits.MaximumFixedWidth;
    private const int MaxRdpFixedHeight = RdpDisplayLimits.MaximumFixedHeight;

    private static readonly HashSet<string> ValidLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "fr"
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

        // Every numeric bound, from the one declaration each setting carries. Spelling a bound
        // here again is how the loader and the settings screen came to disagree.
        ValidateDeclaredRanges(errors, settings);

        if (!ValidLocales.Contains(settings.DefaultLocale))
        {
            errors.Add($"{nameof(settings.DefaultLocale)}: unsupported locale '{settings.DefaultLocale}'.");
        }

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

        if (settings.VaultHelloEnrolled)
        {
            if (string.IsNullOrEmpty(settings.VaultId))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultId)}.");
            }

            if (string.IsNullOrEmpty(settings.VaultHelloWrappedDek))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultHelloWrappedDek)}.");
            }

            if (string.IsNullOrEmpty(settings.VaultHelloChallenge))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultHelloChallenge)}.");
            }

            if (string.IsNullOrEmpty(settings.VaultHelloSalt))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultHelloSalt)}.");
            }

            if (string.IsNullOrEmpty(settings.VaultHelloCredentialName))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultHelloCredentialName)}.");
            }

            if (string.IsNullOrEmpty(settings.VaultHelloPublicKeyHash))
            {
                errors.Add(
                    $"{nameof(settings.VaultHelloEnrolled)}: an enrolled Windows Hello vault requires a non-empty {nameof(settings.VaultHelloPublicKeyHash)}.");
            }
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

        if (ConnectionTypeCatalog.RequiresRemoteServer(server.ConnectionType)
            && string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            errors.Add($"{nameof(server.RemoteServer)}: required.");
        }
        else if (!string.IsNullOrWhiteSpace(server.RemoteServer)
            && !HostnameRegex().IsMatch(server.RemoteServer))
        {
            errors.Add($"{nameof(server.RemoteServer)}: invalid hostname or IP address.");
        }

        ValidatePort(errors, server.RemotePort, nameof(server.RemotePort));
        ValidatePort(errors, server.LocalPort, nameof(server.LocalPort));
        ValidatePort(errors, server.SshPort, nameof(server.SshPort));

        if (!ConnectionTypeCatalog.IsKnown(server.ConnectionType))
        {
            errors.Add(
                $"{nameof(server.ConnectionType)}: unsupported type '{server.ConnectionType}'.");
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

        ValidateDeclaredRanges(errors, server);
        ValidateRdpResolutionProfile(errors, server);
        ValidateWinRmTransport(errors, server);

        return new ValidationResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Reports a WinRM profile that speaks TLS to the plaintext port.
    /// </summary>
    /// <remarks>
    /// The server dialog moves the port when TLS is ticked, so a profile made in the
    /// interface is already coherent. Every other boundary keeps an explicit port exactly as
    /// written - the deserializer derives one only when the field is absent, and the launch
    /// builder honours an explicit port whether or not TLS is on. An imported or hand-edited
    /// profile therefore connected with TLS to 5985 and nothing said so.
    /// <para>
    /// Reported rather than corrected, and here rather than in a fourth place that silently
    /// rewrites the value. An explicit port must keep winning over a default - that part was
    /// never the defect - and a listener deliberately serving TLS on 5985 is unusual rather
    /// than impossible. What was missing is saying it. Load turns this into a warning, so a
    /// profile already on disk still opens.
    /// </para>
    /// </remarks>
    private static void ValidateWinRmTransport(List<string> errors, ServerProfileDto server)
    {
        // Only a WinRM profile makes a transport claim. Every other profile carries the same
        // fields at their defaults, and reading them would report a defect on servers that
        // never speak WinRM.
        if (!string.Equals(server.ConnectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (server.WinRmUseSsl && server.WinRmPort == DefaultPorts.WinRmHttp)
        {
            errors.Add(
                $"{nameof(server.WinRmPort)}: TLS is enabled but the port is the plaintext "
                + $"default {DefaultPorts.WinRmHttp}; WinRM over TLS listens on "
                + $"{DefaultPorts.WinRmHttps}.");
        }
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
            // The per-profile override means the same thing as the application setting, so it is
            // bounded by that setting's one declaration rather than by a second spelling here.
            SettingRange range = SettingRanges.Of(nameof(AppSettings.RdpResizeEnableDelayMs));
            int value = server.RdpResizeEnableDelayMs.Value;
            if (!range.Accepts(value))
            {
                ValidateRange(errors, value, range.Min, range.Max, nameof(server.RdpResizeEnableDelayMs));
            }
        }
    }

    /// <summary>
    /// Diagnoses every <see cref="SettingRangeAttribute"/> the object's type declares.
    /// </summary>
    /// <remarks>
    /// The one range pass. A declared "off" value (zero, by <see cref="SettingRangeAttribute.ZeroMeansOff"/>)
    /// is accepted without a word, which is what the two hand-written zero-or-range methods this
    /// replaces used to do for their two settings, and what the loader wrongly did not do for the
    /// anti-idle interval.
    /// </remarks>
    private static void ValidateDeclaredRanges(List<string> errors, object target)
    {
        foreach (SettingRange range in SettingRanges.For(target.GetType()).Values)
        {
            int value = SettingRanges.ValueOf(target, range.PropertyName);
            if (range.DisabledValue.HasValue && value == range.DisabledValue.Value)
            {
                continue;
            }

            ValidateRange(errors, value, range.Min, range.Max, range.PropertyName);
        }
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

        if (gateway.ParentGatewayId == gateway.Id && !string.IsNullOrEmpty(gateway.Id))
        {
            errors.Add($"{nameof(gateway.ParentGatewayId)}: gateway cannot be its own parent.");
        }

        return new ValidationResult(errors.Count == 0, errors);
    }

    internal static ValidationResult DiagnoseSettingsLoad(AppSettings settings)
    {
        ValidationResult strictResult = ValidateSettings(settings);
        List<string> blockingMessages = [];
        ValidateVault(blockingMessages, settings);
        return ValidationResult.FromDiagnostics(
            strictResult.Errors.Select(message => new ValidationDiagnostic(
                blockingMessages.Contains(message, StringComparer.Ordinal)
                    ? ValidationSeverity.Error
                    : ValidationSeverity.Warning,
                message)));
    }

    internal static ValidationResult DiagnoseServerLoad(ServerProfileDto server)
    {
        ValidationResult strictResult = ValidateServer(server);
        return ValidationResult.FromDiagnostics(
            strictResult.Errors.Select(message =>
                new ValidationDiagnostic(ValidationSeverity.Warning, message)));
    }

    internal static ValidationResult DiagnoseGatewayLoad(SshGatewayDto gateway)
    {
        ValidationResult strictResult = ValidateGateway(gateway);
        List<string> blockingMessages = [];
        ValidateGatewayWriteInvariants(blockingMessages, gateway);
        return ValidationResult.FromDiagnostics(
            strictResult.Errors.Select(message => new ValidationDiagnostic(
                blockingMessages.Contains(message, StringComparer.Ordinal)
                    ? ValidationSeverity.Error
                    : ValidationSeverity.Warning,
                message)));
    }

    internal static ValidationResult ValidateSettingsWriteInvariants(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        List<string> errors = [];
        ValidateVault(errors, settings);
        return new ValidationResult(errors.Count == 0, errors);
    }

    internal static ValidationResult ValidateGatewayWriteInvariants(SshGatewayDto gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        List<string> errors = [];
        ValidateGatewayWriteInvariants(errors, gateway);
        return new ValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateGatewayWriteInvariants(
        List<string> errors,
        SshGatewayDto gateway)
    {
        if (gateway.ParentGatewayId == gateway.Id && !string.IsNullOrEmpty(gateway.Id))
        {
            errors.Add($"{nameof(gateway.ParentGatewayId)}: gateway cannot be its own parent.");
        }
    }

    /// <summary>
    /// Records a value outside its recommended range, without altering it.
    /// </summary>
    /// <remarks>
    /// The sentence says what the loader does, which is nothing: every range check reaches the
    /// log through <see cref="DiagnoseSettingsLoad"/> or <see cref="DiagnoseServerLoad"/>, and the
    /// load path preserves what the file said by contract, so that a file written by a newer
    /// build survives an older one. Whether an out-of-range value then bites is decided at its
    /// use site, setting by setting. The old sentence, "outside the valid range", read as a
    /// correction the loader never made: a 240000 ms tunnel delay was warned about and then waited
    /// out in full.
    /// </remarks>
    private static void ValidateRange(List<string> errors, int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            errors.Add(
                $"{name}: value {value} is outside the recommended range [{min}..{max}]; "
                + "the loader keeps it as written.");
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
public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationDiagnostic(ValidationSeverity Severity, string Message);

/// <summary>
/// Result of a schema validation operation.
/// </summary>
public sealed class ValidationResult
{
    public ValidationResult(bool isValid, List<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
        Warnings = [];
        Diagnostics = errors
            .Select(message => new ValidationDiagnostic(ValidationSeverity.Error, message))
            .ToArray();
    }

    private ValidationResult(IReadOnlyList<ValidationDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
        Errors = diagnostics
            .Where(diagnostic => diagnostic.Severity == ValidationSeverity.Error)
            .Select(diagnostic => diagnostic.Message)
            .ToList();
        Warnings = diagnostics
            .Where(diagnostic => diagnostic.Severity == ValidationSeverity.Warning)
            .Select(diagnostic => diagnostic.Message)
            .ToList();
        IsValid = Errors.Count == 0;
    }

    public bool IsValid { get; }

    public List<string> Errors { get; }

    public List<string> Warnings { get; }

    public IReadOnlyList<ValidationDiagnostic> Diagnostics { get; }

    internal static ValidationResult FromDiagnostics(
        IEnumerable<ValidationDiagnostic> diagnostics)
    {
        return new ValidationResult(diagnostics.ToArray());
    }
}
