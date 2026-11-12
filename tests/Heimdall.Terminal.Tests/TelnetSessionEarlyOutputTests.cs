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
using System.Threading.Channels;
using Heimdall.Terminal;

namespace Heimdall.Terminal.Tests;

/// <summary>
/// What a Telnet host sends before the terminal view subscribes (a banner and a login prompt)
/// and a peer that closes before the view attaches must both reach the first subscriber.
/// </summary>
public sealed class TelnetSessionEarlyOutputTests
{
    private const int CleanExitCode = 0;
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Telnet_BannerBeforeSubscribe_IsReplayedInOrder()
    {
        using ScriptedStream stream = new();
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);
        stream.Feed("banner|");
        stream.Feed("login:");
        await stream.WaitUntilConsumedAsync(chunkCount: 2, Backstop);

        StringBuilder received = new();
        TaskCompletionSource live = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DataReceived += data =>
        {
            lock (received)
            {
                received.Append(Encoding.UTF8.GetString(data.Span));
                if (received.ToString().EndsWith('!'))
                {
                    live.TrySetResult();
                }
            }
        };
        stream.Feed("!");
        await live.Task.WaitAsync(Backstop);

        lock (received)
        {
            Assert.Equal("banner|login:!", received.ToString());
        }
    }

    [Fact]
    public async Task Telnet_LaterSubscriber_GetsNoReplay()
    {
        using ScriptedStream stream = new();
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);
        stream.Feed("banner|");
        await stream.WaitUntilConsumedAsync(chunkCount: 1, Backstop);

        StringBuilder first = new();
        StringBuilder second = new();
        TaskCompletionSource live = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DataReceived += data =>
        {
            lock (first)
            {
                first.Append(Encoding.UTF8.GetString(data.Span));
            }
        };
        session.DataReceived += data =>
        {
            lock (second)
            {
                second.Append(Encoding.UTF8.GetString(data.Span));
            }

            live.TrySetResult();
        };
        stream.Feed("!");
        await live.Task.WaitAsync(Backstop);

        lock (second)
        {
            Assert.Equal("!", second.ToString());
        }

        lock (first)
        {
            Assert.StartsWith("banner|", first.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Telnet_PeerClosesBeforeSubscribe_ExitIsDeliveredOnSubscribe()
    {
        using ScriptedStream stream = new();
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);
        TaskCompletionSource<int> firstSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += code => firstSeen.TrySetResult(code);
        stream.EndOfStream();

        // Once the first handler has run, the exit has certainly fired and been latched.
        await firstSeen.Task.WaitAsync(Backstop);
        bool lateHandlerRan = false;
        int lateCode = -1;
        session.ProcessExited += code =>
        {
            lateHandlerRan = true;
            lateCode = code;
        };

        Assert.True(lateHandlerRan);
        Assert.Equal(CleanExitCode, lateCode);
    }

    [Fact]
    public async Task Telnet_ExitAfterSubscribe_IsRaisedOnce()
    {
        using ScriptedStream stream = new();
        using TelnetSession session = new("unused.invalid", 23);
        session.StartOnStream(stream);
        int raised = 0;
        TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProcessExited += code =>
        {
            Interlocked.Increment(ref raised);
            exit.TrySetResult(code);
        };

        stream.EndOfStream();
        Assert.Equal(CleanExitCode, await exit.Task.WaitAsync(Backstop));
        session.Dispose();

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    /// <summary>
    /// A stream whose reads deliver what the test feeds, one chunk per read, and which reports
    /// when the session has asked for more after consuming a given number of chunks.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly object _sync = new();
        private int _readCalls;
        private int _awaitedCalls = int.MaxValue;
        private TaskCompletionSource? _reached;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Feed(string text) => _incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void EndOfStream() => _incoming.Writer.TryComplete();

        /// <summary>
        /// Waits until the session has processed <paramref name="chunkCount"/> chunks: a read is
        /// only issued again once the previous chunk has been handled.
        /// </summary>
        public Task WaitUntilConsumedAsync(int chunkCount, TimeSpan timeout)
        {
            TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                _awaitedCalls = chunkCount + 1;
                _reached = reached;
                if (_readCalls >= _awaitedCalls)
                {
                    reached.TrySetResult();
                }
            }

            return reached.Task.WaitAsync(timeout);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _readCalls++;
                if (_readCalls >= _awaitedCalls)
                {
                    _reached?.TrySetResult();
                }
            }

            try
            {
                byte[] data = await _incoming.Reader.ReadAsync(cancellationToken);
                data.CopyTo(buffer);
                return data.Length;
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _incoming.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
