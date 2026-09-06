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

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// What a user who cancels a connect sees when the server has accepted the
/// socket and then says nothing: an SSH.NET client is past the point where a
/// Disconnect can reach it, and the handshake would otherwise run to the
/// connect timeout and be reported as one.
/// </summary>
public sealed class SshConnectCancellationTests
{
    /// <summary>
    /// The connect timeout the server's silence would otherwise run to. The
    /// assertion is that a cancel returns well before it, so this is the spec
    /// the elapsed time is measured against, not a tolerance.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Failure bound for the server to observe the client's socket. Paid only
    /// when something is broken.
    /// </summary>
    private static readonly TimeSpan AcceptBackstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConnectWithCancellation_CancelledWhileServerWithholdsItsBanner_ThrowsOperationCanceledBeforeTheConnectTimeout()
    {
        using SilentSshServer server = SilentSshServer.Start();
        using SshClient client = new SshClient(
            new ConnectionInfo(
                server.Host,
                server.Port,
                "user",
                new PasswordAuthenticationMethod("user", "secret"))
            {
                Timeout = ConnectTimeout
            });
        using CancellationTokenSource cancellation = new CancellationTokenSource();

        Task connect = SshConnectionFactory.ConnectWithCancellationAsync(client, cancellation.Token);
        await server.WaitForClientAsync(AcceptBackstop);

        Stopwatch elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < ConnectTimeout,
            $"The cancel took {elapsed.Elapsed.TotalSeconds:F1} s to surface: the handshake ran to its "
            + $"{ConnectTimeout.TotalSeconds:F0} s timeout instead of being interrupted.");
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectWithCancellation_AlreadyCancelled_ThrowsWithoutTouchingTheNetwork()
    {
        using SilentSshServer server = SilentSshServer.Start();
        using SshClient client = new SshClient(
            new ConnectionInfo(
                server.Host,
                server.Port,
                "user",
                new PasswordAuthenticationMethod("user", "secret"))
            {
                Timeout = ConnectTimeout
            });
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SshConnectionFactory.ConnectWithCancellationAsync(client, cancellation.Token));

        Assert.False(server.ClientArrived);
    }

    [Fact]
    public async Task ShellSession_CancelledWhileServerWithholdsItsBanner_ThrowsOperationCanceledBeforeTheConnectTimeout()
    {
        using SilentSshServer server = SilentSshServer.Start();
        SshConnectionParams connectionParams = new SshConnectionParams
        {
            Host = server.Host,
            Port = server.Port,
            Username = "user",
            Password = "secret",
            ConnectTimeout = ConnectTimeout
        };
        SshShellSession.Transport transport = SshShellSession.Transport.Default with
        {
            ResolveHostKeyAsync = static (parameters, _, _, _) => Task.FromResult(
                new PinnedFingerprintVerifier(parameters.Host, parameters.Port, "SHA256:pinned"))
        };
        using SshShellSession session = new SshShellSession(null, transport);
        using CancellationTokenSource cancellation = new CancellationTokenSource();

        Task connect = session.ConnectAsync(
            connectionParams,
            new HostKeyStore(),
            NeverAskedVerifier.Instance,
            cancellationToken: cancellation.Token);
        await server.WaitForClientAsync(AcceptBackstop);

        Stopwatch elapsed = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < ConnectTimeout,
            $"The cancel took {elapsed.Elapsed.TotalSeconds:F1} s to surface: the handshake ran to its "
            + $"{ConnectTimeout.TotalSeconds:F0} s timeout instead of being interrupted.");
    }

    /// <summary>
    /// A TCP listener that accepts the SSH client and never writes the
    /// protocol banner, holding the client in the pre-session phase of the
    /// handshake for as long as the test wants.
    /// </summary>
    private sealed class SilentSshServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<TcpClient> _accepted;

        private SilentSshServer(TcpListener listener)
        {
            _listener = listener;
            _accepted = listener.AcceptTcpClientAsync();
        }

        public string Host => IPAddress.Loopback.ToString();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool ClientArrived => _accepted.IsCompletedSuccessfully;

        public static SilentSshServer Start()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new SilentSshServer(listener);
        }

        public async Task WaitForClientAsync(TimeSpan backstop)
        {
            await _accepted.WaitAsync(backstop);
        }

        public void Dispose()
        {
            if (_accepted.IsCompletedSuccessfully)
            {
                _accepted.Result.Dispose();
            }

            _listener.Stop();
        }
    }

    private sealed class NeverAskedVerifier : IHostKeyVerifier
    {
        public static NeverAskedVerifier Instance { get; } = new();

        public Task<HostKeyDecision> VerifyAsync(
            string host,
            int port,
            string algorithm,
            string presentedFingerprint,
            string? storedFingerprint,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The host key is pinned before the handshake in these tests.");
    }
}
