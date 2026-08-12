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

using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace Heimdall.Ssh;

/// <summary>
/// Structured information about an SSH failure.
/// </summary>
/// <param name="Code">Classified failure code.</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="IsFatal">Whether the connection should be aborted.</param>
/// <param name="OriginalException">The original exception, if any.</param>
public sealed record SshFailureInfo(
    SshFailureCode Code,
    string Message,
    bool IsFatal,
    Exception? OriginalException = null);

/// <summary>
/// Classifies SSH.NET exceptions into structured <see cref="SshFailureInfo"/> records.
/// Replaces the legacy stderr regex parsing approach with typed exception analysis.
/// </summary>
public static class FailureClassifier
{
    /// <summary>
    /// Classify an exception into a structured failure code.
    /// SSH.NET provides typed exceptions for auth, connection, timeout, and proxy errors.
    /// </summary>
    /// <param name="ex">The exception to classify.</param>
    /// <param name="connectionParams">Optional connection params for context-aware classification.</param>
    /// <returns>Structured failure information.</returns>
    public static SshFailureInfo Classify(Exception ex, SshConnectionParams? connectionParams = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (HostKeyRejectionFinder.TryFind(ex, out HostKeyRejectedException? rejectedHostKey))
        {
            return ClassifyHostKeyRejected(rejectedHostKey);
        }

        return ex switch
        {
            HostKeyRejectedException directHostKeyRejected =>
                ClassifyHostKeyRejected(directHostKeyRejected),

            SshAuthenticationException authEx =>
                ClassifyAuthException(authEx, connectionParams),

            SshOperationTimeoutException =>
                new SshFailureInfo(SshFailureCode.NetworkTimedOut, "Connection timed out.", true, ex),

            ProxyException proxyEx =>
                new SshFailureInfo(SshFailureCode.ForwardingFailed, $"Proxy error: {proxyEx.Message}", true, ex),

            SshPassPhraseNullOrEmptyException =>
                new SshFailureInfo(SshFailureCode.PassphraseRequired, "SSH key requires a passphrase.", true, ex),

            SshConnectionException connEx =>
                ClassifyConnectionException(connEx),

            SocketException socketEx =>
                ClassifySocketException(socketEx),

            IOException ioEx when ioEx.InnerException is SocketException inner =>
                ClassifySocketException(inner),

            OperationCanceledException =>
                new SshFailureInfo(SshFailureCode.AuthTimeout, "Connection cancelled.", false, ex),

            SshException sshEx =>
                ClassifySshException(sshEx, connectionParams),

            _ => new SshFailureInfo(SshFailureCode.Unknown, ex.Message, true, ex)
        };
    }

    /// <summary>
    /// Format a failure info into a user-facing message, using a localizer function.
    /// Falls back to the raw message if no localized string is found.
    /// </summary>
    /// <param name="info">The failure information to format.</param>
    /// <param name="localizer">Function that maps i18n keys to localized strings (returns null if not found).</param>
    /// <param name="gatewayName">Optional gateway name prefix for multi-hop context.</param>
    /// <returns>Formatted message string.</returns>
    public static string FormatMessage(SshFailureInfo info, Func<string, string?> localizer, string? gatewayName = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(localizer);

        var prefix = gatewayName is not null ? $"{gatewayName}: " : "";
        var key = info.Code switch
        {
            SshFailureCode.PageantKeyUnavailable => "ErrorNoSshAgentRunning",
            SshFailureCode.PageantNoIdentities => "ErrorSshAgentHasNoIdentities",
            SshFailureCode.TunnelPortOwnedByDifferentProcess
                or SshFailureCode.TunnelPortNotListening
                or SshFailureCode.TunnelPortOwnershipIndeterminate
                => "ErrorSshTunnelPortOwnershipUnattested",
            _ => $"ErrorSsh{info.Code}"
        };
        var localized = localizer(key);
        return prefix + (localized ?? info.Message);
    }

