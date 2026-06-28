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
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.StateMachine;
using Heimdall.Sftp;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles FTP connection logic.
/// </summary>
internal sealed class FtpHandler : IProtocolHandler
{
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly FtpsCertificateStore _certificateStore;
    private readonly IFtpsCertificateVerifier _certificateVerifier;

    public FtpHandler(
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        FtpsCertificateStore certificateStore,
        IFtpsCertificateVerifier certificateVerifier)
    {
        _connectionSm = connectionSm;
        _localizer = localizer;
        _certificateStore = certificateStore;
        _certificateVerifier = certificateVerifier;
    }

    public string Protocol => "FTP";

    /// <summary>
    /// Establishes an FTP browser session using FluentFTP.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        if (string.IsNullOrWhiteSpace(server.RemoteServer))
        {
            var msg = _localizer["ErrorInvalidTargetHost"];
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        var host = server.RemoteServer;
        if (!IsValidFtpHost(host))
        {
            var msg = _localizer["ErrorInvalidTargetHost"];
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        var port = server.FtpPort > 0 ? server.FtpPort : DefaultPorts.Ftp;
        if (!InputValidator.ValidatePortRange(port))
        {
            var msg = _localizer.Format("ErrorInvalidPort", port.ToString(CultureInfo.InvariantCulture));
            _connectionSm.SetError(server.Id, msg);
            return new ConnectionResult(false, msg, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingFtp);

        var username = server.FtpUsername;
        var password = ConnectionHelpers.DecryptPassword(server.FtpPasswordEncrypted);

        var browser = new FtpBrowser(_certificateStore, _certificateVerifier);

        try
        {
            await browser.ConnectAsync(
                    host,
                    port,
                    username,
                    password,
                    server.FtpPassiveMode,
                    server.FtpUseSsl,
                    ct)
                .ConfigureAwait(false);
        }
        catch (FtpsCertificateRejectedException ex)
        {
            browser.Dispose();
            Core.Logging.FileLogger.Warn($"FTPS certificate rejected: {ex.Message}");
            var userMsg = BuildCertificateRejectedMessage(ex);
            _connectionSm.SetError(server.Id, userMsg);
            return new ConnectionResult(false, userMsg, null);
        }
        catch (Exception ex)
        {
            browser.Dispose();
            Core.Logging.FileLogger.Warn($"FTP connect failed: {ex.Message}");
            var userMsg = _localizer["ErrorFtpConnectionFailed"];
            _connectionSm.SetError(server.Id, userMsg);
            return new ConnectionResult(false, userMsg, null);
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.Connected);
        var warning = ComputeCleartextWarning(
            server.FtpUseSsl,
            username,
            host,
            port,
            _localizer);

        return new ConnectionResult(
            true,
            null,
            new FtpSessionBundle(browser, server.SessionLoggingOverride),
            Warning: warning);
    }

    internal static string? ComputeCleartextWarning(
        bool useSsl,
        string? username,
        string host,
        int port,
        LocalizationManager localizer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(localizer);

        if (useSsl || string.IsNullOrEmpty(username))
        {
            return null;
        }

        return localizer.Format(
            "WarnFtpCleartext",
            host,
            port.ToString(CultureInfo.InvariantCulture));
    }

    private string BuildCertificateRejectedMessage(FtpsCertificateRejectedException ex)
    {
        if (ex.IsMismatch && !string.IsNullOrWhiteSpace(ex.StoredFingerprint))
        {
            var message = _localizer["ErrorFtpsCertificateMismatch"];
            if (string.Equals(message, "ErrorFtpsCertificateMismatch", StringComparison.Ordinal))
            {
                message = "FTPS certificate mismatch. Stored fingerprint differs from server-presented fingerprint.";
            }

            var detail = _localizer.Format(
                "ErrorFtpsCertificateMismatchDetail",
                ex.StoredFingerprint,
                ex.PresentedFingerprint);
            if (string.Equals(detail, "ErrorFtpsCertificateMismatchDetail", StringComparison.Ordinal))
            {
                detail = $"Stored: {ex.StoredFingerprint}. Presented: {ex.PresentedFingerprint}.";
            }

            return $"{message} {detail}";
        }

        var rejected = _localizer["ErrorFtpsCertificateRejected"];
        return string.Equals(rejected, "ErrorFtpsCertificateRejected", StringComparison.Ordinal)
            ? "FTPS certificate was not trusted. Connection refused."
            : rejected;
    }

    private static bool IsValidFtpHost(string host)
    {
        return !string.IsNullOrWhiteSpace(host)
            && (InputValidator.ValidateDomain(host) || IPAddress.TryParse(host, out _));
    }
}
