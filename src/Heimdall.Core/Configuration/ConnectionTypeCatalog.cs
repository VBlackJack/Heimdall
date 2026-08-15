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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Canonical connection-type names shared by configuration validation and profile import.
/// </summary>
public static class ConnectionTypeCatalog
{
    /// <summary>The marker a connection type carries when its tab hosts a tool.</summary>
    public const string ToolPrefix = "TOOL:";

    private static readonly IReadOnlyDictionary<string, string> CanonicalMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RDP"] = "RDP",
            ["SSH"] = "SSH",
            ["SFTP"] = "SFTP",
            ["VNC"] = "VNC",
            ["TELNET"] = "Telnet",
            ["FTP"] = "FTP",
            ["CITRIX"] = "Citrix",
            ["LOCAL"] = "Local",
            ["WINRM"] = "WINRM"
        };

    /// <summary>
    /// Fixed protocol types accepted by the strict profile-import boundary.
    /// Dynamic TOOL entries are runtime inventory types and are not imported as profiles.
    /// </summary>
    public static IReadOnlySet<string> CanonicalTypes { get; } =
        CanonicalMappings.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether a persisted type is known, including non-empty dynamic TOOL entries.
    /// </summary>
    public static bool IsKnown(string? connectionType)
    {
        return !string.IsNullOrWhiteSpace(connectionType)
            && (CanonicalMappings.ContainsKey(connectionType) || IsNamedTool(connectionType));
    }

    /// <summary>
    /// Canonicalizes a fixed protocol type while preserving unknown and TOOL values verbatim.
    /// </summary>
    public static string Canonicalize(string connectionType)
    {
        ArgumentNullException.ThrowIfNull(connectionType);
        return CanonicalMappings.TryGetValue(connectionType, out string? canonicalType)
            ? canonicalType
            : connectionType;
    }

    /// <summary>
    /// Returns whether a profile type requires a remote host value.
    /// </summary>
    public static bool RequiresRemoteServer(string? connectionType)
    {
        return !string.Equals(connectionType, "Local", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(connectionType, "Citrix", StringComparison.OrdinalIgnoreCase)
            && !IsNamedTool(connectionType);
    }

    /// <summary>
    /// Returns whether a connection type marks a tab that hosts a tool rather than a connection.
    /// </summary>
    /// <remarks>
    /// A bare prefix test, deliberately: this answers "is this tab a tool tab", and a value of
    /// exactly <see cref="ToolPrefix"/> names no tool but is certainly not a connection either.
    /// It is therefore WIDER than <see cref="IsNamedTool"/>, which additionally requires an
    /// identifier and answers the different question of whether the value is a usable type. The
    /// two are not interchangeable and swapping one for the other changes behaviour silently.
    /// </remarks>
    public static bool IsToolConnectionType(string? connectionType)
    {
        return connectionType is not null
            && connectionType.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the tool identifier behind a tool connection type, or the value unchanged when it
    /// carries no prefix.
    /// </summary>
    public static string StripToolPrefix(string connectionType)
    {
        ArgumentNullException.ThrowIfNull(connectionType);

        return IsToolConnectionType(connectionType)
            ? connectionType[ToolPrefix.Length..]
            : connectionType;
    }

    /// <summary>
    /// Whether the value names an actual tool, prefix AND identifier.
    /// </summary>
    /// <remarks>
    /// Stricter than <see cref="IsToolConnectionType"/> on purpose: this one decides whether a
    /// persisted type is usable, so a prefix with nothing behind it is not.
    /// </remarks>
    private static bool IsNamedTool(string? connectionType)
    {
        return IsToolConnectionType(connectionType)
            && connectionType!.Length > ToolPrefix.Length;
    }
}
