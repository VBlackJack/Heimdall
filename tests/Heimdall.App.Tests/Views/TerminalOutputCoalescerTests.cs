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

using System.IO;
using System.Text;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Views;

public sealed class TerminalOutputCoalescerTests
{
    private const int ChunkBytes = 4096;
    private const int ChunkCount = 1000;
    private const int ProducerCount = 4;
    private const int ChunksPerProducer = 500;
    private const int DrainEveryChunks = 50;
    private const int SmallSliceBytes = 1024;
    private const int OversizedMessageChars = 5 * 1024 * 1024;
    private const int JoinTimeoutSeconds = 30;

    private sealed class Harness
    {
        private readonly List<Action> _scheduled = new();
        private readonly object _gate = new();
        private readonly object _uiThread = new();

        public Harness(int maxRawBytesPerMessage = TerminalOutputCoalescer.DefaultMaxRawBytesPerMessage)
        {
            Coalescer = new TerminalOutputCoalescer(
                action =>
                {
                    lock (_gate)
                    {
                        _scheduled.Add(action);
                    }
                },
                message =>
                {
                    lock (_gate)
                    {
                        Posts.Add(message);
                    }
                },
                maxRawBytesPerMessage);
        }

        public TerminalOutputCoalescer Coalescer { get; }

        public List<string> Posts { get; } = new();

        public int ScheduleCount
        {
            get
            {
                lock (_gate)
                {
                    return _scheduled.Count;
                }
            }
        }

        public void RunScheduled()
        {
            Action[] pending;
            lock (_gate)
            {
                pending = _scheduled.ToArray();
                _scheduled.Clear();
            }

            // Drains run one at a time, as they do on the single UI thread.
            lock (_uiThread)
            {
                foreach (Action action in pending)
                {
                    action();
                }
            }
        }

        public byte[] Decoded()
        {
            using var stream = new MemoryStream();
            foreach (string post in Posts)
            {
                Assert.StartsWith(TerminalOutputCoalescer.DataPrefix, post, StringComparison.Ordinal);
                stream.Write(Convert.FromBase64String(post[TerminalOutputCoalescer.DataPrefix.Length..]));
            }

            return stream.ToArray();
        }
    }

    [Fact]
    public void ThousandFourKilobyteAppends_ScheduleOnce_AndPostSixteenSlices()
    {
        var harness = new Harness();
        var expected = new byte[ChunkBytes * ChunkCount];
        new Random(1).NextBytes(expected);

        for (int index = 0; index < ChunkCount; index++)
        {
            harness.Coalescer.Append(expected.AsSpan(index * ChunkBytes, ChunkBytes));
        }

        Assert.Equal(1, harness.ScheduleCount);

        harness.Coalescer.Drain();

        Assert.Equal(16, harness.Posts.Count);
        Assert.True(expected.AsSpan().SequenceEqual(harness.Decoded()), "Decoded output differs from the appended bytes.");
    }

    [Fact]
    public void AppendDrainAppendDrain_KeepsOrder_AndSchedulesAgainOnlyAfterTheFirstDrain()
    {
        var harness = new Harness();

        harness.Coalescer.Append("A"u8);
        harness.Coalescer.Append("B"u8);
        Assert.Equal(1, harness.ScheduleCount);

        harness.Coalescer.Drain();
        Assert.Single(harness.Posts);

        harness.Coalescer.Append("C"u8);
        Assert.Equal(2, harness.ScheduleCount);

        harness.Coalescer.Drain();
        Assert.Equal(2, harness.Posts.Count);
        Assert.Equal("ABC", Encoding.ASCII.GetString(harness.Decoded()));
    }

    [Fact]
    public void Drain_WithNothingQueued_PostsNothing()
    {
        var harness = new Harness();

        harness.Coalescer.Drain();

        Assert.Empty(harness.Posts);
        Assert.Equal(0, harness.ScheduleCount);
    }

