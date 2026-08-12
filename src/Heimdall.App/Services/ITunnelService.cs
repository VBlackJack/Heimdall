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

using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Orchestrates SSH tunnel setup for protocol handlers and connection routing.
/// </summary>
public interface ITunnelService
{
    /// <summary>
    /// Checks whether the server requires a tunnel and establishes it if needed.
    /// Returns the resolved host and port to connect to.
    /// </summary>
    Task<TunnelSetupOutcome>
        SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false);

    /// <summary>
    /// Updates the cached settings used for tunnel keepalive and Plink tuning.
    /// </summary>
    void UpdateSettings(AppSettings settings);

    /// <summary>
    /// Returns the most recent local-forwarded-port failure observed on the
    /// given tunnel local port while it is still recent enough to plausibly
    /// explain a session drop on that port; otherwise <c>null</c>.
    /// </summary>
    Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort);

    /// <summary>
    /// Releases one reference to an established tunnel local port.
    /// </summary>
    void ReleaseTunnelReference(int localPort);
}
