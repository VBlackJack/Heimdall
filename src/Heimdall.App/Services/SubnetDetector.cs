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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

/// <summary>
/// Shared subnet detection utilities for network tools. Detects the local
/// machine's IPv4 subnet or probes remote subnets via SSH gateway.
/// </summary>
internal static class SubnetDetector
{
    private static readonly Regex s_linuxInetRegex = new(
        @"inet\s+(\d+\.\d+\.\d+\.\d+/\d+)",
        RegexOptions.Compiled);

    private static readonly Regex s_ifconfigInetRegex = new(
        @"inet\s+(?:addr:)?(\d+\.\d+\.\d+\.\d+)\s+.*?(?:netmask|Mask:?)\s*(\d+\.\d+\.\d+\.\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Detects the local machine's primary IPv4 subnet by enumerating network interfaces.
    /// Prefers interfaces with a default gateway. Skips loopback and APIPA addresses.
    /// </summary>
    public static string? DetectLocalSubnet()
    {
        try
        {
            string? fallback = null;

            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                var props = iface.GetIPProperties();
                var hasGateway = false;
                foreach (var gw in props.GatewayAddresses)
                {
                    if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !gw.Address.Equals(System.Net.IPAddress.Any))
                    {
                        hasGateway = true;
                        break;
                    }
                }

                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var ip = uni.Address.ToString();
                    if (ip.StartsWith("127.", StringComparison.Ordinal)) continue;
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;

                    var cidr = NormalizeCidrFromIpAndPrefix(ip, uni.PrefixLength);
                    if (hasGateway) return cidr;
                    fallback ??= cidr;
                }
            }

            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Connects to a gateway via SSH and discovers its network interfaces using
    /// a fallback command chain: <c>ip -4 addr show</c> → <c>ifconfig</c> → <c>ipconfig</c>.
    /// </summary>
    public static async Task<List<string>> DetectRemoteSubnetsAsync(
        SshGatewayDto gateway,
        CancellationToken ct = default)
    {
        using var sshClient = await ToolGatewayConnector.ConnectAsync(gateway, ct).ConfigureAwait(false);

        var output = ExecuteSshCommand(sshClient, "ip -4 addr show 2>/dev/null");
        var subnets = ParseLinuxInterfaces(output);

        if (subnets.Count == 0)
        {
            output = ExecuteSshCommand(sshClient, "ifconfig 2>/dev/null");
            subnets = ParseIfconfigInterfaces(output);
        }

        if (subnets.Count == 0)
        {
            output = ExecuteSshCommand(sshClient, "ipconfig 2>nul");
            subnets = ParseWindowsIpconfig(output);
        }

        return subnets;
    }

    public static string NormalizeCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix)) return cidr;
        return NormalizeCidrFromIpAndPrefix(parts[0], prefix);
    }

    public static string NormalizeCidrFromIpAndPrefix(string ip, int prefix)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var addr)) return $"{ip}/{prefix}";
        var bytes = addr.GetAddressBytes();
        var maskBits = prefix;
        for (int i = 0; i < 4; i++)
        {
            if (maskBits >= 8) { maskBits -= 8; continue; }
            bytes[i] = (byte)(bytes[i] & (0xFF << (8 - maskBits)));
            maskBits = 0;
        }
        return $"{new System.Net.IPAddress(bytes)}/{prefix}";
    }

    public static int MaskToPrefix(string mask)
    {
        if (!System.Net.IPAddress.TryParse(mask, out var addr)) return 0;
        var bits = BitConverter.ToUInt32(addr.GetAddressBytes().Reverse().ToArray(), 0);
        int count = 0;
        while ((bits & 0x80000000) != 0) { count++; bits <<= 1; }
        return count;
    }

    // ── Private helpers ─────────────────────────────────────────────

    private static string ExecuteSshCommand(Renci.SshNet.SshClient client, string command)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(5);
        return cmd.Execute();
    }

    private static List<string> ParseLinuxInterfaces(string output)
    {
        var subnets = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return subnets;

        foreach (Match match in s_linuxInetRegex.Matches(output))
        {
            var cidr = match.Groups[1].Value;
            if (cidr.StartsWith("127.", StringComparison.Ordinal)) continue;
            subnets.Add(NormalizeCidr(cidr));
        }
        return subnets.Distinct().ToList();
    }

    private static List<string> ParseIfconfigInterfaces(string output)
    {
        var subnets = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return subnets;

        foreach (Match match in s_ifconfigInetRegex.Matches(output))
        {
            var ip = match.Groups[1].Value;
            var mask = match.Groups[2].Value;
            if (ip.StartsWith("127.", StringComparison.Ordinal)) continue;
            var prefix = MaskToPrefix(mask);
            if (prefix > 0)
                subnets.Add(NormalizeCidrFromIpAndPrefix(ip, prefix));
        }
        return subnets.Distinct().ToList();
    }

    private static List<string> ParseWindowsIpconfig(string output)
    {
        var subnets = new List<string>();
        if (string.IsNullOrWhiteSpace(output)) return subnets;

        string? lastIp = null;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("IPv4", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
            {
                lastIp = trimmed[(trimmed.LastIndexOf(':') + 1)..].Trim();
            }
            else if (lastIp is not null && trimmed.Contains("Mask", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':'))
            {
                var mask = trimmed[(trimmed.LastIndexOf(':') + 1)..].Trim();
                if (!lastIp.StartsWith("127.", StringComparison.Ordinal))
                {
                    var prefix = MaskToPrefix(mask);
                    if (prefix > 0)
                        subnets.Add(NormalizeCidrFromIpAndPrefix(lastIp, prefix));
                }
                lastIp = null;
            }
        }
        return subnets.Distinct().ToList();
    }
}
