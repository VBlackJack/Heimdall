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

using System.Text;
using Heimdall.Core.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Drives <see cref="SshShellSession"/>'s read loop end to end through the transport
/// seam with a scripted shell stream: data, a remote end of stream, a transport
/// failure, and the race between a remote end of stream and a local Disconnect. This
/// is the code the disposed-client and read-token fixes of the SSH audit depended on,
/// and it had never been executed by a test. The last test pins that a Dispose issued
/// while the connect is still resolving the host key leaves no connected client behind.
/// </summary>
public sealed class SshShellSessionReadLoopTests
{
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ReadLoop_DeliversDataThenRemoteEof_RaisesOneCleanDisconnectAndReleasesTheStream()
    {
        ScriptedShellStream stream = new(
            ScriptedShellStream.Data("hello"),
            ScriptedShellStream.Data("world"),
            ScriptedShellStream.EndOfStream());
        using SshShellSession session = new(null, TransportFor(stream));
        StringBuilder received = new();
        List<SshSessionDisconnectInfo> disconnects = [];
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DataReceived += chunk => received.Append(Encoding.UTF8.GetString(chunk));
        session.Disconnected += info =>
        {
            lock (disconnects)
            {
                disconnects.Add(info);
            }

            disconnected.TrySetResult();
        };

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await disconnected.Task.WaitAsync(Backstop);

        Assert.Equal("helloworld", received.ToString());
        SshSessionDisconnectInfo info = Assert.Single(disconnects);
        Assert.True(info.IsClean);
        Assert.Equal(SshDisconnectMessageKeys.MessageKeyRemoteShellExited, info.MessageKey);
        Assert.Equal(1, stream.CloseCount);
        Assert.False(session.IsConnected);

        // A later local Disconnect has nothing left to announce.
        session.Disconnect();
        Assert.Single(disconnects);
    }

    [Fact]
    public async Task ReadLoop_TransportFailure_DispatchesOneClassifiedDisconnect()
    {
        ScriptedShellStream stream = new(
            ScriptedShellStream.Data("partial"),
            ScriptedShellStream.Throw(new SshConnectionException("Connection lost")));
        using SshShellSession session = new(null, TransportFor(stream));
        List<SshSessionDisconnectInfo> disconnects = [];
        int securityEvents = 0;
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SecurityEventOccurred += _ => Interlocked.Increment(ref securityEvents);
        session.Disconnected += info =>
        {
            lock (disconnects)
            {
                disconnects.Add(info);
            }

            disconnected.TrySetResult();
        };

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await disconnected.Task.WaitAsync(Backstop);

        SshSessionDisconnectInfo info = Assert.Single(disconnects);
        Assert.False(info.IsClean);
        Assert.NotNull(info.Failure);
        Assert.Equal(0, Volatile.Read(ref securityEvents));
        Assert.Equal(1, stream.CloseCount);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task ReadLoop_RemoteEofRacingALocalDisconnect_NotifiesExactlyOnce()
    {
        TaskCompletionSource eofGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedShellStream stream = new(
            ScriptedShellStream.EndOfStreamAfter(eofGate.Task));
        using SshShellSession session = new(null, TransportFor(stream));
        int disconnectedCount = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnectedCount);

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await stream.FirstReadStarted.WaitAsync(Backstop);

        // The remote end of stream is released the instant the local Disconnect starts.
        Task localDisconnect = Task.Run(() =>
        {
            eofGate.TrySetResult();
            session.Disconnect();
        });
        await localDisconnect.WaitAsync(Backstop);
        await stream.LastReadCompleted.WaitAsync(Backstop);

        Assert.Equal(1, Volatile.Read(ref disconnectedCount));
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task ReadLoop_DisposedWhileBlockedInRead_StopsWithoutAnyNotification()
    {
        ScriptedShellStream stream = new(ScriptedShellStream.BlockUntilCancelled());
        SshShellSession session = new(null, TransportFor(stream));
        int disconnectedCount = 0;
        session.Disconnected += _ => Interlocked.Increment(ref disconnectedCount);

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await stream.FirstReadStarted.WaitAsync(Backstop);

        session.Dispose();
        await stream.LastReadCompleted.WaitAsync(Backstop);

        Assert.Equal(0, Volatile.Read(ref disconnectedCount));
        Assert.Equal(1, stream.CloseCount);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_DisposedWhileResolvingTheHostKey_ThrowsAndLeavesNoConnectedClient()
    {
        TaskCompletionSource hostKeyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedShellStream stream = new(ScriptedShellStream.BlockUntilCancelled());
        DisposalRecordingSshClient client = new();
        int connectCalls = 0;
        int createCalls = 0;
        SshShellSession.Transport transport = SshShellSession.Transport.Default with
        {
            ResolveHostKeyAsync = async (parameters, _, _, _) =>
            {
                await hostKeyGate.Task;
                return new PinnedFingerprintVerifier(parameters.Host, parameters.Port, "SHA256:pinned");
            },
            CreateClient = _ =>
            {
                Interlocked.Increment(ref createCalls);
                return client;
            },
            ConnectAsync = (_, _) =>
            {
                Interlocked.Increment(ref connectCalls);
                return Task.CompletedTask;
            },
            CreateShellStream = (_, _, _) => stream
        };
        SshShellSession session = new(null, transport);

        Task connect = session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);

        // The user closes the tab while the host key prompt is still open.
        session.Dispose();
        hostKeyGate.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connect.WaitAsync(Backstop));
        // Either the connect stopped before a client existed, or the client it made is gone.
        Assert.True(
            Volatile.Read(ref createCalls) == 0 || client.Disposed,
            "a session disposed mid-connect left its client alive");
        Assert.Equal(0, Volatile.Read(ref connectCalls));
        Assert.False(session.IsConnected);
        Assert.Equal(0, stream.ReadCount);
    }

