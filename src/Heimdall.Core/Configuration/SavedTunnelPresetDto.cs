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
using System.Text.Json.Serialization;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Persisted favorite / preset for launching a manual SSH tunnel (Local -L, Dynamic SOCKS5 -D,
/// or Remote -R) in one click from the Tunnel Manager.
/// </summary>
public sealed class SavedTunnelPresetDto
{
    public const string ModeLocal = "Local";
    public const string ModeDynamicSocks5 = "DynamicSocks5";
    public const string ModeRemote = "Remote";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string GatewayId { get; set; } = string.Empty;

    /// <summary>Forwarding mode: <c>Local</c>, <c>DynamicSocks5</c>, or <c>Remote</c>.</summary>
    public string Mode { get; set; } = ModeLocal;

    public int LocalPort { get; set; }

    public string RemoteHost { get; set; } = string.Empty;

    public int RemotePort { get; set; }

    public int RemoteBindPort { get; set; }

    public int RemoteLocalPort { get; set; }

    [JsonIgnore]
    public string DisplaySummary => Mode switch
    {
        ModeDynamicSocks5 => string.Create(CultureInfo.InvariantCulture, $"-D 127.0.0.1:{LocalPort}"),
        ModeRemote => string.Create(
            CultureInfo.InvariantCulture,
            $"-R :{RemoteBindPort} -> 127.0.0.1:{(RemoteLocalPort > 0 ? RemoteLocalPort : LocalPort)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"-L :{LocalPort} -> {RemoteHost}:{RemotePort}")
    };

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Name)
        ? DisplaySummary
        : $"{Name} ({DisplaySummary})";
}
