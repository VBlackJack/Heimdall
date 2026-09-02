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

using System.IO;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Parses MobaXterm session files (.mxtsessions or MobaXterm.ini [Bookmarks] sections)
/// and converts them into <see cref="ServerProfileDto"/> instances.
/// </summary>
/// <remarks>
/// MobaXterm session format (INI):
/// <code>
/// [Bookmarks]
/// SubRep=FolderName
/// ImgNum=42
/// sessionName= #protocolCode#flags%host%port%user%...
/// [Bookmarks_1]
/// SubRep=SubFolder
/// ...
/// </code>
/// Password fields are encrypted with MobaXterm's proprietary algorithm and cannot be
/// imported; users must re-enter credentials after import.
/// </remarks>
public static partial class MobaXtermImporter
{
    private const string PasswordsSection = "Passwords";
    private const string CredentialsSection = "Credentials";

    // MobaXterm protocol/icon codes mapped to Heimdall ConnectionType strings.
    // Multiple codes per protocol to handle version differences.
    private static readonly Dictionary<int, string> ProtocolMap = new()
    {
        [109] = "SSH",
        [91] = "RDP",
        [140] = "SFTP",
        [130] = "FTP",
        [128] = "VNC",
        [98] = "Telnet",
    };

    // Default ports per protocol - delegates to shared constants.
    private static int GetDefaultPort(string protocol) => protocol switch
    {
        "SSH" => Models.DefaultPorts.Ssh,
        "RDP" => Models.DefaultPorts.Rdp,
        "SFTP" => Models.DefaultPorts.Sftp,
        "FTP" => Models.DefaultPorts.Ftp,
        "VNC" => Models.DefaultPorts.Vnc,
        "Telnet" => Models.DefaultPorts.Telnet,
        _ => 0
    };

    /// <summary>
    /// Parse a MobaXterm session file and return the imported sessions with any warnings.
    /// </summary>
    /// <param name="content">Full text content of the .mxtsessions or MobaXterm.ini file.</param>
    /// <returns>Import result containing parsed servers, folder structure, and warnings.</returns>
    public static MobaXtermImportResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var result = new MobaXtermImportResult();
        var sections = ParseIniSections(content);
        result.StoredCredentialCount = CountStoredCredentialEntries(sections);

