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

using Heimdall.Core.Ssh;
using Renci.SshNet;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Who owns a shell session once it is connected. The token handed to
/// <see cref="SshShellSession.ConnectAsync"/> governs the connect; the session
/// itself is released through <see cref="SshShellSession.Disconnect"/> or
/// <see cref="SshShellSession.Dispose"/>, which is what every owner in the
/// application calls. A read loop that also listened to the connect token
/// exited silently when that token was cancelled after a successful connect,
/// with no cleanup and no <see cref="SshShellSession.Disconnected"/>, leaving
/// a connected client and its key file alive with nothing watching them.
/// </summary>
public sealed class SshShellSessionConnectTokenTests
{
    /// <summary>
    /// Failure bound for the read loop to reach its first read. Paid only when
    /// something is broken.
    /// </summary>
    private static readonly TimeSpan ReadStartBackstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConnectToken_CancelledAfterConnect_DoesNotReachTheReadLoop()
    {
        FakeShellStream stream = new FakeShellStream();
        SshShellSession.Transport transport = SshShellSession.Transport.Default with
        {
            ResolveHostKeyAsync = static (parameters, _, _, _) => Task.FromResult(
                new PinnedFingerprintVerifier(parameters.Host, parameters.Port, "SHA256:pinned")),
            CreateClient = static _ => new ConnectedFakeSshClient(),
            ConnectAsync = static (_, _) => Task.CompletedTask,
            CreateShellStream = (_, _, _) => stream
        };
        using SshShellSession session = new SshShellSession(null, transport);
        using CancellationTokenSource connectCancellation = new CancellationTokenSource();
        int disconnectedCount = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnectedCount);

        await session.ConnectAsync(
            ConnectionParams(),
            new HostKeyStore(),
            NeverAskedVerifier.Instance,
            cancellationToken: connectCancellation.Token);
        await stream.FirstReadStarted.WaitAsync(ReadStartBackstop);

        connectCancellation.Cancel();

        // A linked read token is cancelled synchronously by the connect token,
        // and the registration below fires before Cancel() returns.
        Assert.False(stream.ReadTokenCancelled);
        Assert.True(session.IsConnected);
        Assert.Equal(0, stream.CloseCount);
        Assert.Equal(0, Volatile.Read(ref disconnectedCount));

        session.Disconnect();

        Assert.True(stream.ReadTokenCancelled);
        Assert.Equal(1, stream.CloseCount);
        Assert.Equal(1, Volatile.Read(ref disconnectedCount));
        Assert.False(session.IsConnected);
    }

    private static SshConnectionParams ConnectionParams() =>
        new SshConnectionParams
        {
            Host = "example.test",
            Port = 22,
            Username = "user",
            Password = "secret"
        };

    /// <summary>
    /// A shell stream whose read never completes on its own and records
    /// whether the token it was handed is cancelled.
    /// </summary>
    private sealed class FakeShellStream : ISshShellStream
    {
        private readonly TaskCompletionSource _firstReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closeCount;
        private bool _readTokenCancelled;

        public Task FirstReadStarted => _firstReadStarted.Task;

        public bool ReadTokenCancelled => Volatile.Read(ref _readTokenCancelled);

        public int CloseCount => Volatile.Read(ref _closeCount);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration observed = cancellationToken.Register(
                () => Volatile.Write(ref _readTokenCancelled, true));
            _firstReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public void Write(byte[] data, int offset, int count)
        {
        }

        public void Flush()
        {
        }

        public void ChangeWindowSize(uint columns, uint rows, uint width, uint height)
        {
        }

        public void Close()
        {
            Interlocked.Increment(ref _closeCount);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConnectedFakeSshClient : SshClient
    {
        public ConnectedFakeSshClient()
            : base(new ConnectionInfo("example.test", 22, "user", new NoneAuthenticationMethod("user")))
        {
        }

        public override bool IsConnected => true;
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
