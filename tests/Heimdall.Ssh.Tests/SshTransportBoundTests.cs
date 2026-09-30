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

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Pins audit 2026-09-30 S-03: the two-minute bound that lets a person answer a
/// keyboard-interactive question must not also govern reaching the server.
/// </summary>
public sealed class SshTransportBoundTests
{
    private static readonly TimeSpan ShortConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan LongAuthenticationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Far below the authentication bound, far above the connect bound.</summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AServerThatNeverSendsItsBanner_FailsAtTheConnectTimeout_NotTheAuthenticationOne()
    {
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        int port = ((IPEndPoint)silent.LocalEndpoint).Port;
        Task<TcpClient> accepted = silent.AcceptTcpClientAsync();
        SshConnectionParams parameters = Parameters(port);
        using SshClient client = new(new ConnectionInfo(
            "127.0.0.1", port, parameters.Username, new NoneAuthenticationMethod(parameters.Username))
        {
            Timeout = LongAuthenticationTimeout,
        });

        Task connect = SshConnectionFactory.ConnectWithTransportBoundAsync(
            client,
            parameters,
            token => SshConnectionFactory.ConnectWithCancellationAsync(client, token),
            CancellationToken.None);

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => connect.WaitAsync(Backstop));
        using TcpClient server = await accepted;
    }

    [Fact]
    public async Task OnceTheHostKeyArrived_TheWaitForAnAnswerIsNotCutByTheConnectTimeout()
    {
        SshConnectionParams parameters = Parameters(port: 22);
        using SshClient client = new(new ConnectionInfo(
            "127.0.0.1", 22, parameters.Username, new NoneAuthenticationMethod(parameters.Username)));

        // Stands in for SSH.NET's connect: the key exchange delivers the host key, then the
        // authentication waits on a person for longer than the connect timeout.
        Task connect = SshConnectionFactory.ConnectWithTransportBoundAsync(
            client,
            parameters,
            async token =>
            {
                RaiseHostKeyReceived(client);
                await Task.Delay(ShortConnectTimeout * 4, token);
            },
            CancellationToken.None);

        await connect.WaitAsync(Backstop);
    }

    [Fact]
    public async Task TheCallersCancellation_IsStillACancellation()
    {
        SshConnectionParams parameters = Parameters(port: 22);
        using SshClient client = new(new ConnectionInfo(
            "127.0.0.1", 22, parameters.Username, new NoneAuthenticationMethod(parameters.Username)));
        using CancellationTokenSource caller = new();

        Task connect = SshConnectionFactory.ConnectWithTransportBoundAsync(
            client,
            parameters,
            async token =>
            {
                caller.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            caller.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Backstop));
    }

    private static SshConnectionParams Parameters(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        Username = "audit",
        ConnectTimeout = ShortConnectTimeout,
        AuthenticationTimeout = LongAuthenticationTimeout,
    };

    /// <summary>
    /// Raises <see cref="BaseClient.HostKeyReceived"/> as SSH.NET does during the key exchange.
    /// The handler under test ignores its arguments, so none are built.
    /// </summary>
    private static void RaiseHostKeyReceived(BaseClient client)
    {
        FieldInfo field = typeof(BaseClient).GetField(
                nameof(BaseClient.HostKeyReceived),
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SSH.NET no longer backs HostKeyReceived with a field.");
        EventHandler<HostKeyEventArgs>? handlers = (EventHandler<HostKeyEventArgs>?)field.GetValue(client);
        Assert.NotNull(handlers);
        handlers(client, null!);
    }
}