        foreach (var section in sections)
        {
            if (!IsBookmarkSection(section.Key))
            {
                continue;
            }

            var folderName = section.Value
                .GetValueOrDefault("SubRep", string.Empty)
                .Trim();

            foreach (var kvp in section.Value)
            {
                if (IsMetaKey(kvp.Key))
                {
                    continue;
                }

                try
                {
                    var server = ParseSession(kvp.Key, kvp.Value, folderName);
                    if (server is not null)
                    {
                        result.Servers.Add(server);
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{kvp.Key}: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static int CountStoredCredentialEntries(Dictionary<string, Dictionary<string, string>> sections)
    {
        // These sections contain proprietary-encrypted secrets; count them only for user warning.
        int count = 0;
        if (sections.TryGetValue(PasswordsSection, out Dictionary<string, string>? passwords))
        {
            count += passwords.Count;
        }

        if (sections.TryGetValue(CredentialsSection, out Dictionary<string, string>? credentials))
        {
            count += credentials.Count;
        }

        return count;
    }

    /// <summary>
    /// Parse a single MobaXterm session line into a <see cref="ServerProfileDto"/>.
    /// </summary>
    /// <param name="sessionName">The bookmark key (session display name).</param>
    /// <param name="rawValue">The raw INI value (protocol-encoded fields separated by %).</param>
    /// <param name="folder">Parent folder name from SubRep, used as Group.</param>
    /// <returns>A populated DTO, or null if the session type is unsupported.</returns>
    internal static ServerProfileDto? ParseSession(string sessionName, string rawValue, string folder)
    {
        // Format: " #protocolCode#flags%host%port%user%...rest"
        // Some lines start with a space before the '#'
        var trimmed = rawValue.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains('#'))
        {
            return null;
        }

        var protocolCode = ExtractProtocolCode(trimmed);
        if (protocolCode < 0 || !ProtocolMap.TryGetValue(protocolCode, out var connectionType))
        {
            return null; // Unsupported protocol
        }

        // Strip the "#code#flags" prefix to get the %-delimited fields.
        var fieldsPart = ExtractFieldsPart(trimmed);
        var fields = fieldsPart.Split('%');

        var host = fields.Length > 0 ? fields[0] : string.Empty;
        var portStr = fields.Length > 1 ? fields[1] : string.Empty;
        var user = fields.Length > 2 ? fields[2] : string.Empty;

        if (string.IsNullOrWhiteSpace(host))
        {
            return null; // No host, skip
        }

        var port = ParsePort(portStr, GetDefaultPort(connectionType));

        var sanitizedGroup = SanitizeGroupName(folder);

        var dto = new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = sessionName.Trim(),
            Origin = Models.ProfileOrigin.ImportMobaXterm,
            RemoteServer = host,
            ConnectionType = connectionType,
            UseDirectConnection = true,
            Group = string.IsNullOrWhiteSpace(sanitizedGroup) ? null : sanitizedGroup,
        };

        ApplyProtocolSettings(dto, connectionType, port, user, fields);

        return dto;
    }

    private static void ApplyProtocolSettings(
        ServerProfileDto dto, string connectionType, int port, string user, string[] fields)
    {
        switch (connectionType)
        {
            case "SSH":
                dto.SshPort = port > 0 ? port : GetDefaultPort("SSH");
                dto.SshUsername = NullIfEmpty(user);
                dto.RemotePort = dto.SshPort;
                dto.SshMode = "Embedded";
                ApplySshExtras(dto, fields);
                break;

            case "RDP":
                dto.RemotePort = port > 0 ? port : GetDefaultPort("RDP");
                dto.RdpUsername = NullIfEmpty(user);
                dto.RdpMode = "Embedded";
                break;

            case "SFTP":
                dto.SshPort = port > 0 ? port : GetDefaultPort("SFTP");
                dto.SshUsername = NullIfEmpty(user);
                dto.RemotePort = dto.SshPort;
                break;

            case "FTP":
                dto.FtpPort = port > 0 ? port : GetDefaultPort("FTP");
                dto.FtpUsername = NullIfEmpty(user);
                dto.RemotePort = dto.FtpPort;
                ApplyFtpExtras(dto, fields);
                break;

            case "VNC":
                dto.VncPort = port > 0 ? port : GetDefaultPort("VNC");
                dto.RemotePort = dto.VncPort;
                break;

            case "Telnet":
                dto.TelnetPort = port > 0 ? port : GetDefaultPort("Telnet");
                dto.TelnetUsername = NullIfEmpty(user);
                dto.RemotePort = dto.TelnetPort;
                break;
        }
    }

    private static void ApplySshExtras(ServerProfileDto dto, string[] fields)
    {
        // MobaXterm SSH field layout (approximate, varies by version):
        // [0]=host, [1]=port, [2]=user, [3]=password(encrypted), [4]=privateKeyPath,
        // [5-6]=misc, [7]=port(again), [8-9]=misc, [10]=X11, [11]=compression,
        // [12]=agentForwarding ...
        if (fields.Length > 4)
        {
            var keyPath = fields[4];
            if (!string.IsNullOrWhiteSpace(keyPath) && keyPath != "-1")
            {
                dto.SshKeyPath = SanitizeFilePath(keyPath);
            }
        }

        if (fields.Length > 10)
        {
            dto.SshX11Forwarding = fields[10] == "1";
        }

        if (fields.Length > 11)
        {
            dto.SshCompression = fields[11] == "1";
        }

        if (fields.Length > 12)
        {
            dto.SshAgentForwarding = fields[12] == "1";
        }
    }

    private static void ApplyFtpExtras(ServerProfileDto dto, string[] fields)
    {
        // MobaXterm FTP: field[3] often indicates passive mode, field[4] TLS/SSL
        if (fields.Length > 3)
        {
            dto.FtpPassiveMode = fields[3] != "0";
        }

        if (fields.Length > 4)
        {
            dto.FtpUseSsl = fields[4] == "1";
        }
    }

    /// <summary>
    /// Extract the numeric protocol code from a MobaXterm session value.
    /// Expected format: "#CODE#..." where CODE is an integer.
    /// </summary>
    internal static int ExtractProtocolCode(string value)
    {
        var match = ProtocolCodeRegex().Match(value);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var code))
        {
            return code;
        }

        return -1;
    }