    [Fact]
    public void Append_EmptySpan_DoesNotSchedule()
    {
        var harness = new Harness();

        harness.Coalescer.Append(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, harness.ScheduleCount);
    }

    [Fact]
    public void Close_DropsQueuedBytes_AndBlocksLaterAppendsAndSchedules()
    {
        var harness = new Harness();
        harness.Coalescer.Append("lost"u8);
        Assert.Equal(1, harness.ScheduleCount);

        harness.Coalescer.Close();
        harness.RunScheduled();
        harness.Coalescer.Append("later"u8);
        harness.Coalescer.Drain();

        Assert.Empty(harness.Posts);
        Assert.Equal(0, harness.ScheduleCount);
    }

    [Fact]
    public void ConcurrentProducers_DeliverEveryByte_AndKeepEachProducerOrder()
    {
        var harness = new Harness(maxRawBytesPerMessage: SmallSliceBytes);
        using var start = new ManualResetEventSlim(false);
        Thread[] threads = Enumerable.Range(0, ProducerCount).Select(producer => new Thread(() =>
        {
            start.Wait();
            for (int sequence = 0; sequence < ChunksPerProducer; sequence++)
            {
                harness.Coalescer.Append(Encoding.ASCII.GetBytes($"{producer}:{sequence:D4};"));
                if (sequence % DrainEveryChunks == 0)
                {
                    harness.RunScheduled();
                }
            }
        })).ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        start.Set();
        foreach (Thread thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(JoinTimeoutSeconds)));
        }

        harness.RunScheduled();
        harness.Coalescer.Drain();

        string text = Encoding.ASCII.GetString(harness.Decoded());
        string[] tokens = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ProducerCount * ChunksPerProducer, tokens.Length);
        for (int producer = 0; producer < ProducerCount; producer++)
        {
            int[] sequences = tokens
                .Where(token => token.StartsWith($"{producer}:", StringComparison.Ordinal))
                .Select(token => int.Parse(token[(token.IndexOf(':') + 1)..]))
                .ToArray();
            Assert.Equal(Enumerable.Range(0, ChunksPerProducer), sequences);
        }
    }

    [Fact]
    public void ASplitUtf8Character_ArrivesContiguousAcrossAppends()
    {
        var harness = new Harness();
        byte[] euro = Encoding.UTF8.GetBytes("\u20AC");

        harness.Coalescer.Append(euro.AsSpan(0, 1));
        harness.Coalescer.Append(euro.AsSpan(1));
        harness.Coalescer.Drain();

        Assert.Single(harness.Posts);
        Assert.Equal("\u20AC", Encoding.UTF8.GetString(harness.Decoded()));
    }

    [Fact]
    public void SlicesNeverExceedTheConfiguredRawSize()
    {
        const int slice = 100;
        var harness = new Harness(slice);

        harness.Coalescer.Append(new byte[(slice * 3) + 1]);
        harness.Coalescer.Drain();

        Assert.Equal(4, harness.Posts.Count);
        Assert.All(
            harness.Posts,
            post => Assert.True(
                Convert.FromBase64String(post[TerminalOutputCoalescer.DataPrefix.Length..]).Length <= slice));
    }

    [Fact]
    public void AMessageAboveThePendingCap_ClearsTheQueue_WhichIsWhySlicingIsRequired()
    {
        int dropped = 0;
        var buffer = new PendingTerminalMessageBuffer(
            EmbeddedSshView.MaxPendingTerminalMessageBytes,
            () => dropped++);
        buffer.Enqueue("data:older");

        buffer.Enqueue(new string('x', OversizedMessageChars));

        Assert.Equal(0, buffer.Count);
        Assert.Equal(1, dropped);

        // A slice-sized message keeps drop-oldest semantics instead.
        buffer.Enqueue("data:one");
        buffer.Enqueue(TerminalOutputCoalescer.BuildMessage(
            new byte[TerminalOutputCoalescer.DefaultMaxRawBytesPerMessage]));
        Assert.Equal(2, buffer.Count);
    }
}
