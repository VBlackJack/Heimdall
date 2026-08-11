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
