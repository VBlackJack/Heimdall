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
using System.Security.Authentication;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class WinRmPreflightTests
{
    [Fact]
    public async Task EnsureReachableAsync_TcpHostNotFound_MapsToDnsFailure()
    {
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: CreateFailingSocketProbe(SocketError.HostNotFound));

        WinRmPreflightException exception = await Assert.ThrowsAsync<WinRmPreflightException>(
            () => preflight.EnsureReachableAsync(CreateServer(useSsl: false), CancellationToken.None));

        Assert.Equal("ErrorWinRmPreflightDnsFailure", exception.LocalizationKey);
    }

    [Fact]
    public async Task EnsureReachableAsync_TcpConnectionRefused_MapsToUnreachable()
    {
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: CreateFailingSocketProbe(SocketError.ConnectionRefused));

        WinRmPreflightException exception = await Assert.ThrowsAsync<WinRmPreflightException>(
            () => preflight.EnsureReachableAsync(CreateServer(useSsl: false), CancellationToken.None));

        Assert.Equal("ErrorWinRmPreflightUnreachable", exception.LocalizationKey);
    }

    [Fact]
    public async Task EnsureReachableAsync_TcpTimeout_MapsToUnreachable()
    {
        WinRmPreflight preflight = new WinRmPreflight(tcpProbe: TimeoutProbeAsync);

        WinRmPreflightException exception = await Assert.ThrowsAsync<WinRmPreflightException>(
            () => preflight.EnsureReachableAsync(CreateServer(useSsl: false), CancellationToken.None));

        Assert.Equal("ErrorWinRmPreflightUnreachable", exception.LocalizationKey);
    }

    [Fact]
    public async Task EnsureReachableAsync_TlsAuthenticationFailure_MapsToTlsFailure()
    {
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: SucceedProbeAsync,
            tlsProbe: AuthenticationFailureProbeAsync);

        WinRmPreflightException exception = await Assert.ThrowsAsync<WinRmPreflightException>(
            () => preflight.EnsureReachableAsync(CreateServer(useSsl: true), CancellationToken.None));

        Assert.Equal("ErrorWinRmPreflightTlsFailed", exception.LocalizationKey);
    }

    [Fact]
    public async Task EnsureReachableAsync_TlsFailureWithCertificateCheckSkipped_UsesNoVerifyKey()
    {
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: SucceedProbeAsync,
            tlsProbe: AuthenticationFailureProbeAsync);
        ServerProfileDto server = CreateServer(useSsl: true);
        server.WinRmSkipCertificateCheck = true;

        WinRmPreflightException exception = await Assert.ThrowsAsync<WinRmPreflightException>(
            () => preflight.EnsureReachableAsync(server, CancellationToken.None));

        Assert.Equal("ErrorWinRmPreflightTlsFailedNoVerify", exception.LocalizationKey);
    }

    [Fact]
    public async Task EnsureReachableAsync_NonSsl_SkipsTlsProbe()
    {
        bool tlsInvoked = false;
        Func<string, int, TimeSpan, bool, CancellationToken, Task> tlsProbe =
            (string host, int port, TimeSpan timeout, bool skipCertValidation, CancellationToken token) =>
            {
                tlsInvoked = true;
                return Task.CompletedTask;
            };
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: SucceedProbeAsync,
            tlsProbe: tlsProbe);

        await preflight.EnsureReachableAsync(CreateServer(useSsl: false), CancellationToken.None);

        Assert.False(tlsInvoked);
    }

    [Fact]
    public async Task EnsureReachableAsync_Ssl_PassesSkipCertificateCheckToTlsProbe()
    {
        bool? observedSkipCertValidation = null;
        Func<string, int, TimeSpan, bool, CancellationToken, Task> tlsProbe =
            (string host, int port, TimeSpan timeout, bool skipCertValidation, CancellationToken token) =>
            {
                observedSkipCertValidation = skipCertValidation;
                return Task.CompletedTask;
            };
        WinRmPreflight preflight = new WinRmPreflight(
            tcpProbe: SucceedProbeAsync,
            tlsProbe: tlsProbe);
        ServerProfileDto server = CreateServer(useSsl: true);
        server.WinRmSkipCertificateCheck = true;

        await preflight.EnsureReachableAsync(server, CancellationToken.None);

        Assert.True(observedSkipCertValidation);
    }

    [Fact]
    public async Task EnsureReachableAsync_CallerCancellation_PropagatesOperationCanceledException()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();
        WinRmPreflight preflight = new WinRmPreflight(tcpProbe: CanceledProbeAsync);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => preflight.EnsureReachableAsync(CreateServer(useSsl: false), cts.Token));
    }

    private static Func<string, int, TimeSpan, CancellationToken, Task> CreateFailingSocketProbe(SocketError socketError)
    {
        return (string host, int port, TimeSpan timeout, CancellationToken token) =>
        {
            throw new SocketException((int)socketError);
        };
    }

    private static Task SucceedProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken token) => Task.CompletedTask;

    private static Task TimeoutProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken token)
    {
        throw new TimeoutException("Timed out.");
    }

    private static Task AuthenticationFailureProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        bool skipCertValidation,
        CancellationToken token)
    {
        throw new AuthenticationException("Authentication failed.");
    }

    private static Task CanceledProbeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken token)
    {
        throw new OperationCanceledException(token);
    }

    private static ServerProfileDto CreateServer(bool useSsl)
        => new ServerProfileDto
        {
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = useSsl ? DefaultPorts.WinRmHttps : DefaultPorts.WinRmHttp,
            WinRmUseSsl = useSsl,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser
        };
}
