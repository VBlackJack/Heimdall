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
using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Parses Microsoft Remote Desktop Connection Manager (.rdg) files into
/// <see cref="ServerProfileDto"/> instances. Supports schema v1 (RDCMan 2.2),
/// where a file, group or server carries its name as a direct child, and v3 (RDCMan 2.7+),
/// where the name and display name sit under a <c>properties</c> child.
/// </summary>
public static class RdcManImporter
{
    private const string UnknownServerName = "unknown";

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

            // RDCMan files have <file> or <RDCMan> as root
            var fileNodes = root.Name.LocalName == "file"
                ? [root]
                : root.Descendants().Where(e => e.Name.LocalName == "file").ToArray();

            foreach (var fileNode in fileNodes)
            {
                ParseGroup(fileNode, "", result);
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"XML parse error: {ex.Message}");
        }

        return result;
    }

    private static void ParseGroup(XElement element, string groupPath, ImportResult result)
    {
        var groupName = Property(element, "name");
        var currentPath = string.IsNullOrWhiteSpace(groupPath)
            ? (groupName ?? "")
            : (string.IsNullOrWhiteSpace(groupName) ? groupPath : $"{groupPath}/{groupName}");

        // Parse servers in this group
        foreach (var server in element.Elements().Where(e => e.Name.LocalName == "server"))
        {
            try
            {
                var dto = ParseServer(server, currentPath, element);
                if (dto is not null)
                    result.Servers.Add(dto);
                else
                    result.SkippedServers.Add(new SkippedServer(currentPath));
            }
            catch (Exception ex)
            {
                var name = Property(server, "name") ?? Property(server, "displayName") ?? UnknownServerName;
                result.Warnings.Add($"{name}: {ex.Message}");
            }
        }

        // Recurse into sub-groups
        foreach (var group in element.Elements().Where(e => e.Name.LocalName == "group"))
        {
            ParseGroup(group, currentPath, result);
        }
    }

    private static ServerProfileDto? ParseServer(XElement server, string groupPath, XElement parentGroup)
    {
        var name = Property(server, "displayName") ?? Property(server, "name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        var host = Property(server, "name") ?? name;

        var dto = new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = name,
            Origin = ProfileOrigin.ImportRdcMan,
            RemoteServer = host,
            RemotePort = DefaultPorts.Rdp,
            ConnectionType = "RDP",
            RdpMode = "Embedded",
            UseDirectConnection = true,
            Group = string.IsNullOrWhiteSpace(groupPath) ? null : groupPath,
        };

        // Try to get credentials (server > group > file level)
        var creds = FindElement(server, "logonCredentials")
                    ?? FindElement(parentGroup, "logonCredentials");

        if (creds is not null)
        {
            var user = El(creds, "userName");
            var domain = El(creds, "domain");

            if (!string.IsNullOrWhiteSpace(user))
            {
                dto.RdpUsername = !string.IsNullOrWhiteSpace(domain)
                    ? $"{user}@{domain}"
                    : user;
            }
        }

        // Connection settings
        var connSettings = FindElement(server, "connectionSettings")
                           ?? FindElement(parentGroup, "connectionSettings");

        if (connSettings is not null)
        {
            if (int.TryParse(El(connSettings, "port"), out var port) && port > 0)
                dto.RemotePort = port;
        }

        // Gateway settings
        var gwSettings = FindElement(server, "gatewaySettings");
        if (gwSettings is not null)
        {
            var gwName = El(gwSettings, "hostName");
            if (!string.IsNullOrWhiteSpace(gwName))
                dto.RdpGateway = gwName;
        }

        return dto;
    }

    private static XElement? FindElement(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? El(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    /// <summary>
    /// A name-like value of a file, group or server: read from its <c>properties</c> child first
    /// (schema v3), then from a direct child (schema v1). A blank value counts as absent so the
    /// other layout still gets its say.
    /// </summary>
    private static string? Property(XElement element, string localName)
    {
        var properties = FindElement(element, "properties");
        var fromProperties = properties is null ? null : El(properties, localName);
        if (!string.IsNullOrWhiteSpace(fromProperties))
            return fromProperties;

        var direct = El(element, localName);
        return string.IsNullOrWhiteSpace(direct) ? null : direct;
    }

    public sealed class ImportResult
    {
        public List<ServerProfileDto> Servers { get; } = [];
        public List<string> Warnings { get; } = [];

        /// <summary>
        /// Servers left out because they carry neither a name nor a display name, so there is
        /// nothing to connect to. Reported structurally, not as text, so the caller words the
        /// warning in the user's language.
        /// </summary>
        public List<SkippedServer> SkippedServers { get; } = [];
    }

    /// <summary>A server that was not imported for lack of a name; <paramref name="GroupPath"/> is empty at the root.</summary>
    public sealed record SkippedServer(string GroupPath);
}
