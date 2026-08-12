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

namespace Heimdall.Ssh.Tests;

public class FailureClassifierTests
{
    // ── Auth exception classification ──────────────────────────────────

    [Fact]
    public void Classify_AuthException_WithKeyMessage_ReturnsKeyRejected()
    {
        var ex = new SshAuthenticationException("Public key authentication failed");
        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.KeyRejected, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(ex, result.OriginalException);
    }

    [Fact]
    public void Classify_AuthException_PasswordDenied_WithPassword_ReturnsPasswordRejected()
    {
        var ex = new SshAuthenticationException("Permission denied (password).");
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Username = "user",
            Password = "secret"
        };

        var result = FailureClassifier.Classify(ex, connParams);

        Assert.Equal(SshFailureCode.PasswordRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_PasswordDenied_WithoutPassword_ReturnsAuthRejected()
    {
        var ex = new SshAuthenticationException("Permission denied.");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.AuthRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_TooManyFailures_ReturnsTooManyAuthFailures()
    {
        var ex = new SshAuthenticationException("Too many authentication failures");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.TooManyAuthFailures, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_KeyboardInteractiveWithoutPassword_ReturnsDedicatedCode()
    {
        SshAuthenticationException exception = new(
            "Server requires keyboard-interactive authentication.");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.KeyboardInteractiveNoPassword, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(exception, result.OriginalException);
    }

    [Fact]
    public void Classify_AuthException_KeyboardInteractiveWithPassword_DoesNotUseMissingPasswordCode()
    {
        SshAuthenticationException exception = new(
            "Server requires keyboard-interactive authentication.");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            Password = "secret"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.KeyRejected, result.Code);
        Assert.NotEqual(SshFailureCode.KeyboardInteractiveNoPassword, result.Code);
    }

    [Fact]
    public void Classify_AuthException_GenericMessage_ReturnsNoSupportedAuth()
    {
        var ex = new SshAuthenticationException("No auth methods available");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NoSupportedAuth, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshPassPhraseNullOrEmpty_ReturnsPassphraseRequired()
    {
        var ex = new SshPassPhraseNullOrEmptyException("Private key passphrase is required.");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.PassphraseRequired, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshException_PassphraseMessageWithKeyPassphrase_ReturnsPassphraseRejected()
    {
        var ex = new SshException("Invalid passphrase.");
        var connParams = new SshConnectionParams
        {
            Host = "example.com",
            Username = "user",
            KeyPath = @"C:\keys\id_rsa",
            KeyPassphrase = "wrong"
        };

        var result = FailureClassifier.Classify(ex, connParams);

        Assert.Equal(SshFailureCode.PassphraseRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_NoSuitableAuthenticationMethod_ReturnsNoSupportedAuth()
    {
        // Renci.SshNet.ClientAuthentication.Authenticate throws this wording when none
        // of the offered methods are supported. The "keyboard-interactive" token inside
        // the parenthetical list must not be mistaken for a key-rejection failure. A
        // password is configured so the keyboard-interactive-without-password guard does
        // not intercept this case before the fix under test is reached.
        SshAuthenticationException exception = new(
            "No suitable authentication method found to complete authentication (keyboard-interactive,password).");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            Password = "secret"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.NoSupportedAuth, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_AttemptLimitReachedForKeyboardInteractive_ReturnsTooManyAuthFailures()
    {
        // Renci.SshNet.ClientAuthentication.Authenticate throws this wording when the
        // server-side retry ceiling for a method is hit. The "keyboard-interactive" token
        // inside the parenthetical must not be mistaken for a key-rejection failure. A
        // password is configured so the keyboard-interactive-without-password guard does
        // not intercept this case before the fix under test is reached.
        SshAuthenticationException exception = new(
            "Reached authentication attempt limit for method (keyboard-interactive).");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            Password = "secret"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.TooManyAuthFailures, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_PermissionDeniedPublicKeyPassword_WithPassword_ReturnsKeyRejected()
    {
        SshAuthenticationException exception = new("Permission denied (publickey,password).");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            Password = "secret"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.KeyRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_AuthException_PermissionDeniedKeyboardInteractive_WithoutPassword_ReturnsKeyboardInteractiveNoPassword()
    {
        SshAuthenticationException exception = new("Permission denied (keyboard-interactive).");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.KeyboardInteractiveNoPassword, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshException_MacVerificationFailedForPuttyKeyFile_WithKeyPassphrase_ReturnsPassphraseRejected()
    {
        // Renci.SshNet.PuttyKeyFile constructor throws this wording when the
        // passphrase used to decrypt a PuTTY-format key is wrong.
        SshException exception = new("MAC verification failed for PuTTY key file");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            KeyPath = @"C:\keys\id_rsa.ppk",
            KeyPassphrase = "wrong"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.PassphraseRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshException_PrivateKeyBlockSizeMismatch_WithKeyPassphrase_ReturnsPassphraseRejected()
    {
        // Renci.SshNet.PrivateKeyFile.ParseSshCryptFile throws this wording when the
        // decrypted key blob length is uneven, which happens with a wrong passphrase.
        SshException exception = new("The private key section must be a multiple of the block size (8)");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            KeyPath = "/home/user/.ssh/id_rsa",
            KeyPassphrase = "wrong"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.PassphraseRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshException_OpenSshRandomCheckBytesMismatch_WithKeyPassphrase_ReturnsPassphraseRejected()
    {
        // Renci.SshNet.PrivateKeyFile.ParseOpenSshFile throws this wording when the
        // decrypted check bytes do not match, which happens with a wrong passphrase.
        SshException exception = new("The random check bytes of the OpenSSH key do not match (1 <-> 2).");
        SshConnectionParams connectionParams = new()
        {
            Host = "example.com",
            Username = "user",
            KeyPath = "/home/user/.ssh/id_ed25519",
            KeyPassphrase = "wrong"
        };

        SshFailureInfo result = FailureClassifier.Classify(exception, connectionParams);

        Assert.Equal(SshFailureCode.PassphraseRejected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_SshException_MacVerificationFailedForPuttyKeyFile_WithoutPassphraseConfigured_ReturnsKeyFileInvalid()
    {
        // Guard-intact check: with no KeyPath/KeyPassphrase configured, a corrupt key
        // file message must still fall through to the generic key-file-invalid branch,
        // not be misclassified as a passphrase rejection.
        SshException exception = new("MAC verification failed for PuTTY key file");

        SshFailureInfo result = FailureClassifier.Classify(exception);

        Assert.Equal(SshFailureCode.KeyFileInvalid, result.Code);
        Assert.True(result.IsFatal);
    }

    // ── Connection exception classification ────────────────────────────

    [Fact]
    public void Classify_ConnectionException_Refused_ReturnsNetworkRefused()
    {
        var ex = new SshConnectionException("Connection refused by remote host");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_WithInnerSocketException_UsesSocketErrorCode()
    {
        SocketException socketEx = new SocketException((int)SocketError.ConnectionRefused);
        SshConnectionException ex = new SshConnectionException("Transport failed", socketEx);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(socketEx, result.OriginalException);
    }

    [Fact]
    public void Classify_ConnectionExceptionWrappingHostKeyRejected_ReturnsHostKeyMismatch()
    {
        var hostKeyRejected = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            "SHA256:OLD");
        var ex = new SshConnectionException("Connection refused", hostKeyRejected);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.HostKeyMismatch, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(hostKeyRejected, result.OriginalException);
        Assert.False(SshReconnectPolicy.AllowsAutoReconnect(result.Code));
    }

    [Fact]
    public void Classify_ConnectionException_Reset_ReturnsNetworkReset()
    {
        var ex = new SshConnectionException("Connection reset by peer");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkReset, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_Protocol_ReturnsProtocolError()
    {
        var ex = new SshConnectionException("SSH protocol version mismatch");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ProtocolError, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_GenericMessage_ReturnsUnknown()
    {
        var ex = new SshConnectionException("Something unexpected happened");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.Unknown, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonKeyExchangeFailed_ReturnsProtocolError()
    {
        // Renci.SshNet.Security.KeyExchange.HandleKeyExchangeInitMessage throws this
        // wording, with DisconnectReason.KeyExchangeFailed, when no offered algorithm
        // matches. The typed reason must win even though the message text contains
        // none of the refused/reset/protocol tokens.
        SshConnectionException ex = new SshConnectionException(
            "No matching key exchange algorithm (server offers diffie-hellman-group14-sha256)",
            DisconnectReason.KeyExchangeFailed);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ProtocolError, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonProtocolVersionNotSupported_ReturnsProtocolError()
    {
        // Renci.SshNet.Session.ConnectSocket throws this wording, with
        // DisconnectReason.ProtocolVersionNotSupported, when the server identification
        // string reports an unsupported protocol version.
        SshConnectionException ex = new SshConnectionException(
            "Server version '1.99' is not supported.",
            DisconnectReason.ProtocolVersionNotSupported);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ProtocolError, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonProtocolError_ReturnsProtocolError()
    {
        // Renci.SshNet.Session.TryReadPacket throws this wording, with
        // DisconnectReason.ProtocolError, when the declared packet length is invalid.
        SshConnectionException ex = new SshConnectionException(
            "Bad packet length: 5.",
            DisconnectReason.ProtocolError);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ProtocolError, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonMacError_ReturnsProtocolError()
    {
        // Renci.SshNet.Session.TryReadPacket throws this wording, with
        // DisconnectReason.MacError, when the inbound MAC check fails.
        SshConnectionException ex = new SshConnectionException(
            "MAC error",
            DisconnectReason.MacError);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ProtocolError, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonConnectionLost_ReturnsSessionDisconnected()
    {
        // Renci.SshNet.Abstractions.SocketAbstraction and Renci.SshNet.Session throw
        // this wording, with DisconnectReason.ConnectionLost, when the transport is
        // torn down unexpectedly.
        SshConnectionException ex = new SshConnectionException(
            "An established connection was aborted by the server.",
            DisconnectReason.ConnectionLost);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.SessionDisconnected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonByApplication_ReturnsSessionDisconnected()
    {
        // Renci.SshNet.Session.OnDisconnectReceived wraps every server-sent disconnect
        // reason code, including ByApplication, into an SshConnectionException with this
        // message shape.
        SshConnectionException ex = new SshConnectionException(
            "The connection was closed by the server: Session closed by application. (ByApplication).",
            DisconnectReason.ByApplication);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.SessionDisconnected, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonNoMoreAuthenticationMethodsAvailable_ReturnsNoSupportedAuth()
    {
        // Renci.SshNet.Session.OnDisconnectReceived wraps every server-sent disconnect
        // reason code, including NoMoreAuthenticationMethodsAvailable, into an
        // SshConnectionException with this message shape.
        SshConnectionException ex = new SshConnectionException(
            "The connection was closed by the server: No more authentication methods available. (NoMoreAuthenticationMethodsAvailable).",
            DisconnectReason.NoMoreAuthenticationMethodsAvailable);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NoSupportedAuth, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonAuthenticationCanceledByUser_ReturnsCancelled()
    {
        // Renci.SshNet.Session.OnDisconnectReceived wraps every server-sent disconnect
        // reason code, including AuthenticationCanceledByUser, into an
        // SshConnectionException with this message shape.
        SshConnectionException ex = new SshConnectionException(
            "The connection was closed by the server: Authentication canceled by user. (AuthenticationCanceledByUser).",
            DisconnectReason.AuthenticationCanceledByUser);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.Cancelled, result.Code);
        Assert.True(result.IsFatal);
    }

    // ── Connection exception: DisconnectReason control cases ────────────

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonNone_RefusedMessage_ReturnsNetworkRefused()
    {
        // Control: DisconnectReason.None must keep falling through to the message-text
        // heuristic unchanged.
        SshConnectionException ex = new SshConnectionException(
            "Connection refused.",
            DisconnectReason.None);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonNone_ResetMessage_ReturnsNetworkReset()
    {
        // Control: DisconnectReason.None must keep falling through to the message-text
        // heuristic unchanged.
        SshConnectionException ex = new SshConnectionException(
            "Connection reset by peer.",
            DisconnectReason.None);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkReset, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_DisconnectReasonNone_UnrecognizedMessage_ReturnsUnknown()
    {
        // Control: DisconnectReason.None with no recognized token must keep falling
        // through to Unknown, unchanged.
        SshConnectionException ex = new SshConnectionException(
            "Something unexpected happened",
            DisconnectReason.None);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.Unknown, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_UnmappedDisconnectReason_RefusedMessage_FallsThroughToNetworkRefused()
    {
        // Control: a DisconnectReason with no dedicated mapping (HostNotAllowedToConnect)
        // must still fall through to the message-text heuristic, proving the mapping is
        // exhaustive-by-exclusion rather than a catch-all.
        SshConnectionException ex = new SshConnectionException(
            "Connection refused.",
            DisconnectReason.HostNotAllowedToConnect);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_ConnectionException_InnerSocketExceptionWithMappedDisconnectReason_SocketBranchStillWins()
    {
        // Control: the inner-SocketException branch is more specific and must keep
        // winning even when the typed DisconnectReason is also mapped.
        SocketException socketEx = new SocketException((int)SocketError.ConnectionRefused);
        SshConnectionException ex = new SshConnectionException(
            "Transport failed",
            DisconnectReason.ProtocolError,
            socketEx);

        SshFailureInfo result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(socketEx, result.OriginalException);
    }

    // ── Timeout exception ──────────────────────────────────────────────

    [Fact]
    public void Classify_TimeoutException_ReturnsNetworkTimedOut()
    {
        var ex = new SshOperationTimeoutException("Socket read timed out");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.NetworkTimedOut, result.Code);
        Assert.True(result.IsFatal);
    }

    // ── Proxy exception ────────────────────────────────────────────────

    [Fact]
    public void Classify_ProxyException_ReturnsForwardingFailed()
    {
        var ex = new ProxyException("SOCKS5 proxy authentication failed");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.ForwardingFailed, result.Code);
        Assert.True(result.IsFatal);
        Assert.Contains("SOCKS5", result.Message);
    }

    // ── Socket exception classification ────────────────────────────────

    [Theory]
    [InlineData(SocketError.ConnectionRefused, SshFailureCode.NetworkRefused)]
    [InlineData(SocketError.TimedOut, SshFailureCode.NetworkTimedOut)]
    [InlineData(SocketError.ConnectionReset, SshFailureCode.NetworkReset)]
    [InlineData(SocketError.HostUnreachable, SshFailureCode.NetworkUnreachable)]
    [InlineData(SocketError.NetworkUnreachable, SshFailureCode.NetworkUnreachable)]
    public void Classify_SocketException_MapsCorrectCode(SocketError socketError, SshFailureCode expectedCode)
    {
        var ex = new SocketException((int)socketError);

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(expectedCode, result.Code);
        Assert.True(result.IsFatal);
        Assert.Same(ex, result.OriginalException);
    }

    [Fact]
    public void Classify_SocketException_UnknownError_ReturnsUnknown()
    {
        var ex = new SocketException((int)SocketError.AddressAlreadyInUse);

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.Unknown, result.Code);
    }

    [Fact]
    public void Classify_IOException_WrappingSocketException_ClassifiesInner()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionRefused);
        var ioEx = new IOException("Transport error", socketEx);

        var result = FailureClassifier.Classify(ioEx);

        Assert.Equal(SshFailureCode.NetworkRefused, result.Code);
    }

    // ── Cancellation ───────────────────────────────────────────────────

    [Fact]
    public void Classify_OperationCancelled_ReturnsAuthTimeout_NotFatal()
    {
        var ex = new OperationCanceledException("User cancelled");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.AuthTimeout, result.Code);
        Assert.False(result.IsFatal);
    }

    // ── Unknown exception ──────────────────────────────────────────────

    [Fact]
    public void Classify_UnknownException_ReturnsUnknown()
    {
        var ex = new InvalidOperationException("Unexpected state");

        var result = FailureClassifier.Classify(ex);

        Assert.Equal(SshFailureCode.Unknown, result.Code);
        Assert.Equal("Unexpected state", result.Message);
        Assert.True(result.IsFatal);
    }

    [Fact]
    public void Classify_NullException_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => FailureClassifier.Classify(null!));
    }

    // ── FormatMessage ──────────────────────────────────────────────────

    [Fact]
    public void FormatMessage_WithLocalization_ReturnsLocalizedString()
    {
        var info = new SshFailureInfo(SshFailureCode.NetworkRefused, "Connection refused.", true);

        var result = FailureClassifier.FormatMessage(
            info,
            key => key == "ErrorSshNetworkRefused" ? "Connexion refusee." : null);

        Assert.Equal("Connexion refusee.", result);
    }

    [Fact]
    public void FormatMessage_WithGatewayName_PrependsPrefixed()
    {
        var info = new SshFailureInfo(SshFailureCode.NetworkRefused, "Connection refused.", true);

        var result = FailureClassifier.FormatMessage(
            info,
            key => key == "ErrorSshNetworkRefused" ? "Connexion refusee." : null,
            gatewayName: "gw-prod");

        Assert.Equal("gw-prod: Connexion refusee.", result);
    }

    [Fact]
    public void FormatMessage_NoLocalization_FallsBackToRawMessage()
    {
        var info = new SshFailureInfo(SshFailureCode.Unknown, "Something broke.", true);

        var result = FailureClassifier.FormatMessage(info, _ => null);

        Assert.Equal("Something broke.", result);
    }

    [Theory]
    [InlineData(SshFailureCode.TunnelPortOwnedByDifferentProcess)]
    [InlineData(SshFailureCode.TunnelPortNotListening)]
    [InlineData(SshFailureCode.TunnelPortOwnershipIndeterminate)]
    public void FormatMessage_UnattestedPortCodes_ShareSingleLocalizationKey(SshFailureCode code)
    {
        var info = new SshFailureInfo(code, "Ownership failed.", true);

        string result = FailureClassifier.FormatMessage(
            info,
            key => key == "ErrorSshTunnelPortOwnershipUnattested" ? "Localized ownership failure." : null);

        Assert.Equal("Localized ownership failure.", result);
    }

    [Fact]
    public void FormatMessage_NullInfo_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FailureClassifier.FormatMessage(null!, _ => null));
    }

    [Fact]
    public void FormatMessage_NullLocalizer_ThrowsArgumentNull()
    {
        var info = new SshFailureInfo(SshFailureCode.Unknown, "msg", true);
        Assert.Throws<ArgumentNullException>(() =>
            FailureClassifier.FormatMessage(info, null!));
    }
}
