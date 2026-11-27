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
using Heimdall.Core.Ssh;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Orchestrates connections to remote hosts across all supported protocols.
/// </summary>
public interface IConnectionService : IDisposable
{
    /// <summary>
    /// Returns the latest cached settings snapshot, or null if settings
    /// have not been loaded yet.
    /// </summary>
    AppSettings? CurrentSettings { get; }

    /// <summary>
    /// Runs authentication preflight checks for a server's gateway.
    /// </summary>
    PreflightResult RunPreflight(ServerProfileDto server, AppSettings settings);

    Task<ConnectionResult> ConnectSshAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectRdpAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct = default,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile);
    Task<ConnectionResult> ConnectSftpAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectSftpAsync(
        ServerProfileDto server,
        AppSettings settings,
        PinnedFingerprintVerifier? preResolvedHostKeyVerifier,
        CancellationToken ct = default)
        => ConnectSftpAsync(server, settings, ct);
    Task<ConnectionResult> ConnectVncAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectWebAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectTelnetAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectFtpAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectCitrixAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectLocalShellAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
    Task<ConnectionResult> ConnectWinRmAsync(ServerProfileDto server, AppSettings settings, CancellationToken ct = default);
}
