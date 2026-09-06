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

namespace Heimdall.Core.Network;

/// <summary>
/// UI-friendly row model for a single batch DNS resolution result.
/// </summary>
public sealed record DnsBatchResolveResult(
    string Hostname,
    string Ipv4,
    string Ipv6,
    int ResolveTimeMs,
    string Status,
    bool Success)
{
    public const string Placeholder = "-";

    public static DnsBatchResolveResult Ok(string hostname, IEnumerable<IPAddress>? addresses, int resolveTimeMs)
    {
        var addressList = addresses?.ToArray() ?? [];
        var ipv4 = addressList
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .FirstOrDefault() ?? Placeholder;

        var ipv6 = addressList
            .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            .Select(address => address.ToString())
            .FirstOrDefault() ?? Placeholder;

        return new DnsBatchResolveResult(hostname, ipv4, ipv6, resolveTimeMs, "OK", true);
    }

    public static DnsBatchResolveResult Failed(string hostname, int resolveTimeMs, string? status)
        => new(hostname, Placeholder, Placeholder, resolveTimeMs, status ?? string.Empty, false);
}
