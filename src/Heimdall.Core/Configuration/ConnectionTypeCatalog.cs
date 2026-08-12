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
    private const string ToolPrefix = "TOOL:";

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
            && (CanonicalMappings.ContainsKey(connectionType) || IsTool(connectionType));
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
            && !IsTool(connectionType);
    }

    private static bool IsTool(string? connectionType)
    {
        return connectionType is not null
            && connectionType.StartsWith(ToolPrefix, StringComparison.OrdinalIgnoreCase)
            && connectionType.Length > ToolPrefix.Length;
    }
}
