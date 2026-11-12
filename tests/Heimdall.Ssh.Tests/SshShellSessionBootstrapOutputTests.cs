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

namespace Heimdall.Ssh.Tests;

/// <summary>
/// Output produced before the terminal view subscribes (a banner and a prompt printed once
/// by network equipment) must reach the first subscriber, in order, and only it.
/// </summary>
public sealed class SshShellSessionBootstrapOutputTests
{
    private const int SmallCapBytes = 8;
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    /// <summary>Time given to a live delivery to reach a gate that would wrongly let it through.</summary>
    private static readonly TimeSpan LiveDeliverySettle = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void DataBeforeFirstSubscriber_IsReplayedInOrder()
    {
        ShellOutputGate gate = new();
        gate.Deliver(Bytes("banner|"));
        gate.Deliver(Bytes("router#"));
        StringBuilder received = new();

        gate.Add(chunk => received.Append(Encoding.UTF8.GetString(chunk)));
        gate.Deliver(Bytes(" live"));

        Assert.Equal("banner|router# live", received.ToString());
    }

    [Fact]
    public async Task DataReadDuringConnect_ReachesASubscriberAttachedAfterConnect()
    {
        ScriptedStream stream = new(Bytes("banner|"), Bytes("router#"));
        using SshShellSession session = new(null, TransportFor(stream));

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await stream.Drained.WaitAsync(Backstop);

        StringBuilder received = new();
        session.DataReceived += chunk => received.Append(Encoding.UTF8.GetString(chunk));

        Assert.Equal("banner|router#", received.ToString());
    }

    [Fact]
    public void BootstrapBuffer_IsCapped()
    {
        ShellOutputGate gate = new(SmallCapBytes);
        gate.Deliver(Bytes("12345"));
        gate.Deliver(Bytes("6789"));
        gate.Deliver(Bytes("ab"));
        StringBuilder received = new();

        gate.Add(chunk => received.Append(Encoding.UTF8.GetString(chunk)));

        // The chunk that would cross the cap closes the buffer: the later "ab" would still fit,
        // but replaying it after a hole could start inside an escape sequence.
        Assert.Equal("12345", received.ToString());
    }

    [Fact]
    public void LaterSubscriber_GetsNoReplay()
    {
        ShellOutputGate gate = new();
        gate.Deliver(Bytes("prompt"));
        StringBuilder first = new();
        StringBuilder second = new();

        gate.Add(chunk => first.Append(Encoding.UTF8.GetString(chunk)));
        gate.Add(chunk => second.Append(Encoding.UTF8.GetString(chunk)));
        gate.Deliver(Bytes("!"));

        Assert.Equal("prompt!", first.ToString());
        Assert.Equal("!", second.ToString());
    }

    [Fact]
    public void ReleasedGate_DropsBufferedOutput()
    {
        ShellOutputGate gate = new();
        gate.Deliver(Bytes("lost"));
        gate.Release();
        gate.Deliver(Bytes("lost too"));
        StringBuilder received = new();

        gate.Add(chunk => received.Append(Encoding.UTF8.GetString(chunk)));

        Assert.Equal(string.Empty, received.ToString());
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotStopLaterSubscribersOrLaterChunks()
    {
        ShellOutputGate gate = new();
        StringBuilder received = new();
        gate.Add(_ => throw new InvalidOperationException("subscriber failure"));
        gate.Add(chunk => received.Append(Encoding.UTF8.GetString(chunk)));

        gate.Deliver(Bytes("one|"));
        gate.Deliver(Bytes("two"));

        Assert.Equal("one|two", received.ToString());
    }

    [Fact]
    public async Task LiveChunk_NeverOvertakesTheReplay()
    {
        ShellOutputGate gate = new();
        gate.Deliver(Bytes("replay1|"));
        gate.Deliver(Bytes("replay2|"));
        List<string> order = [];
        using ManualResetEventSlim insideReplay = new(false);
        using ManualResetEventSlim releaseReplay = new(false);

        Task subscribe = Task.Run(() => gate.Add(chunk =>
        {
            string text = Encoding.UTF8.GetString(chunk);
            lock (order)
            {
                order.Add(text);
            }

            if (text == "replay1|")
            {
                insideReplay.Set();
                releaseReplay.Wait(Backstop);
            }
        }));
        Assert.True(insideReplay.Wait(Backstop), "the replay never started");

        using ManualResetEventSlim liveAboutToDeliver = new(false);
        Task live = Task.Run(() =>
        {
            liveAboutToDeliver.Set();
            gate.Deliver(Bytes("live"));
        });
        Assert.True(liveAboutToDeliver.Wait(Backstop), "the live delivery never started");
        await Task.Delay(LiveDeliverySettle);

        string[] seenWhileReplayBlocked;
        lock (order)
        {
            seenWhileReplayBlocked = [.. order];
        }

        releaseReplay.Set();
        await Task.WhenAll(subscribe, live).WaitAsync(Backstop);

        Assert.Equal(["replay1|"], seenWhileReplayBlocked);
        Assert.Equal(["replay1|", "replay2|", "live"], order);
    }

    [Fact]
    public async Task DisconnectBeforeFirstSubscriber_IsDeliveredOnSubscribe()
    {
        ScriptedStream stream = new(Bytes("max sessions reached"));
        stream.EndAfterChunks = true;
        using SshShellSession session = new(null, TransportFor(stream));

        // The probe is not the subject: its signal proves the disconnect has been announced, and
        // so latched, before the late handler is added.
        TaskCompletionSource announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += _ => announced.TrySetResult();
        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await announced.Task.WaitAsync(Backstop);

        List<string> events = [];
        session.DataReceived += chunk => events.Add("data:" + Encoding.UTF8.GetString(chunk));
        bool lateHandlerRan = false;
        session.Disconnected += _ =>
        {
            events.Add("disconnected");
            lateHandlerRan = true;
        };

        Assert.True(lateHandlerRan, "the late handler must run inside the subscription");
        Assert.Equal(["data:max sessions reached", "disconnected"], events);
    }

    [Fact]
    public async Task DisconnectAfterSubscribe_IsRaisedOnce()
    {
        ScriptedStream stream = new(Bytes("bye"));
        stream.EndAfterChunks = true;
        using SshShellSession session = new(null, TransportFor(stream));
        int early = 0;
        TaskCompletionSource fired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += _ =>
        {
            Interlocked.Increment(ref early);
            fired.TrySetResult();
        };

        await session.ConnectAsync(ConnectionParams(), new HostKeyStore(), NeverAskedVerifier.Instance);
        await fired.Task.WaitAsync(Backstop);
        int late = 0;
        session.Disconnected += _ => Interlocked.Increment(ref late);
        session.Disconnect();

        Assert.Equal(1, Volatile.Read(ref early));
        Assert.Equal(1, Volatile.Read(ref late));
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

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

    /// <summary>Serves the given chunks, then blocks until the read is cancelled.</summary>
    private sealed class ScriptedStream : ISshShellStream
    {
        private readonly Queue<byte[]> _chunks;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ScriptedStream(params byte[][] chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
        }

        /// <summary>When set, the stream reports a remote end of stream after the last chunk.</summary>
        public bool EndAfterChunks { get; set; }

        /// <summary>Completes once every chunk has been handed to the read loop.</summary>
        public Task Drained => _drained.Task;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_chunks.Count > 0)
            {
                byte[] chunk = _chunks.Dequeue();
                chunk.CopyTo(buffer);
                return chunk.Length;
            }

            _drained.TrySetResult();
            if (EndAfterChunks)
            {
                return 0;
            }

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
