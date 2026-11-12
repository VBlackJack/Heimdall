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

using System.Reflection;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Terminal;
using Heimdall.Terminal;
using Heimdall.Terminal.ConPty;

namespace Heimdall.Terminal.Tests;

/// <summary>
/// After the bootstrap buffer overflows, what the first subscriber is replayed must stay an
/// unbroken prefix of the stream. A real child cannot be made to emit chunks of chosen sizes, so
/// the chunks are fed to the session's own output gate (the one its DataReceived event is bound
/// to) and the replay is read back through the public event. Not covered here: the read loop
/// handing its chunks to that gate, which the process-backed bootstrap replay tests exercise.
/// </summary>
public sealed class BootstrapOverflowReplayTests
{
    private const string OutputFieldName = "_output";
    private const int OverflowingChunkExtraBytes = 10;
    private const int SmallChunkBytes = 2;
    private const char HeadFill = 'h';
    private const char OverflowFill = 'o';
    private const char TailFill = 't';

    [Fact]
    public void ConPty_OverflowThenSmallerChunk_ReplayIsAContiguousPrefix()
    {
        using ConPtySession session = new();
        OutputReplayGate gate = GateOf(session);

        FeedOverflowingStream(gate);
        StringBuilder replay = new();
        session.DataReceived += data => replay.Append(Encoding.UTF8.GetString(data.Span));

        Assert.Equal(ExpectedReplay(), replay.ToString());
    }

    [Fact]
    public void PipeMode_OverflowThenSmallerChunk_ReplayIsAContiguousPrefix()
    {
        using PipeModeSession session = new();
        OutputReplayGate gate = GateOf(session);

        FeedOverflowingStream(gate);
        StringBuilder replay = new();
        session.DataReceived += data => replay.Append(Encoding.UTF8.GetString(data.Span));

        Assert.Equal(ExpectedReplay(), replay.ToString());
    }

    // A chunk that fits with room for a small one, a chunk that crosses the cap, then a small chunk
    // that would still fit: a buffer that kept appending would replay the small chunk after a hole.
    private static void FeedOverflowingStream(OutputReplayGate gate)
    {
        gate.Deliver(Chunk(HeadFill, AppConstants.MaxConPtyBootstrapBufferBytes - SmallChunkBytes));
        gate.Deliver(Chunk(OverflowFill, SmallChunkBytes + OverflowingChunkExtraBytes));
        gate.Deliver(Chunk(TailFill, SmallChunkBytes));
    }

    private static string ExpectedReplay() =>
        new(HeadFill, AppConstants.MaxConPtyBootstrapBufferBytes - SmallChunkBytes);

    private static OutputReplayGate GateOf(object session)
    {
        FieldInfo field = session.GetType().GetField(OutputFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{session.GetType().Name} has no {OutputFieldName} field.");
        return (OutputReplayGate)field.GetValue(session)!;
    }

    private static byte[] Chunk(char fill, int length) => Encoding.ASCII.GetBytes(new string(fill, length));
}
