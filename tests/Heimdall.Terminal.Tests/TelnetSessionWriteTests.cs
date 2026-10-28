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
using System.Text;
using Heimdall.Terminal;

namespace Heimdall.Terminal.Tests;

/// <summary>
/// The write queue and the keepalive of <see cref="TelnetSession"/>.
/// </summary>
/// <remarks>
/// The interleaving of a user keystroke with a negotiation reply inside one frame is the race the
/// single queue closes. It has no deterministic test: it needs two producers to hit the socket in
/// the same instant, so what is pinned here is the observable contract (order, and a caller that
/// never waits on the socket).
/// </remarks>
public sealed class TelnetSessionWriteTests
{
    private const int SequentialWriteCount = 1000;
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Start_TurnsOnTcpKeepAliveWithTheNamedTimings()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TelnetSession session = new(IPAddress.Loopback.ToString(), port, connectTimeoutMs: 1000);
        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            await session.StartAsync(string.Empty, string.Empty);
            using TcpClient accepted = await acceptTask.WaitAsync(Backstop);

            Socket socket = Assert.IsType<Socket>(session.ConnectedSocket);
            Assert.NotEqual(0, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            Assert.Equal(
                TelnetSession.KeepAliveTimeSeconds,
                (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
            Assert.Equal(
                TelnetSession.KeepAliveIntervalSeconds,
                (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
        }
        finally
        {
            session.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public async Task Write_ThousandSequentialWrites_ArriveInOrder()
    {
        using GatedStream stream = new(gateOpen: true);
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);

        byte[] expected = new byte[SequentialWriteCount * 2];
        for (int i = 0; i < SequentialWriteCount; i++)
        {
            byte[] chunk = { (byte)(i >> 8), (byte)(i & 0xFF) };
            chunk.CopyTo(expected, i * 2);
            session.Write(chunk);
        }

        byte[] received = await stream.WaitForBytesAsync(expected.Length, Backstop);

        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task Write_WhileTheStreamIsBlocked_ReturnsAtOnce()
    {
        using GatedStream stream = new(gateOpen: false);
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);

        // Run on the pool and bound the wait: a Write that blocks on the socket must fail this test
        // instead of hanging it. The gate is opened afterwards so the queued bytes are still delivered.
        Task write = Task.Run(() => session.Write(Encoding.ASCII.GetBytes("typed")));
        await write.WaitAsync(Backstop);

        Assert.Empty(stream.Snapshot());

        stream.OpenGate();
        byte[] received = await stream.WaitForBytesAsync(5, Backstop);
        Assert.Equal("typed", Encoding.ASCII.GetString(received));
    }

    [Fact]
    public async Task NegotiationReplyAndKeystrokes_ShareOneOrderedQueue()
    {
        using GatedStream stream = new(gateOpen: true);
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);

        session.Write(Encoding.ASCII.GetBytes("a"));
        stream.Feed(new byte[] { 255, 253, 31 }); // IAC DO NAWS
        _ = await stream.WaitForBytesAsync(1 + 3 + 9, Backstop);
        session.Write(Encoding.ASCII.GetBytes("b"));

        byte[] received = await stream.WaitForBytesAsync(1 + 3 + 9 + 1, Backstop);

        // Keystroke, WILL NAWS, the NAWS frame for 80x24, keystroke: each frame whole, in queue order.
        Assert.Equal(
            new byte[] { 0x61, 255, 251, 31, 255, 250, 31, 0, 80, 0, 24, 255, 240, 0x62 },
            received);
    }

    /// <summary>
    /// A stream whose writes wait on a gate and are then recorded, and whose reads deliver what the
    /// test feeds. The synchronous Write blocks on the gate on purpose: it is what a caller that
    /// writes straight to the socket would experience.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly object _sync = new();
        private readonly List<byte> _written = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly System.Threading.Channels.Channel<byte[]> _incoming =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public GatedStream(bool gateOpen)
        {
            if (gateOpen)
            {
                _gate.SetResult();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void OpenGate() => _gate.TrySetResult();

        public void Feed(byte[] data) => _incoming.Writer.TryWrite(data);

        public byte[] Snapshot()
        {
            lock (_sync)
            {
                return _written.ToArray();
            }
        }

        public async Task<byte[]> WaitForBytesAsync(int count, TimeSpan timeout)
        {
            using CancellationTokenSource cts = new(timeout);
            while (true)
            {
                byte[] snapshot = Snapshot();
                if (snapshot.Length >= count)
                {
                    return snapshot;
                }

                try
                {
                    await Task.Delay(5, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Only {snapshot.Length} of {count} bytes arrived.");
                }
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            Record(buffer.Span);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _gate.Task.Wait();
            Record(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] data = await _incoming.Reader.ReadAsync(cancellationToken);
                data.CopyTo(buffer);
                return data.Length;
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _incoming.Writer.TryComplete();
            _gate.TrySetResult();
            base.Dispose(disposing);
        }

        private void Record(ReadOnlySpan<byte> data)
        {
            lock (_sync)
            {
                _written.AddRange(data.ToArray());
            }
        }
    }
}
