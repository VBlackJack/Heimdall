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
/// Builds the settings a connection to a shared profile runs with.
/// </summary>
/// <remarks>
/// The connection resolves its gateway chain from the settings it is handed
/// (<c>TunnelService</c> reads <see cref="AppSettings.SshGateways"/>), and a shared gateway lives
/// only in memory. So the connection gets a copy holding the shared gateways as well; the
/// canonical settings instance is never touched, and the copy is never written: the configuration
/// manager refuses a shared identifier arriving in the gateway list.
/// </remarks>
public static class SharedSessionSettings
{
    /// <summary>
    /// A deep copy of <paramref name="canonical"/> with <paramref name="sharedGateways"/> appended
    /// to its gateways. A gateway whose identifier is already present is not added twice.
    /// </summary>
    public static AppSettings WithSharedGateways(AppSettings canonical, IEnumerable<SshGatewayDto> sharedGateways)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentNullException.ThrowIfNull(sharedGateways);

        AppSettings effective = ConfigManager.CloneSettings(canonical);
        HashSet<string> present = effective.SshGateways
            .Select(gateway => gateway.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (SshGatewayDto gateway in sharedGateways)
        {
            if (SharedSessionIds.IsShared(gateway.Id) && present.Add(gateway.Id))
            {
                effective.SshGateways.Add(gateway.CloneFaithfully());
            }
        }

        return effective;
    }
}
