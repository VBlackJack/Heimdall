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
using Heimdall.Terminal;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles Telnet connection logic.
/// </summary>
internal sealed class TelnetHandler : IProtocolHandler
{
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly Func<string, int, int, ITerminalSession> _sessionFactory;

    public TelnetHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer)
        : this(connectionSm, localizer, CreateSession)
    {
    }

    /// <summary>Lets a test supply the session, so a timeout or a cancel can be made to happen on demand.</summary>
    internal TelnetHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        Func<string, int, int, ITerminalSession> sessionFactory)
    {
        _connectionSm = connectionSm;
        _localizer = localizer;
        _sessionFactory = sessionFactory;
    }

    private static ITerminalSession CreateSession(string host, int port, int connectTimeoutMs)
        => new TelnetSession(host, port, connectTimeoutMs);

    public string Protocol => "TELNET";

    /// <summary>
    /// Opens a raw TCP (Telnet) connection to the specified host and port.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(
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
            return new ConnectionResult(false, msg, null);
        }

        int port = server.TelnetPort > 0 ? server.TelnetPort : DefaultPorts.Telnet;
        if (!InputValidator.ValidatePortRange(port))
        {
            string msg = _localizer.Format("ErrorInvalidPort", port.ToString(CultureInfo.InvariantCulture));
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingTelnet);

        Core.Logging.FileLogger.Info($"Launching Telnet session: {server.RemoteServer}:{port}");

        ITerminalSession session = _sessionFactory(
            server.RemoteServer,
            port,
            settings.TelnetConnectTimeoutMs);

        try
        {
            await session.StartAsync(string.Empty, string.Empty, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.Dispose();
            TelnetConnectOutcome outcome = TelnetConnectFailureMapper.Classify(ex, ct);
            if (outcome == TelnetConnectOutcome.Cancelled)
            {
                // The user cancelled: not a failure to report. Same contract as SshHandler and FtpHandler.
                throw;
            }

            Core.Logging.FileLogger.Error($"Telnet connection failed ({outcome})", ex);
            string endpoint = $"{server.RemoteServer}:{port.ToString(CultureInfo.InvariantCulture)}";
            // Only Cancelled has no key, and it was rethrown above.
            string messageKey = TelnetConnectFailureMapper.MessageKey(outcome)!;
            string userMsg = _localizer.Format(
                messageKey,
                outcome == TelnetConnectOutcome.TimedOut ? endpoint : ex.Message);
            _connectionSm.SetError(server.Id, userMsg);
            return new ConnectionResult(false, userMsg, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
        return new ConnectionResult(
            true,
            null,
            new TerminalSessionResult(
                session,
                $"{server.RemoteServer}:{port}",
                server.SessionLoggingOverride));
    }
}
