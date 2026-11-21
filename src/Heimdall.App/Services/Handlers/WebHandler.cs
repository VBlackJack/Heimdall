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

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Opens a web console: in an embedded browser tab, or with an external program when the
/// profile asks for one.
/// </summary>
internal sealed class WebHandler : IProtocolHandler
{
    /// <summary>
    /// The target the gateway's SOCKS tunnel is opened against. The tunnel carries the browser's
    /// traffic through its SOCKS proxy and nothing ever connects to its local forward, so the
    /// target only has to be the same for every console behind one gateway: that is what lets
    /// them share one tunnel instead of each binding the proxy port another one holds.
    /// </summary>
    internal const string GatewayTunnelTargetHost = "127.0.0.1";

    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly ITunnelService? _tunnelService;
    private readonly WebGatewayProxyPorts _proxyPorts;

    public WebHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        ITunnelService tunnelService,
        WebGatewayProxyPorts proxyPorts)
        : this(connectionSm, localizer, Process.Start, tunnelService, proxyPorts)
    {
    }

    internal WebHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        Func<ProcessStartInfo, Process?> startProcess,
        ITunnelService? tunnelService = null,
        WebGatewayProxyPorts? proxyPorts = null)
    {
        _connectionSm = connectionSm;
        _localizer = localizer;
        _startProcess = startProcess;
        _tunnelService = tunnelService;
        _proxyPorts = proxyPorts ?? new WebGatewayProxyPorts();
    }

    public string Protocol => "WEB";

    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        if (WebAddress.From(server) is not { } address)
        {
            return Fail(server.Id, _localizer["ErrorWebInvalidAddress"]);
        }

        string? gatewayId = SshGatewayRoute.GatewayIdFor(server, settings.GroupDefaults);
        bool viaGateway = gatewayId is not null;
        if (viaGateway && server.WebOpenExternally)
        {
            // The external program would not go through the tunnel; opening it anyway would
            // reach the console directly, or not at all, while the profile says it cannot.
            return Fail(server.Id, _localizer["ErrorWebExternalWithGateway"]);
        }

        int? proxyPort = null;
        if (viaGateway)
        {
            TunnelSetupOutcome? tunnel = await OpenGatewayProxyAsync(server, gatewayId!, settings, ct).ConfigureAwait(false);
            if (tunnel is not { Success: true })
            {
                return tunnel is null
                    ? Fail(server.Id, _localizer["ErrorWebGatewayUnavailable"])
                    : new ConnectionResult(false, tunnel.ErrorMessage, null);
            }

            proxyPort = _proxyPorts.GetOrAllocate(gatewayId!);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingWeb);

        if (server.WebOpenExternally)
        {
            return LaunchExternally(server, address);
        }

        string? password = string.IsNullOrEmpty(server.WebPasswordEncrypted)
            ? null
            : ConnectionHelpers.DecryptPassword(server.WebPasswordEncrypted);
        string? username = string.IsNullOrWhiteSpace(server.WebUsername) ? null : server.WebUsername;

        var session = new WebSessionResult(
            server.Id,
            address,
            username,
            password,
            server.SessionLoggingOverride)
        {
            GatewayId = gatewayId,
            ProxyPort = proxyPort,
            ExternalProgram = server.WebExternalProgram,
            ExternalArguments = server.WebExternalArguments,
            ExternalExecutionConfirmed = server.ExecutionConfirmed,
            DisplayName = server.DisplayName,
            SignInUsernameSelector = server.WebSignInUsernameSelector,
            SignInPasswordSelector = server.WebSignInPasswordSelector,
            SignInSubmitSelector = server.WebSignInSubmitSelector,
        };
        return new ConnectionResult(true, null, session);
    }

    /// <summary>
    /// Opens, or joins, the gateway's SOCKS tunnel for this console. The tunnel service records
    /// the lease against the profile, and closing the tab gives it back the way it does for RDP.
    /// </summary>
    private async Task<TunnelSetupOutcome?> OpenGatewayProxyAsync(
        ServerProfileDto server,
        string gatewayId,
        AppSettings settings,
        CancellationToken ct)
    {
        if (_tunnelService is null)
        {
            return null;
        }

        ServerProfileDto tunnelProfile = server.CloneFaithfully();
        tunnelProfile.RemoteServer = GatewayTunnelTargetHost;
        tunnelProfile.SshGatewayId = gatewayId;
        tunnelProfile.SocksProxyPort = _proxyPorts.GetOrAllocate(gatewayId);
        tunnelProfile.RemoteBindPort = 0;
        tunnelProfile.RemoteLocalPort = 0;
        tunnelProfile.LocalPort = 0;

        return await _tunnelService
            .SetupTunnelIfNeededAsync(tunnelProfile, DefaultPorts.Ssh, settings, ct)
            .ConfigureAwait(false);
    }

    private ConnectionResult LaunchExternally(ServerProfileDto server, Uri address)
    {
        ProcessStartInfo? startInfo = WebExternalLaunch.TryCreate(server, address, out string? errorKey);
        if (startInfo is null)
        {
            return Fail(server.Id, _localizer[errorKey!]);
        }

        Process? process;
        try
        {
            process = _startProcess(startInfo);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Core.Logging.FileLogger.Warn($"Web external launch failed for {server.DisplayName}: {ex.Message}");
            return Fail(server.Id, _localizer.Format("ErrorWebExternalLaunchFailed", ex.Message));
        }

        Core.Logging.FileLogger.Info($"Web console {server.DisplayName} opened externally.");

        // A browser that hands the address to an instance already running returns no process,
        // or one that exits at once: there is nothing left to watch, so the row goes back to idle
        // rather than claiming a client it cannot see.
        if (process is null)
        {
            _connectionSm.TryTransition(server.Id, ConnectionState.LaunchedExternalClient);
            _connectionSm.TryTransition(server.Id, ConnectionState.Disconnected);
            return new ConnectionResult(true, null, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchedExternalClient);
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                _connectionSm.TryTransition(server.Id, ConnectionState.Disconnected);
                process.Dispose();
            };
            if (process.HasExited)
            {
                _connectionSm.TryTransition(server.Id, ConnectionState.Disconnected);
            }
        }
        catch (InvalidOperationException)
        {
            _connectionSm.TryTransition(server.Id, ConnectionState.Disconnected);
        }

        return new ConnectionResult(true, null, null);
    }

    private ConnectionResult Fail(string serverId, string message)
    {
        _connectionSm.SetError(serverId, message);
        return new ConnectionResult(false, message, null);
    }
}
