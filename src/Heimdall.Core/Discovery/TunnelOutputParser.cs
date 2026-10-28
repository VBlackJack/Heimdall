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

using System.Globalization;

namespace Heimdall.Core.Discovery;

/// <summary>
/// Reads the text a cartography scan gets back from its SSH gateway: the open ports, the hosts
/// that answered a ping, the gateway's ARP table and its reverse-DNS lookups.
/// </summary>
/// <remarks>
/// The output is the gateway's, but what is in it was put there by the scanned network, and a
/// gateway that has been tampered with can say anything. Every reading is therefore checked
/// against what was asked: a port must be one that was probed, an address must be one of the
/// subnet being scanned. Without that a line such as <c>70000</c>, <c>-1</c> or an address of
/// another network became a result, and the progress counters counted it.
/// </remarks>
public static class TunnelOutputParser
{
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    /// <summary>The longest host name DNS allows.</summary>
    public const int MaxHostnameLength = 253;

    private const string PointerMarker = "domain name pointer";
    private const char ReverseDnsSeparator = '|';

    /// <summary>
    /// The probed ports reported open, one per line, in the order reported and without repeats.
    /// A line that is not a plain decimal port, or a port that was not probed, is ignored.
    /// </summary>
    public static List<int> ParseOpenPorts(string? output, IReadOnlySet<int> probedPorts)
    {
        var open = new List<int>();
        var seen = new HashSet<int>();
        foreach (var line in Lines(output))
        {
            if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port is >= MinPort and <= MaxPort
                && probedPorts.Contains(port)
                && seen.Add(port))
            {
                open.Add(port);
            }
        }

        return open;
    }

    /// <summary>The addresses of <paramref name="expectedIps"/> that the output lists, one per line.</summary>
    public static HashSet<string> ParseAliveHosts(string? output, IReadOnlySet<string> expectedIps)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(output))
        {
            var candidate = line.Trim();
            if (expectedIps.Contains(candidate))
            {
                alive.Add(candidate);
            }
        }

        return alive;
    }

    /// <summary>The addresses of <paramref name="subnetIps"/> found in the first column of an ARP table.</summary>
    public static HashSet<string> ParseArpHosts(string? output, IReadOnlySet<string> subnetIps)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(output))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && subnetIps.Contains(parts[0]))
            {
                discovered.Add(parts[0]);
            }
        }

        return discovered;
    }

    /// <summary>
    /// The host names of <c>address|host output</c> lines, for the addresses that were asked about.
    /// A name is cleaned of control characters, bounded, and refused if it holds whitespace.
    /// </summary>
    public static Dictionary<string, string> ParseReverseDns(string? output, IReadOnlySet<string> expectedIps)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf(ReverseDnsSeparator);
            if (separator <= 0)
            {
                continue;
            }

            var ip = line[..separator].Trim();
            var dnsLine = line[(separator + 1)..];
            var marker = dnsLine.IndexOf(PointerMarker, StringComparison.Ordinal);
            if (marker < 0 || !expectedIps.Contains(ip))
            {
                continue;
            }

            var afterMarker = dnsLine[(marker + PointerMarker.Length)..];
            var hostname = ReplyText.Clean(afterMarker)?.TrimEnd('.');
            if (string.IsNullOrEmpty(hostname)
                || hostname.Length > MaxHostnameLength
                || hostname.Any(char.IsWhiteSpace))
            {
                continue;
            }

            map[ip] = hostname;
        }

        return map;
    }

    private static string[] Lines(string? output) =>
        string.IsNullOrEmpty(output) ? [] : output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
