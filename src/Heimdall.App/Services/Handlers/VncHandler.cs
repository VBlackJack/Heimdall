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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles VNC connection validation and session result creation.
/// </summary>
internal sealed class VncHandler : IProtocolHandler
{
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;

    public VncHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer)
    {
        _connectionSm = connectionSm;
        _localizer = localizer;
    }

    public string Protocol => "VNC";

    public Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            string msg = _localizer["ErrorInvalidTargetHost"];
            _connectionSm.SetError(server.Id, msg);
            return Task.FromResult(new ConnectionResult(false, msg, null));
        }

        int vncPort = server.VncPort > 0 ? server.VncPort : DefaultPorts.Vnc;
        if (!InputValidator.ValidatePortRange(vncPort))
        {
            string msg = _localizer.Format("ErrorInvalidPort", vncPort.ToString(CultureInfo.InvariantCulture));
            _connectionSm.SetError(server.Id, msg);
            return Task.FromResult(new ConnectionResult(false, msg, null));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingVnc);

        string? password = null;
        if (!string.IsNullOrEmpty(server.VncPassword))
        {
            password = ConnectionHelpers.DecryptPassword(server.VncPassword);
        }

        VncSessionResult session = new VncSessionResult(
            server.Id,
            server.RemoteServer,
            vncPort,
            password,
            server.VncViewOnly,
            server.SessionLoggingOverride);
        return Task.FromResult(new ConnectionResult(true, null, session));
    }
}
