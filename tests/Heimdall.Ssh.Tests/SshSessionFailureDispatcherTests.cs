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

using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

public sealed class SshSessionFailureDispatcherTests
{
    [Fact]
    public void Dispatch_HostKeyRejectedException_Mismatch_RaisesSecurityEventAndDisconnect()
    {
        var ex = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            "SHA256:OLD");

        SshSessionSecurityEvent? captured = null;
        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            evt => captured = evt,
            info => disconnect = info);

        Assert.NotNull(captured);
        Assert.Equal(SshFailureCode.HostKeyMismatch, captured!.Code);
        Assert.Equal("gw.example.com", captured.Host);
        Assert.Equal(22, captured.Port);
        Assert.Equal("ssh-ed25519", captured.Algorithm);
        Assert.Equal("SHA256:NEW", captured.PresentedFingerprint);
        Assert.Equal("SHA256:OLD", captured.StoredFingerprint);
        Assert.NotNull(disconnect);
        Assert.Equal(SshFailureCode.HostKeyMismatch, disconnect!.Failure?.Code);
    }

    [Fact]
    public void Dispatch_HostKeyRejectedException_FirstUseRefused_RaisesCancelledAndDisconnect()
    {
        var ex = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            storedFingerprint: null);

        SshSessionSecurityEvent? captured = null;
        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            evt => captured = evt,
            info => disconnect = info);

        Assert.NotNull(captured);
        Assert.Equal(SshFailureCode.Cancelled, captured!.Code);
        Assert.NotNull(disconnect);
        Assert.Equal(SshFailureCode.Cancelled, disconnect!.Failure?.Code);
    }

    [Fact]
    public void Dispatch_WrappedHostKeyRejectedException_RaisesSecurityEventAndNonReconnectableDisconnect()
    {
        var hostKeyRejected = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            "SHA256:OLD");
        var ex = new SshConnectionException("Transport failed", hostKeyRejected);

        SshSessionSecurityEvent? captured = null;
        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            evt => captured = evt,
            info => disconnect = info);

        Assert.NotNull(captured);
        Assert.Equal(SshFailureCode.HostKeyMismatch, captured!.Code);
        Assert.Equal("SHA256:NEW", captured.PresentedFingerprint);
        Assert.NotNull(disconnect);
        Assert.Equal(SshFailureCode.HostKeyMismatch, disconnect!.Failure?.Code);
        Assert.False(SshReconnectPolicy.AllowsAutoReconnect(disconnect));
    }

    [Fact]
    public void Dispatch_GenericException_OnlyRaisesDisconnect()
    {
        var ex = new InvalidOperationException("connection reset");

        SshSessionSecurityEvent? captured = null;
        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            evt => captured = evt,
            info => disconnect = info);

        Assert.Null(captured);
        Assert.Equal("connection reset", disconnect?.Message);
        Assert.Equal(SshFailureCode.Unknown, disconnect?.Failure?.Code);
    }

    [Fact]
    public void Dispatch_AuthenticationException_RaisesTypedAuthDisconnect()
    {
        var ex = new SshAuthenticationException("Permission denied.");

        SshSessionSecurityEvent? captured = null;
        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            evt => captured = evt,
            info => disconnect = info);

        Assert.Null(captured);
        Assert.NotNull(disconnect);
        Assert.Equal(SshFailureCode.AuthRejected, disconnect!.Failure?.Code);
    }

    [Fact]
    public void Dispatch_NullSecurityHandler_StillRaisesDisconnect()
    {
        var ex = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            "SHA256:OLD");

        SshSessionDisconnectInfo? disconnect = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            securityHandler: null,
            disconnectedHandler: info => disconnect = info);

        Assert.NotNull(disconnect);
    }

    [Fact]
    public void Dispatch_NullDisconnectHandler_StillRaisesSecurityEvent()
    {
        var ex = new HostKeyRejectedException(
            "gw.example.com",
            22,
            "ssh-ed25519",
            "SHA256:NEW",
            "SHA256:OLD");

        SshSessionSecurityEvent? captured = null;

        SshSessionFailureDispatcher.Dispatch(
            ex,
            securityHandler: evt => captured = evt,
            disconnectedHandler: null);

        Assert.NotNull(captured);
    }

    [Fact]
    public void Dispatch_NullException_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SshSessionFailureDispatcher.Dispatch(null!, null, null));
    }
}
