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

using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Input model for exporting a single OpenSSH <c>Host</c> configuration block.
/// </summary>
public sealed record SshConfigHostEntry(
    string Alias,
    string HostName,
    string? User = null,
    int Port = DefaultPorts.Ssh,
    string? IdentityFile = null,
    string? ProxyJump = null,
    bool ForwardAgent = false,
    bool Compression = false,
    bool ForwardX11 = false,
    int DynamicForwardPort = 0,
    int RemoteBindPort = 0,
    int RemoteLocalPort = 0,
    int ServerAliveInterval = 0,
    IReadOnlyList<string>? LocalForwards = null);

/// <summary>
/// Pure OpenSSH <c>~/.ssh/config</c> exporter for single hosts and full Heimdall inventories.
/// Never exports secrets (passwords or key passphrases).
/// </summary>
public static class SshConfigExporter
{
    private const string ConfigIndent = "    ";
    private const string LoopbackHost = "127.0.0.1";
    private const string SshConnectionType = "SSH";

    /// <summary>
    /// Exports a single OpenSSH <c>Host</c> block, omitting default values and neutralizing
    /// control characters to prevent directive injection.
    /// </summary>
    public static string ExportSingleHost(SshConfigHostEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var alias = SanitizeAlias(entry.Alias, entry.HostName);
        var hostName = SanitizeToken(entry.HostName);
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("Host ").AppendLine(alias);
        sb.Append(ConfigIndent).Append("HostName ").AppendLine(hostName);

        var user = SanitizeToken(entry.User);
        if (!string.IsNullOrWhiteSpace(user))
        {
            sb.Append(ConfigIndent).Append("User ").AppendLine(user);
        }

        if (entry.Port is > 0 and <= 65535 && entry.Port != DefaultPorts.Ssh)
        {
            sb.Append(ConfigIndent).Append("Port ").AppendLine(entry.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var identityFile = SanitizePathValue(entry.IdentityFile);
        if (!string.IsNullOrWhiteSpace(identityFile))
        {
            sb.Append(ConfigIndent).Append("IdentityFile ").AppendLine(FormatDirectiveValue(identityFile));
        }

        var proxyJump = SanitizeToken(entry.ProxyJump);
        if (!string.IsNullOrWhiteSpace(proxyJump))
        {
            sb.Append(ConfigIndent).Append("ProxyJump ").AppendLine(proxyJump);
        }

        if (entry.ForwardAgent)
        {
            sb.Append(ConfigIndent).AppendLine("ForwardAgent yes");
        }

        if (entry.Compression)
        {
            sb.Append(ConfigIndent).AppendLine("Compression yes");
        }

        if (entry.ForwardX11)
        {
            sb.Append(ConfigIndent).AppendLine("ForwardX11 yes");
        }

        if (entry.DynamicForwardPort is >= 1 and <= 65535)
        {
            sb.Append(ConfigIndent)
                .Append("DynamicForward ")
                .Append(LoopbackHost)
                .Append(':')
                .AppendLine(entry.DynamicForwardPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (entry.RemoteBindPort is >= 1 and <= 65535)
        {
            var localTargetPort = entry.RemoteLocalPort is >= 1 and <= 65535
                ? entry.RemoteLocalPort
                : entry.RemoteBindPort;
            sb.Append(ConfigIndent)
                .Append("RemoteForward ")
                .Append(entry.RemoteBindPort.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(LoopbackHost)
                .Append(':')
                .AppendLine(localTargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (entry.LocalForwards is { Count: > 0 })
        {
            foreach (var rawForward in entry.LocalForwards)
            {
                if (SshLocalForwardSpec.TryParse(rawForward, out var parsed) && parsed is not null)
                {
                    sb.Append(ConfigIndent)
                        .Append("LocalForward ")
                        .AppendLine(parsed.ToOpenSshDirectiveValue());
                }
            }
        }

        if (entry.ServerAliveInterval > 0)
        {
            sb.Append(ConfigIndent)
                .Append("ServerAliveInterval ")
                .AppendLine(entry.ServerAliveInterval.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Exports all SSH gateways and SSH server profiles from a Heimdall inventory as a single
    /// OpenSSH configuration document. Gateway parent chains and server gateway assignments
    /// are resolved to <c>ProxyJump</c> references.
    /// </summary>
    public static string ExportInventory(
        IEnumerable<ServerProfileDto> servers,
        IEnumerable<SshGatewayDto> gateways,
        int defaultServerAliveInterval = 60)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(gateways);

        var gatewayList = gateways
            .Where(g => !string.IsNullOrWhiteSpace(g.Host))
            .ToList();
        var sshServers = servers
            .Where(s => string.Equals(s.ConnectionType, SshConnectionType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(s.RemoteServer))
            .ToList();

        if (gatewayList.Count == 0 && sshServers.Count == 0)
        {
            return string.Empty;
        }

        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gatewayAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var gw in gatewayList)
        {
            var rawAlias = string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name;
            var alias = ReserveUniqueAlias(SanitizeAlias(rawAlias, gw.Host), usedAliases);
            if (!string.IsNullOrWhiteSpace(gw.Id))
            {
                gatewayAliases[gw.Id] = alias;
            }
        }

        var blocks = new List<string>(gatewayList.Count + sshServers.Count);

        foreach (var gw in gatewayList)
        {
            var alias = !string.IsNullOrWhiteSpace(gw.Id) && gatewayAliases.TryGetValue(gw.Id, out var mapped)
                ? mapped
                : SanitizeAlias(string.IsNullOrWhiteSpace(gw.Name) ? gw.Host : gw.Name, gw.Host);

            string? parentAlias = null;
            if (!string.IsNullOrWhiteSpace(gw.ParentGatewayId)
                && !string.Equals(gw.ParentGatewayId, gw.Id, StringComparison.OrdinalIgnoreCase)
                && gatewayAliases.TryGetValue(gw.ParentGatewayId, out var resolvedParent))
            {
                parentAlias = resolvedParent;
            }

            var block = ExportSingleHost(new SshConfigHostEntry(
                Alias: alias,
                HostName: gw.Host,
                User: gw.User,
                Port: gw.Port,
                IdentityFile: gw.KeyPath,
                ProxyJump: parentAlias,
                ServerAliveInterval: defaultServerAliveInterval));

            if (!string.IsNullOrWhiteSpace(block))
            {
                blocks.Add(block);
            }
        }

        foreach (var server in sshServers)
        {
            var rawAlias = string.IsNullOrWhiteSpace(server.DisplayName) ? server.RemoteServer : server.DisplayName;
            var alias = ReserveUniqueAlias(SanitizeAlias(rawAlias, server.RemoteServer), usedAliases);

            string? proxyJump = null;
            if (!server.UseDirectConnection
                && !string.IsNullOrWhiteSpace(server.SshGatewayId)
                && gatewayAliases.TryGetValue(server.SshGatewayId, out var resolvedGateway))
            {
                proxyJump = resolvedGateway;
            }

            var block = ExportSingleHost(new SshConfigHostEntry(
                Alias: alias,
                HostName: server.RemoteServer,
                User: server.SshUsername,
                Port: server.SshPort,
                IdentityFile: server.SshKeyPath,
                ProxyJump: proxyJump,
                ForwardAgent: server.SshAgentForwarding,
                Compression: server.SshCompression,
                ForwardX11: server.SshX11Forwarding,
                DynamicForwardPort: server.SocksProxyPort,
                RemoteBindPort: server.RemoteBindPort,
                RemoteLocalPort: server.RemoteLocalPort,
                ServerAliveInterval: defaultServerAliveInterval,
                LocalForwards: server.SshLocalForwards));

            if (!string.IsNullOrWhiteSpace(block))
            {
                blocks.Add(block);
            }
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string ReserveUniqueAlias(string baseAlias, HashSet<string> usedAliases)
    {
        if (usedAliases.Add(baseAlias))
        {
            return baseAlias;
        }

        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = $"{baseAlias}-{suffix}";
            if (usedAliases.Add(candidate))
            {
                return candidate;
            }
        }

        var fallback = $"{baseAlias}-{Guid.NewGuid():N}";
        usedAliases.Add(fallback);
        return fallback;
    }

    private static string SanitizeAlias(string? alias, string fallbackHost)
    {
        var source = string.IsNullOrWhiteSpace(alias) ? fallbackHost : alias;
        var sb = new StringBuilder(source.Length);
        var lastWasSeparator = false;

        foreach (var ch in source.Trim())
        {
            if (ch is '\r' or '\n')
            {
                break;
            }

            if (char.IsControl(ch) || ch is '*' or '?' or '!' or '"' or '\'' or '#')
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSeparator && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasSeparator = true;
                }

                continue;
            }

            sb.Append(ch);
            lastWasSeparator = false;
        }

        var cleaned = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "host" : cleaned;
    }

    private static string SanitizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch is '\r' or '\n')
            {
                break;
            }

            if (!char.IsControl(ch) && ch != '#' && !char.IsWhiteSpace(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string SanitizePathValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch is '\r' or '\n')
            {
                break;
            }

            if (!char.IsControl(ch) && ch != '"')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim();
    }

    private static string FormatDirectiveValue(string value)
    {
        return value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
    }
}
