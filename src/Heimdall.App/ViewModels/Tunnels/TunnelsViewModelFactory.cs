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
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;

namespace Heimdall.App.ViewModels.Tunnels;

/// <summary>
/// Builds tunnels view models, owning the tunnelling collaborators they need.
/// </summary>
/// <remarks>
/// Holding these here is the point of the type: the tunnel manager, the connection state machine and
/// the host-key verifier are used by the tunnels view model alone, and passing them through the shell's
/// constructor made the shell declare dependencies it never used.
/// </remarks>
public sealed class TunnelsViewModelFactory : ITunnelsViewModelFactory
{
    private readonly LocalizationManager _localizer;
    private readonly TunnelManager _tunnelManager;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;
    private readonly IConfigManager _configManager;

    public TunnelsViewModelFactory(
        LocalizationManager localizer,
        TunnelManager tunnelManager,
        ConnectionStateMachine connectionSm,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        IConfigManager configManager)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(tunnelManager);
        ArgumentNullException.ThrowIfNull(connectionSm);
        ArgumentNullException.ThrowIfNull(hostKeyStore);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        ArgumentNullException.ThrowIfNull(configManager);

        _localizer = localizer;
        _tunnelManager = tunnelManager;
        _connectionSm = connectionSm;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
        _configManager = configManager;
    }

    /// <inheritdoc />
    public TunnelsViewModel Create(MainViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return CreateForHost(owner);
    }

    /// <summary>
    /// Builds a tunnels view model for any host, which is what the shell is to it.
    /// </summary>
    /// <remarks>
    /// Internal because production only ever has a shell. It exists so the wiring can be exercised
    /// against the same host seam the view model already accepts, without standing up a whole shell.
    /// </remarks>
    /// <param name="host">The host the view model reports to.</param>
    internal TunnelsViewModel CreateForHost(ITunnelsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // The injected manager is passed through deliberately: a factory that built its own would give
        // the view model a manager nobody else publishes tunnels to.
        return new TunnelsViewModel(
            host,
            _localizer,
            _tunnelManager,
            _connectionSm,
            _hostKeyStore,
            _hostKeyVerifier,
            _configManager);
    }
}
