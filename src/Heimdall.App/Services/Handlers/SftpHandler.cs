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
using System.Net;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles SFTP connection logic.
/// </summary>
internal sealed class SftpHandler : IProtocolHandler
{
    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly HostKeyStore _hostKeyStore;
    private readonly IHostKeyVerifier _hostKeyVerifier;

    public SftpHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier)
    {
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
    }

    public string Protocol => "SFTP";

    /// <summary>
    /// Establishes an SFTP browser session, optionally through a tunnel.
    /// Returns a connected <see cref="SftpBrowser"/> on success.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.SftpBrowserEnabled)
        {
            var msg = _localizer["ErrorSftpBrowserDisabled"];
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            var msg = _localizer["ErrorInvalidTargetHost"];
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        var host = server.RemoteServer;
        if (!IsValidSftpHost(host))
        {
            var msg = _localizer["ErrorInvalidTargetHost"];
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        var port = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        if (!InputValidator.ValidatePortRange(port))
        {
            var msg = _localizer.Format("ErrorInvalidPort", port.ToString(CultureInfo.InvariantCulture));
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        (bool tunnelOk, bool usesTunnel, string targetHost, int targetPort, string? tunnelError) =
            await _tunnelService.SetupTunnelIfNeededAsync(server, port, settings, ct)
                .ConfigureAwait(false);

        if (!tunnelOk)
        {
            return new ConnectionResult(false, tunnelError, null, SshSessionDiagnosticFactory.CreateGatewayFailure(tunnelError));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingSftp);

        SshCapabilityNotice? capabilityNotice = SshCapabilityScope.Evaluate(
            SshResolvedPath.Direct,
            x11Forwarding: false,
            compression: server.SshCompression);
        string? capabilityWarning = capabilityNotice is null
            ? null
            : _localizer[capabilityNotice.StatusLocalizationKey];

        var sshParams = new SshConnectionParams
        {
            Host = targetHost,
            Port = targetPort,
            LogicalHost = usesTunnel ? server.RemoteServer : null,
            LogicalPort = usesTunnel ? port : null,
            Username = server.SshUsername ?? string.Empty,
            Password = ConnectionHelpers.DecryptPassword(server.SshPasswordEncrypted),
            KeyPassphrase = ConnectionHelpers.DecryptPassword(server.SshKeyPassphraseEncrypted),
            KeyPath = string.IsNullOrWhiteSpace(server.SshKeyPath) ? null : server.SshKeyPath,
            SshAgentPreference = settings.SshAgentPreference,
            UseLegacyPasswordAsKeyPassphrase = server.UsesLegacySshCredentialMapping,
            LegacyCredentialName = server.DisplayName,
            AgentForwarding = server.SshAgentForwarding,
            Compression = server.SshCompression,
            KeepAliveIntervalSeconds = settings.SshKeepAliveIntervalSeconds
        };

        var browser = new SftpBrowser();

        try
        {
            await browser.ConnectAsync(sshParams, _hostKeyStore, _hostKeyVerifier, ct)
                .ConfigureAwait(false);
        }
        catch (HostKeyRejectedException ex)
        {
            browser.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);

            if (ex.IsMismatch && !string.IsNullOrWhiteSpace(ex.StoredFingerprint))
            {
                var message = BuildHostKeyMismatchMessage(
                    ex.StoredFingerprint,
                    ex.PresentedFingerprint);
                _connectionSm.SetError(server.Id, message);
                return new ConnectionResult(
                    false,
                    message,
                    null,
                    SshSessionDiagnosticFactory.CreateHostKeyMismatchFailure(
                        ex.StoredFingerprint,
                        ex.PresentedFingerprint,
                        ex.Host,
                        ex.Port));
            }

            var messageCancelled = BuildCancelledMessage();
            _connectionSm.SetError(server.Id, messageCancelled);
            return new ConnectionResult(
                false,
                messageCancelled,
                null,
                SshSessionDiagnosticFactory.FromClassifiedFailure(
                    new SshFailureInfo(SshFailureCode.Cancelled, messageCancelled, false, ex)));
        }
        catch (Exception ex)
        {
            browser.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, targetPort);
            var failure = FailureClassifier.Classify(ex, sshParams);
            Core.Logging.FileLogger.Warn($"SFTP connect failed: {failure.Code} - {ex.Message}");
            _connectionSm.SetError(server.Id, failure.Message);
            return new ConnectionResult(false, failure.Message, null, SshSessionDiagnosticFactory.FromClassifiedFailure(failure));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
        return new ConnectionResult(true, null, new SftpSessionBundle(
            browser,
            sshParams,
            server.SessionLoggingOverride),
            Warning: capabilityWarning);
    }

    private static bool IsValidSftpHost(string host)
    {
        return !string.IsNullOrWhiteSpace(host)
            && (InputValidator.ValidateDomain(host) || IPAddress.TryParse(host, out _));
    }

    private void ReleaseTunnelIfNeeded(bool usesTunnel, int tunnelLocalPort)
    {
        if (!usesTunnel || tunnelLocalPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(tunnelLocalPort);
    }

    private string BuildHostKeyMismatchMessage(
        string storedFingerprint,
        string presentedFingerprint)
    {
        var message = _localizer["ErrorHostKeyMismatch"];
        if (string.Equals(message, "ErrorHostKeyMismatch", StringComparison.Ordinal))
        {
            message = "SSH host key mismatch \u2014 possible MITM. Stored fingerprint differs from server-presented fingerprint.";
        }

        var detail = _localizer.Format(
            "ErrorHostKeyMismatchDetail",
            storedFingerprint,
            presentedFingerprint);
        if (string.Equals(detail, "ErrorHostKeyMismatchDetail", StringComparison.Ordinal))
        {
            detail = $"Stored: {storedFingerprint}. Presented: {presentedFingerprint}.";
        }

        return $"{message} {detail}";
    }

    private string BuildCancelledMessage()
    {
        var message = _localizer["ErrorSshCancelled"];
        return string.Equals(message, "ErrorSshCancelled", StringComparison.Ordinal)
            ? "Connection was cancelled."
            : message;
    }
}