    private static SshFailureInfo ClassifyAuthException(
        SshAuthenticationException ex,
        SshConnectionParams? connectionParams)
    {
        string msg = ex.Message ?? "";

        // SSH.NET exposes no typed granularity for these auth sub-cases, so
        // message inspection is a deliberate last resort. The default arm is
        // fatal, so a wording change can only make the message less precise;
        // it cannot downgrade a failure to non-fatal or success.
        if (msg.Contains("keyboard-interactive", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(connectionParams?.Password))
        {
            return new SshFailureInfo(
                SshFailureCode.KeyboardInteractiveNoPassword,
                "Server requires keyboard-interactive authentication, but no password was provided.",
                true,
                ex);
        }

        // Renci.SshNet.ClientAuthentication.TryAuthenticate: none of the offered methods
        // are supported by this client. Anchored before the generic "key" check below
        // because "keyboard-interactive" (a common item in this message's parenthetical
        // method list) contains the substring "key".
        if (msg.Contains("no suitable authentication method", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.NoSupportedAuth, "No supported authentication method.", true, ex);

        // Renci.SshNet.ClientAuthentication.TryAuthenticate: the server-side retry ceiling
        // for a method was reached. Anchored before the generic "key" check below for the
        // same reason as above.
        if (msg.Contains("attempt limit", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.TooManyAuthFailures, "Too many auth failures.", true, ex);

        if (msg.Contains("key", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.KeyRejected, "Server rejected the SSH key.", true, ex);

        if (msg.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            if (connectionParams?.Password is not null)
                return new SshFailureInfo(SshFailureCode.PasswordRejected, "SSH password was rejected.", true, ex);

            return new SshFailureInfo(SshFailureCode.AuthRejected, "Authentication was rejected.", true, ex);
        }

        if (msg.Contains("too many", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.TooManyAuthFailures, "Too many auth failures.", true, ex);

        return new SshFailureInfo(SshFailureCode.NoSupportedAuth, "No supported authentication method.", true, ex);
    }

    private static SshFailureInfo ClassifyHostKeyRejected(HostKeyRejectedException ex)
    {
        return ex.IsMismatch
            ? new SshFailureInfo(SshFailureCode.HostKeyMismatch, ex.Message, true, ex)
            : new SshFailureInfo(SshFailureCode.Cancelled, "Connection was cancelled.", false, ex);
    }

    private static SshFailureInfo ClassifySshException(
        SshException ex,
        SshConnectionParams? connectionParams)
    {
        string msg = ex.Message ?? "";

        // SSH.NET exposes no typed granularity for these key sub-cases, so
        // message inspection is a deliberate last resort. The default arm is
        // fatal, so a wording change can only make the message less precise;
        // it cannot downgrade a failure to non-fatal or success.
        // The producer-anchored tokens below cover wrong-passphrase failures that do
        // not contain the word "passphrase":
        //   - Renci.SshNet.PrivateKeyFile.PuTTY.Parse: the MAC check fails when a
        //     PuTTY-format key is decrypted with the wrong passphrase.
        //   - Renci.SshNet.PrivateKeyFile.OpenSSH.Parse: the decrypted random check
        //     bytes do not match when an OpenSSH key is decrypted with the wrong
        //     passphrase.
        //   - Renci.SshNet.PrivateKeyFile.OpenSSH.Parse: the decrypted private key section
        //     is not a multiple of the cipher block size when decrypted with the
        //     wrong passphrase.
        if (!string.IsNullOrWhiteSpace(connectionParams?.KeyPath)
            && !string.IsNullOrEmpty(connectionParams.KeyPassphrase)
            && (msg.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("mac verification failed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("random check bytes", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("multiple of the block size", StringComparison.OrdinalIgnoreCase)))
        {
            return new SshFailureInfo(
                SshFailureCode.PassphraseRejected,
                "SSH key passphrase was rejected.",
                true,
                ex);
        }

        if (msg.Contains("private key", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("key file", StringComparison.OrdinalIgnoreCase))
        {
            return new SshFailureInfo(
                SshFailureCode.KeyFileInvalid,
                "SSH key file is invalid or unsupported.",
                true,
                ex);
        }

        return new SshFailureInfo(SshFailureCode.Unknown, msg, true, ex);
    }

    private static SshFailureInfo ClassifyConnectionException(
        SshConnectionException ex)
    {
        if (ex.InnerException is SocketException socketEx)
        {
            return ClassifySocketException(socketEx);
        }

        // The typed DisconnectReason is preferred over message text, since SSH.NET
        // populates it for every server-sent disconnect and for the transport-level
        // failures it raises itself. The text heuristic below remains only for
        // DisconnectReason.None, where SSH.NET provides no typed granularity.
        switch (ex.DisconnectReason)
        {
            case DisconnectReason.ProtocolError:
            case DisconnectReason.ProtocolVersionNotSupported:
            case DisconnectReason.KeyExchangeFailed:
            case DisconnectReason.MacError:
            case DisconnectReason.CompressionError:
                return new SshFailureInfo(SshFailureCode.ProtocolError, "SSH protocol error.", true, ex);

            case DisconnectReason.ConnectionLost:
            case DisconnectReason.ByApplication:
                return new SshFailureInfo(SshFailureCode.SessionDisconnected, "SSH session was disconnected.", true, ex);

            case DisconnectReason.NoMoreAuthenticationMethodsAvailable:
                return new SshFailureInfo(SshFailureCode.NoSupportedAuth, "No supported authentication method available.", true, ex);

            case DisconnectReason.AuthenticationCanceledByUser:
                return new SshFailureInfo(SshFailureCode.Cancelled, "Authentication was cancelled.", true, ex);

            default:
                break;
        }

        string msg = ex.Message ?? "";

        // Last-resort heuristic: SSH.NET does not always expose a typed reason
        // here. The default arm is fatal, so a wording change cannot downgrade
        // a failure to non-fatal or success.
        if (msg.Contains("refused", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.NetworkRefused, "Connection refused.", true, ex);

        if (msg.Contains("reset", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.NetworkReset, "Connection reset.", true, ex);

        if (msg.Contains("protocol", StringComparison.OrdinalIgnoreCase))
            return new SshFailureInfo(SshFailureCode.ProtocolError, "SSH protocol error.", true, ex);

        return new SshFailureInfo(SshFailureCode.Unknown, msg, true, ex);
    }

    private static SshFailureInfo ClassifySocketException(SocketException ex)
    {
        return ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused =>
                new SshFailureInfo(SshFailureCode.NetworkRefused, "Connection refused.", true, ex),

            SocketError.TimedOut =>
                new SshFailureInfo(SshFailureCode.NetworkTimedOut, "Connection timed out.", true, ex),

            SocketError.ConnectionReset =>
                new SshFailureInfo(SshFailureCode.NetworkReset, "Connection reset.", true, ex),

            SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
                new SshFailureInfo(SshFailureCode.NetworkUnreachable, "Host unreachable.", true, ex),

            _ => new SshFailureInfo(SshFailureCode.Unknown, ex.Message, true, ex)
        };
    }
}
