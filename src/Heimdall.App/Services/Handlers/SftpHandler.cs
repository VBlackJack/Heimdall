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
using Heimdall.App.Localization;
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
    private readonly IDialogService _dialogService;
    private readonly ConnectBrowserDelegate _connectBrowser;

    /// <summary>How the handler reaches a remote host. Substitutable for tests only.</summary>
    internal delegate Task ConnectBrowserDelegate(
        SftpBrowser browser,
        SshConnectionParams sshParams,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        CancellationToken ct);

    /// <param name="dialogService">
    /// Non-optional deliberately. An optional dialog service defaulting to null is how a
    /// feature ships fully tested and completely inert because nothing wired it - the
    /// shape this repository has already paid for once, with a close guard attached to no
    /// host for weeks.
    /// </param>
    /// <param name="connectBrowser">
    /// Test seam only, defaulted to the real call. Follows the precedent already used by
    /// the SSH and WinRM handlers.
    /// </param>
    public SftpHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        HostKeyStore hostKeyStore,
        IHostKeyVerifier hostKeyVerifier,
        IDialogService dialogService,
        ConnectBrowserDelegate? connectBrowser = null)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _hostKeyStore = hostKeyStore;
        _hostKeyVerifier = hostKeyVerifier;
        _dialogService = dialogService;
        _connectBrowser = connectBrowser
            ?? ((browser, sshParams, store, verifier, token) =>
                browser.ConnectAsync(sshParams, store, verifier, token));
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

        // SFTP has no external launcher to fall back on, so a blank login name is
        // always fatal. Refused before the tunnel and the host-key prompt, for the same
        // reason as the SSH handler.
        if (ConnectionHelpers.RequiresUsernameToConnect(server))
        {
            string usernameMsg = _localizer[SshLocalizationKeys.ErrorSshUsernameRequired];
            _connectionSm.SetError(server.Id, usernameMsg);
            return new ConnectionResult(
                false,
                usernameMsg,
                null,
                SshSessionDiagnosticFactory.CreatePreflightFailure(
                    SshLocalizationKeys.ErrorSshUsernameRequired,
                    usernameMsg));
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

        // One builder for both attempts. SshConnectionParams is sealed with init-only
        // members and there is no `with` expression, so a hand-copied retry object is the
        // one silent bug this change could ship: fourteen properties where thirteen
        // matching and one drifting would look exactly like a working feature.
        SshConnectionParams BuildSshParams(string? password) => new()
        {
            Host = targetHost,
            Port = targetPort,
            LogicalHost = usesTunnel ? server.RemoteServer : null,
            LogicalPort = usesTunnel ? port : null,
            Username = server.SshUsername ?? string.Empty,
            Password = password,
            KeyPassphrase = ConnectionHelpers.DecryptPassword(server.SshKeyPassphraseEncrypted),
            KeyPath = string.IsNullOrWhiteSpace(server.SshKeyPath) ? null : server.SshKeyPath,
            SshAgentPreference = settings.SshAgentPreference,
            UseLegacyPasswordAsKeyPassphrase = server.UsesLegacySshCredentialMapping,
            LegacyCredentialName = server.DisplayName,
            AgentForwarding = server.SshAgentForwarding,
            Compression = server.SshCompression,
            KeepAliveIntervalSeconds = settings.SshKeepAliveIntervalSeconds
        };

        // The tunnel outlives a failed attempt now, and is released once, here. Releasing
        // it inside a catch was destructive rather than a dereference: the forward is
        // disposed at zero references, and a profile with no explicit local port would get
        // a DIFFERENT, OS-assigned port on any re-setup. This also closes a pre-existing
        // leak - anything throwing between the tunnel setup and the first connect used to
        // leave it open.
        bool releaseTunnel = usesTunnel;
        try
        {
            string? attemptPassword = ConnectionHelpers.DecryptPassword(server.SshPasswordEncrypted);

            for (int attempt = 1; attempt <= SftpPasswordPromptPolicy.MaxConnectAttempts; attempt++)
            {
                SshConnectionParams sshParams = BuildSshParams(attemptPassword);
                var browser = new SftpBrowser();
                SshFailureInfo failure;

                try
                {
                    await _connectBrowser(browser, sshParams, _hostKeyStore, _hostKeyVerifier, ct)
                        .ConfigureAwait(false);

                    _connectionSm.TryTransition(server.Id, ConnectionState.Connected);

                    // The session takes ownership of the tunnel from here.
                    releaseTunnel = false;
                    return new ConnectionResult(true, null, new SftpSessionBundle(
                        browser,
                        sshParams,
                        server.SessionLoggingOverride),
                        Warning: capabilityWarning);
                }
                catch (HostKeyRejectedException ex)
                {
                    browser.Dispose();

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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // The user's own cancel, not a failure. Without this arm it lands in
                    // the generic catch and is classified as a timeout, so the user is
                    // shown an error modal for something they asked for.
                    browser.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    browser.Dispose();
                    failure = FailureClassifier.Classify(ex, sshParams);
                    Core.Logging.FileLogger.Warn($"SFTP connect failed: {failure.Code} - {ex.Message}");
                }

                bool mayRetype = attempt < SftpPasswordPromptPolicy.MaxConnectAttempts
                    && server.AllowCredentialPrompt
                    && SftpPasswordPromptPolicy.AllowsPasswordRetry(failure.Code);

                if (mayRetype)
                {
                    // Asked outside the catch: the dialog's own failures must not be
                    // reclassified as connection failures.
                    string? typed = await PromptForPasswordAsync(server, attemptPassword, ct)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(typed))
                    {
                        attemptPassword = typed;
                        continue;
                    }
                }

                // Localized here, which the SFTP path never did: the classifier's message
                // is an English literal, and the SSH path routes the same failure through
                // the catalogue. The same server produced English from one protocol and
                // French from the other.
                SshFailureInfo localized = SshFailureLocalizer.Localize(failure, _localizer, targetHost);
                _connectionSm.SetError(server.Id, localized.Message);
                return new ConnectionResult(
                    false,
                    localized.Message,
                    null,
                    SshSessionDiagnosticFactory.FromClassifiedFailure(localized));
            }

            throw new InvalidOperationException(
                "The SFTP connect loop must return from inside its body.");
        }
        finally
        {
            if (releaseTunnel)
            {
                ReleaseTunnelIfNeeded(usesTunnel, targetPort);
            }
        }
    }

    /// <summary>
    /// Offers a password for a connection that has already failed for want of one.
    /// </summary>
    /// <remarks>
    /// The typed value goes into a fresh <see cref="SshConnectionParams"/> and nowhere
    /// else. It is never written back to the profile, never protected to disk, never
    /// logged, and never persisted in any form - the handler holds no configuration
    /// manager, so the write-back path is not merely avoided but unreachable from here.
    /// It dies with the attempt; the next connection asks again.
    /// <para>
    /// The message is chosen on what was actually SENT rather than on the failure code,
    /// because the classifier's "no supported authentication method" is its default arm
    /// and can fire with a password present.
    /// </para>
    /// </remarks>
    private Task<string?> PromptForPasswordAsync(
        ServerProfileDto server,
        string? attemptedPassword,
        CancellationToken ct)
    {
        // The logical host, not the tunnel endpoint: through a tunnel the latter is a
        // loopback bind address, which names nothing the user recognizes.
        string account = string.IsNullOrWhiteSpace(server.SshUsername)
            ? server.RemoteServer ?? string.Empty
            : $"{server.SshUsername}@{server.RemoteServer}";

        string messageKey = string.IsNullOrEmpty(attemptedPassword)
            ? SshLocalizationKeys.DialogSftpPasswordPromptNoCredential
            : SshLocalizationKeys.DialogSftpPasswordPromptRefused;

        return _dialogService.ShowPasswordInputAsync(
            _localizer[SshLocalizationKeys.DialogSftpPasswordPromptTitle],
            _localizer.Format(messageKey, account),
            ct);
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
