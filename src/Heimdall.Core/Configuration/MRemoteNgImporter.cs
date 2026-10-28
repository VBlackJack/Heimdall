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
    private const string DefaultProtocol = "RDP";
    private const string UnknownNodeName = "unknown";

    private static readonly Dictionary<string, string> ProtocolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RDP"] = "RDP",
        ["SSH1"] = "SSH",
        ["SSH2"] = "SSH",
        ["VNC"] = "VNC",
        ["Telnet"] = "Telnet",
        ["Rlogin"] = "Telnet",
        ["HTTP"] = "WEB",
        ["HTTPS"] = "WEB",
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
                result.Warnings.Add(new ImportWarning(ImportWarningKind.FullFileEncryption));
                return result;
            }

            ParseNodes(root, "", result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add(new ImportWarning(ImportWarningKind.ParseError, Detail: ex.Message));
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
                    var dto = ParseConnection(node, groupPath, result);
                    if (dto is not null)
                        result.Servers.Add(dto);
                }
                catch (Exception ex)
                {
                    var name = node.Attribute("Name")?.Value ?? UnknownNodeName;
                    result.Warnings.Add(new ImportWarning(ImportWarningKind.NodeError, name, ex.Message));
                }
            }
        }
    }

    private static ServerProfileDto? ParseConnection(XElement node, string groupPath, ImportResult result)
    {
        var name = node.Attribute("Name")?.Value?.Trim();
        var hostname = node.Attribute("Hostname")?.Value?.Trim();

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(hostname))
            return null;

        // The protocol is resolved first: port and credentials only mean something once the
        // connection type is known. An entry that inherits it takes the value of the nearest
        // ancestor that does not, and nothing (so the default) when no ancestor sets one.
        var protocol = ResolveInherited(node, "Protocol", "InheritProtocol")?.Trim();
        if (string.IsNullOrEmpty(protocol))
            protocol = DefaultProtocol;

        if (!ProtocolMap.TryGetValue(protocol, out var connectionType))
        {
            // A protocol Heimdall has no profile type for used to import as RDP, which could only
            // fail to connect; the entry is left out and the user is told.
            result.Warnings.Add(new ImportWarning(
                ImportWarningKind.UnknownProtocol,
                name ?? hostname ?? UnknownNodeName,
                protocol));
            return null;
        }

        var portStr = ResolveInherited(node, "Port", "InheritPort");
        int.TryParse(portStr, out var port);

        var dto = new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = name ?? hostname!,
            Origin = Models.ProfileOrigin.ImportMRemoteNg,
            RemoteServer = hostname ?? name!,
            ConnectionType = connectionType,
            WebUseHttp = string.Equals(protocol, "HTTP", StringComparison.OrdinalIgnoreCase),
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
        var username = ResolveInherited(node, "Username", "InheritUsername")?.Trim();
        var domain = ResolveInherited(node, "Domain", "InheritDomain")?.Trim();

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
                case "WEB":
                    dto.WebUsername = fullUser;
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
                // 256 colours and 15-bit are below the depth the profile may carry; they land on
                // the 16-bit floor, which is what the connect-time resolver made of them anyway.
                dto.RdpColorDepth = Heimdall.Core.Rdp.RdpDisplayLimits.NormalizeColorDepth(colors switch
                {
                    "Colors256" => 8,
                    "Colors15Bit" => 15,
                    "Colors16Bit" => 16,
                    "Colors24Bit" => 24,
                    "Colors32Bit" => 32,
                    _ => 32
                });
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

    /// <summary>
    /// The value of <paramref name="valueAttribute"/> as mRemoteNG applies it: when the node's
    /// <paramref name="inheritAttribute"/> is true its own value is stale and ignored, and the
    /// value comes from the nearest ancestor container that does not inherit it in turn.
    /// Returns <see langword="null"/> when every node up to the top inherits.
    /// </summary>
    private static string? ResolveInherited(XElement node, string valueAttribute, string inheritAttribute)
    {
        for (XElement? current = node; current is { } && IsNode(current); current = current.Parent)
        {
            var inherit = current.Attribute(inheritAttribute)?.Value;
            if (!string.Equals(inherit, "true", StringComparison.OrdinalIgnoreCase))
                return current.Attribute(valueAttribute)?.Value;
        }

        return null;
    }

    private static bool IsNode(XElement element) => element.Name.LocalName == "Node";

    private static void ApplyPortByProtocol(ServerProfileDto dto, string type, int port)
    {
        switch (type)
        {
            case "SSH": dto.SshPort = port; break;
            case "VNC": dto.VncPort = port; break;
            case "Telnet": dto.TelnetPort = port; break;
            case "FTP": dto.FtpPort = port; break;
            case "WEB": dto.WebPort = port; break;
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
            case "WEB": dto.WebPort = WebAddress.DefaultPortFor(dto.WebUseHttp); break;
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
        public List<ImportWarning> Warnings { get; } = [];
    }

    /// <summary>What the parser could not take as it is.</summary>
    public enum ImportWarningKind
    {
        /// <summary>The file is encrypted as a whole and nothing was read.</summary>
        FullFileEncryption,

        /// <summary>The file is not readable XML.</summary>
        ParseError,

        /// <summary>One connection node threw while it was read.</summary>
        NodeError,

        /// <summary>A connection uses a protocol Heimdall has no profile type for; it was skipped.</summary>
        UnknownProtocol,
    }

    /// <summary>
    /// One thing the parser left out or could not read. The text is built by the caller, so the
    /// parser carries data and never a user-facing sentence.
    /// </summary>
    /// <param name="Kind">What happened.</param>
    /// <param name="NodeName">The connection concerned, empty when the warning is about the file.</param>
    /// <param name="Detail">The protocol or the exception message, empty when there is none.</param>
    public sealed record ImportWarning(ImportWarningKind Kind, string NodeName = "", string Detail = "");
}
