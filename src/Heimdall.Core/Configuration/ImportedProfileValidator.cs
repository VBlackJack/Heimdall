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

using System.Net;
using Heimdall.Core.Security;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Validates profile fields that cross the untrusted import boundary.
/// </summary>
public static class ImportedProfileValidator
{
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    public static IReadOnlyList<string> Validate(
        ServerProfileDto profile,
        IReadOnlySet<string> supportedConnectionTypes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(supportedConnectionTypes);

        List<string> errors = [];

        ValidateRequiredPort(errors, profile.RemotePort, nameof(profile.RemotePort));
        ValidateRequiredPort(errors, profile.LocalPort, nameof(profile.LocalPort));
        ValidateRequiredPort(errors, profile.SshPort, nameof(profile.SshPort));
        ValidateRequiredPort(errors, profile.WinRmPort, nameof(profile.WinRmPort));
        ValidateRequiredPort(errors, profile.FtpPort, nameof(profile.FtpPort));
        ValidateRequiredPort(errors, profile.VncPort, nameof(profile.VncPort));
        ValidateRequiredPort(errors, profile.TelnetPort, nameof(profile.TelnetPort));

        ValidateOptionalPort(errors, profile.SocksProxyPort, nameof(profile.SocksProxyPort));
        ValidateOptionalPort(errors, profile.RemoteBindPort, nameof(profile.RemoteBindPort));
        ValidateOptionalPort(errors, profile.RemoteLocalPort, nameof(profile.RemoteLocalPort));

        if (!Enum.IsDefined(profile.WinRmIdentityMode))
        {
            errors.Add(
                $"{nameof(profile.WinRmIdentityMode)}: unsupported value "
                + $"'{(int)profile.WinRmIdentityMode}'.");
        }

        if (string.IsNullOrWhiteSpace(profile.ConnectionType))
        {
            errors.Add($"{nameof(profile.ConnectionType)}: required.");
        }
        else if (!supportedConnectionTypes.Contains(profile.ConnectionType))
        {
            errors.Add($"{nameof(profile.ConnectionType)}: unsupported value '{profile.ConnectionType}'.");
        }

        if (!string.IsNullOrWhiteSpace(profile.RemoteServer)
            && CanonicalizeHost(profile.RemoteServer) is null)
        {
            errors.Add($"{nameof(profile.RemoteServer)}: not a usable host '{profile.RemoteServer}'.");
        }

        return errors;
    }

    /// <summary>
    /// The approved form of a host, or <see langword="null"/> when it cannot be approved.
    /// </summary>
    /// <remarks>
    /// <para>An IP literal is kept as written once it parses, so an address is never silently
    /// rewritten into another notation. Everything else goes through the domain check, which is
    /// what decides the approved form.</para>
    /// <para>An IP literal is accepted separately because the domain pattern covers IPv4 and
    /// hostnames only; without this an imported IPv6 host would be refused.</para>
    /// </remarks>
    internal static string? CanonicalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        string trimmed = host.Trim();
        if (IPAddress.TryParse(trimmed, out _))
        {
            return trimmed;
        }

        return InputValidator.TryCanonicalizeDomain(trimmed, out string canonical)
            ? canonical
            : null;
    }

    public static (List<ServerProfileDto> Valid, List<string> Failures) FilterValid(
        IEnumerable<ServerProfileDto> profiles,
        IReadOnlySet<string> supportedConnectionTypes)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(supportedConnectionTypes);

        List<ServerProfileDto> valid = [];
        List<string> failures = [];

        foreach (ServerProfileDto profile in profiles)
        {
            IReadOnlyList<string> errors = Validate(profile, supportedConnectionTypes);
            if (errors.Count == 0)
            {
                // The point of the check is lost if the value that goes on to be stored is not
                // the value that passed it. Writing the approved form back is what closes that
                // gap; the check above has already ruled out anything with no approved form.
                string? canonicalHost = CanonicalizeHost(profile.RemoteServer);
                if (canonicalHost is not null
                    && !string.Equals(canonicalHost, profile.RemoteServer, StringComparison.Ordinal))
                {
                    profile.RemoteServer = canonicalHost;
                }

                valid.Add(profile);
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? "<unnamed profile>"
                : profile.DisplayName.Trim();
            failures.Add($"{displayName}: {string.Join("; ", errors)}");
        }

        return (valid, failures);
    }

    private static void ValidateRequiredPort(List<string> errors, int value, string fieldName)
    {
        if (value is < MinPort or > MaxPort)
        {
            errors.Add($"{fieldName}: must be between {MinPort} and {MaxPort}.");
        }
    }

    private static void ValidateOptionalPort(List<string> errors, int value, string fieldName)
    {
        if (value != 0 && (value < MinPort || value > MaxPort))
        {
            errors.Add($"{fieldName}: must be 0 or between {MinPort} and {MaxPort}.");
        }
    }
}