    [Fact]
    public async Task ConnectAsync_DisposedDuringTheHandshake_DisposesTheClientAndThrows()
    {
        TaskCompletionSource handshakeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedShellStream stream = new(ScriptedShellStream.BlockUntilCancelled());
        DisposalRecordingSshClient client = new();
        SshShellSession.Transport transport = SshShellSession.Transport.Default with
        {
            ResolveHostKeyAsync = static (parameters, _, _, _) => Task.FromResult(
                new PinnedFingerprintVerifier(parameters.Host, parameters.Port, "SHA256:pinned")),
            CreateClient = _ => client,
            ConnectAsync = (_, _) => handshakeGate.Task,
            CreateShellStream = (_, _, _) => stream
        };
        SshShellSession session = new(null, transport);

        Task connect = session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);

        // The user closes the tab while the key exchange is still running.
        session.Dispose();
        handshakeGate.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => connect.WaitAsync(Backstop));
        Assert.True(client.Disposed, "a session disposed during the handshake left its client alive");
        Assert.False(session.IsConnected);
        Assert.Equal(0, stream.ReadCount);
    }

    private static SshShellSession.Transport TransportFor(ISshShellStream stream) =>
        SshShellSession.Transport.Default with
        {
            ResolveHostKeyAsync = static (parameters, _, _, _) => Task.FromResult(
                new PinnedFingerprintVerifier(parameters.Host, parameters.Port, "SHA256:pinned")),
            CreateClient = static _ => new ConnectedFakeSshClient(),
            ConnectAsync = static (_, _) => Task.CompletedTask,
            CreateShellStream = (_, _, _) => stream
        };

    private static SshConnectionParams ConnectionParams() =>
        new SshConnectionParams
        {
            Host = "example.test",
            Port = 22,
            Username = "user",
            Password = "secret"
        };

    /// <summary>
    /// A shell stream that answers each read from a script: bytes, an end of stream
    /// (optionally gated), a thrown exception, or a wait on the read token.
    /// </summary>
    private sealed class ScriptedShellStream : ISshShellStream
    {
        private readonly Queue<Func<CancellationToken, ValueTask<int>>> _script;
        private readonly TaskCompletionSource _firstReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _lastReadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Memory<byte> _target;
        private int _closeCount;
        private int _readCount;

        public ScriptedShellStream(params Func<ScriptedShellStream, CancellationToken, ValueTask<int>>[] steps)
        {
            _script = new Queue<Func<CancellationToken, ValueTask<int>>>(
                steps.Select(step => new Func<CancellationToken, ValueTask<int>>(token => step(this, token))));
        }

        public Task FirstReadStarted => _firstReadStarted.Task;

        public Task LastReadCompleted => _lastReadCompleted.Task;

        public int CloseCount => Volatile.Read(ref _closeCount);

        public int ReadCount => Volatile.Read(ref _readCount);

        public static Func<ScriptedShellStream, CancellationToken, ValueTask<int>> Data(string text) =>
            (stream, _) =>
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                bytes.CopyTo(stream._target);
                return ValueTask.FromResult(bytes.Length);
            };

        public static Func<ScriptedShellStream, CancellationToken, ValueTask<int>> EndOfStream() =>
            static (_, _) => ValueTask.FromResult(0);

        public static Func<ScriptedShellStream, CancellationToken, ValueTask<int>> EndOfStreamAfter(Task gate) =>
            async (_, _) =>
            {
                await gate;
                return 0;
            };

        public static Func<ScriptedShellStream, CancellationToken, ValueTask<int>> Throw(Exception exception) =>
            (_, _) => ValueTask.FromException<int>(exception);

        public static Func<ScriptedShellStream, CancellationToken, ValueTask<int>> BlockUntilCancelled() =>
            static async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            };

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            _firstReadStarted.TrySetResult();
            _target = buffer;
            try
            {
                if (_script.Count == 0)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return 0;
                }

                Func<CancellationToken, ValueTask<int>> step = _script.Dequeue();
                return await step(cancellationToken);
            }
            finally
            {
                if (_script.Count == 0)
                {
                    _lastReadCompleted.TrySetResult();
                }
            }
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

    private class ConnectedFakeSshClient : SshClient
    {
        public ConnectedFakeSshClient()
            : base(new ConnectionInfo("example.test", 22, "user", new NoneAuthenticationMethod("user")))
        {
        }

        public override bool IsConnected => true;
    }

    private sealed class DisposalRecordingSshClient : ConnectedFakeSshClient
    {
        private bool _disposed;

        public bool Disposed => Volatile.Read(ref _disposed);

        public override bool IsConnected => !Disposed;

        protected override void Dispose(bool disposing)
        {
            Volatile.Write(ref _disposed, true);
            base.Dispose(disposing);
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