    /// <summary>
    /// Extract the %-delimited field portion after the "#code#flags" prefix.
    /// </summary>
    internal static string ExtractFieldsPart(string value)
    {
        // Find the second '#' and take everything after the next '%'
        var firstHash = value.IndexOf('#');
        if (firstHash < 0) return string.Empty;

        var secondHash = value.IndexOf('#', firstHash + 1);
        if (secondHash < 0) return string.Empty;

        // After the second '#' there may be flags before the first '%'
        var firstPercent = value.IndexOf('%', secondHash);
        if (firstPercent < 0) return string.Empty;

        return value[(firstPercent + 1)..];
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIniSections(string content)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.Contains(']'))
            {
                var sectionName = line[1..line.IndexOf(']')];
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[sectionName] = current;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = line[..eqIndex].Trim();
                var val = line[(eqIndex + 1)..];
                current[key] = val;
            }
        }

        return sections;
    }

    private static bool IsBookmarkSection(string sectionName) =>
        sectionName.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase);

    private static bool IsMetaKey(string key) =>
        key.Equals("SubRep", StringComparison.OrdinalIgnoreCase)
        || key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase);

    private static int ParsePort(string value, int defaultPort) =>
        int.TryParse(value, out var port) && port > 0 ? port : defaultPort;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Sanitize a MobaXterm SubRep folder path for use as a Heimdall group name.
    /// MobaXterm uses backslash-separated hierarchical folders (e.g. "ADSEC\Gateways").
    /// Heimdall uses forward-slash hierarchy (e.g. "ADSEC/Gateways").
    /// Strips parent-directory traversal sequences while preserving the hierarchy.
    /// </summary>
    internal static string SanitizeGroupName(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        // Convert MobaXterm backslash hierarchy to Heimdall forward-slash hierarchy
        var sanitized = folder.Replace('\\', '/');

        // Strip traversal sequences
        sanitized = sanitized.Replace("../", string.Empty);
        sanitized = sanitized.Replace("./", string.Empty);

        // Remove leading/trailing slashes and collapse double slashes
        while (sanitized.Contains("//"))
        {
            sanitized = sanitized.Replace("//", "/");
        }

        sanitized = sanitized.Trim('/').Trim();

        // Strip characters illegal in file names from each segment
        var invalidChars = Path.GetInvalidFileNameChars()
            .Where(c => c != '/' && c != '\\')
            .ToArray();

        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c.ToString(), string.Empty);
        }

        return sanitized;
    }

    /// <summary>
    /// Sanitize a file path imported from MobaXterm (e.g. SSH key path).
    /// Returns null if the path contains shell metacharacters or traversal sequences.
    /// </summary>
    internal static string? SanitizeFilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Reject paths with shell metacharacters
        if (path.IndexOfAny([';', '|', '&', '`', '$', '>', '<', '!']) >= 0)
        {
            return null;
        }

        // Reject paths with parent-directory traversal
        if (path.Contains(".."))
        {
            return null;
        }

        return path.Trim();
    }

    [GeneratedRegex(@"#(\d+)#")]
    private static partial Regex ProtocolCodeRegex();
}

/// <summary>
/// Result of a MobaXterm session file import operation.
/// </summary>
public sealed class MobaXtermImportResult
{
    /// <summary>Successfully parsed server profiles.</summary>
    public List<ServerProfileDto> Servers { get; } = [];

    /// <summary>Non-fatal warnings for sessions that could not be parsed.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>
    /// Number of entries found in MobaXterm [Passwords]/[Credentials] sections,
    /// exposed only to warn the user; values are never decrypted.
    /// </summary>
    public int StoredCredentialCount { get; internal set; }
}
