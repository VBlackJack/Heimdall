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

using System.Xml.Linq;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Parses mRemoteNG connection files (confCons.xml) into
/// <see cref="ServerProfileDto"/> instances.
/// Supports unencrypted files. Encrypted passwords are skipped
/// (user must re-enter credentials).
/// </summary>
public static class MRemoteNgImporter
{
    private static readonly Dictionary<string, string> ProtocolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RDP"] = "RDP",
        ["SSH1"] = "SSH",
        ["SSH2"] = "SSH",
        ["VNC"] = "VNC",
        ["Telnet"] = "Telnet",
        ["Rlogin"] = "Telnet",
        ["HTTP"] = "RDP",  // Fallback - no native HTTP in Heimdall
        ["HTTPS"] = "RDP",
        ["IntApp"] = "Local",
    };

    public static ImportResult Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var result = new ImportResult();

        try
        {
            var xmlSettings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(content), xmlSettings);
            var doc = XDocument.Load(reader);
            var root = doc.Root;
            if (root is null) return result;

            // Check for full file encryption
            var fullEncryption = root.Attribute("FullFileEncryption")?.Value;
            if (string.Equals(fullEncryption, "true", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add("File is fully encrypted. Decrypt the file in mRemoteNG first (File > Save As with no encryption).");
                return result;
            }

            ParseNodes(root, "", result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"XML parse error: {ex.Message}");
        }

        return result;
    }

    private static void ParseNodes(XElement parent, string groupPath, ImportResult result)
    {
        foreach (var node in parent.Elements().Where(e => e.Name.LocalName == "Node"))
        {
            var type = node.Attribute("Type")?.Value ?? "Connection";

            if (string.Equals(type, "Container", StringComparison.OrdinalIgnoreCase))
            {
                var containerName = node.Attribute("Name")?.Value?.Trim() ?? "";
                var newPath = string.IsNullOrWhiteSpace(groupPath)
                    ? containerName
                    : $"{groupPath}/{containerName}";
                ParseNodes(node, newPath, result);
            }
            else if (string.Equals(type, "Connection", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dto = ParseConnection(node, groupPath);
                    if (dto is not null)
                        result.Servers.Add(dto);
                }
                catch (Exception ex)
                {
                    var name = node.Attribute("Name")?.Value ?? "unknown";
                    result.Warnings.Add($"{name}: {ex.Message}");
                }
            }
        }
    }

    private static ServerProfileDto? ParseConnection(XElement node, string groupPath)
    {
        var name = node.Attribute("Name")?.Value?.Trim();
        var hostname = node.Attribute("Hostname")?.Value?.Trim();

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(hostname))
            return null;

        var protocol = node.Attribute("Protocol")?.Value?.Trim() ?? "RDP";
        if (!ProtocolMap.TryGetValue(protocol, out var connectionType))
            connectionType = "RDP";

        var portStr = node.Attribute("Port")?.Value;
        int.TryParse(portStr, out var port);

        var dto = new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = name ?? hostname!,
            Origin = Models.ProfileOrigin.ImportMRemoteNg,
            RemoteServer = hostname ?? name!,
            ConnectionType = connectionType,
            UseDirectConnection = true,
            Group = string.IsNullOrWhiteSpace(groupPath) ? null : groupPath,
        };

        // Port
        if (port > 0)
        {
            dto.RemotePort = port;
            ApplyPortByProtocol(dto, connectionType, port);
        }
        else
        {
            ApplyDefaultPort(dto, connectionType);
        }

        // Username / Domain
        var username = node.Attribute("Username")?.Value?.Trim();
        var domain = node.Attribute("Domain")?.Value?.Trim();

        if (!string.IsNullOrWhiteSpace(username))
        {
            var fullUser = !string.IsNullOrWhiteSpace(domain)
                ? $"{username}@{domain}"
                : username;

            switch (connectionType)
            {
                case "RDP":
                    dto.RdpUsername = fullUser;
                    break;
                case "SSH":
                case "SFTP":
                    dto.SshUsername = fullUser;
                    break;
                case "VNC":
                    break; // VNC doesn't use usernames
                case "Telnet":
                    dto.TelnetUsername = fullUser;
                    break;
            }
        }

        // Description -> Tags
        var description = node.Attribute("Description")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(description))
            dto.Tags = description;

        // RDP-specific settings
        if (connectionType == "RDP")
        {
            dto.RdpMode = "Embedded";

            // Every assignment below writes a per-profile RDP setting, which the connect-time
            // resolver only reads once the profile stops following the application defaults.
            // Same rule as the .rdp importer, so the two import paths cannot disagree.
            var carriesPerProfileSettings = false;

            var colors = node.Attribute("Colors")?.Value;
            if (colors is not null)
            {
                dto.RdpColorDepth = colors switch
                {
                    "Colors256" => 8,
                    "Colors15Bit" => 15,
                    "Colors16Bit" => 16,
                    "Colors24Bit" => 24,
                    "Colors32Bit" => 32,
                    _ => 32
                };
                carriesPerProfileSettings = true;
            }

            var resolution = node.Attribute("Resolution")?.Value;
            if (string.Equals(resolution, "SmartSize", StringComparison.OrdinalIgnoreCase))
            {
                dto.RdpDynamicResolution = true;
                carriesPerProfileSettings = true;
            }

            // Non-conditional or: every redirection still has to be mapped.
            carriesPerProfileSettings |= ParseBool(node, "RedirectClipboard", v => dto.RdpRedirectClipboard = v);
            carriesPerProfileSettings |= ParseBool(node, "RedirectDiskDrives", v => dto.RdpRedirectDrives = v);
            carriesPerProfileSettings |= ParseBool(node, "RedirectPrinters", v => dto.RdpRedirectPrinters = v);
            carriesPerProfileSettings |= ParseBool(node, "RedirectSmartCards", v => dto.RdpRedirectSmartCards = v);
            carriesPerProfileSettings |= ParseBool(node, "RedirectAudioCapture", v => dto.RdpAudioCapture = v);

            // The gateway is read by the resolver whichever branch it takes, so an entry that
            // only names one is not carrying a per-profile setting.
            var rdpGw = node.Attribute("RDGatewayHostname")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(rdpGw))
                dto.RdpGateway = rdpGw;

            if (carriesPerProfileSettings)
            {
                // Without this the resolver answers from the application defaults and every value
                // mapped above is inert. A bare entry keeps following the defaults.
                dto.RdpUseGlobalDefaults = false;
            }
        }

        // SSH-specific settings
        if (connectionType == "SSH")
        {
            dto.SshMode = "Embedded";
        }

        return dto;
    }

    private static void ApplyPortByProtocol(ServerProfileDto dto, string type, int port)
    {
        switch (type)
        {
            case "SSH": dto.SshPort = port; break;
            case "VNC": dto.VncPort = port; break;
            case "Telnet": dto.TelnetPort = port; break;
            case "FTP": dto.FtpPort = port; break;
        }
    }

    private static void ApplyDefaultPort(ServerProfileDto dto, string type)
    {
        switch (type)
        {
            case "RDP": dto.RemotePort = Models.DefaultPorts.Rdp; break;
            case "SSH": dto.SshPort = Models.DefaultPorts.Ssh; dto.RemotePort = Models.DefaultPorts.Ssh; break;
            case "VNC": dto.VncPort = Models.DefaultPorts.Vnc; dto.RemotePort = Models.DefaultPorts.Vnc; break;
            case "Telnet": dto.TelnetPort = Models.DefaultPorts.Telnet; dto.RemotePort = Models.DefaultPorts.Telnet; break;
            case "FTP": dto.FtpPort = Models.DefaultPorts.Ftp; dto.RemotePort = Models.DefaultPorts.Ftp; break;
        }
    }

    /// <summary>
    /// Applies <paramref name="attr"/> when the node carries it, and reports whether it did, so the
    /// caller can tell a mapped setting from an absent one.
    /// </summary>
    private static bool ParseBool(XElement node, string attr, Action<bool> setter)
    {
        var val = node.Attribute(attr)?.Value;
        if (val is null)
            return false;

        setter(string.Equals(val, "True", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    public sealed class ImportResult
    {
        public List<ServerProfileDto> Servers { get; } = [];
        public List<string> Warnings { get; } = [];
    }
}
