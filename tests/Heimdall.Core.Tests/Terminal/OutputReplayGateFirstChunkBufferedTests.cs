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

using Heimdall.Core.Terminal;

namespace Heimdall.Core.Tests.Terminal;

/// <summary>
/// <see cref="OutputReplayGate.FirstChunkBuffered"/> completes only when a chunk is stored for
/// the first subscriber.
/// </summary>
public sealed class OutputReplayGateFirstChunkBufferedTests
{
    private const string Owner = "GateTests";
    private const int SmallCapBytes = 8;
    private const int ChunkBytes = 3;

    [Fact]
    public void FirstChunkBuffered_BeforeAnyDeliver_IsPending()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);

        Assert.False(gate.FirstChunkBuffered.IsCompleted);
    }

    [Fact]
    public void FirstChunkBuffered_AfterDeliverWithoutSubscriber_IsCompleted()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);

        gate.Deliver(new byte[ChunkBytes]);

        Assert.True(gate.FirstChunkBuffered.IsCompletedSuccessfully);
    }

    [Fact]
    public void FirstChunkBuffered_DeliverAfterSubscriberAdded_StaysPending()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);
        gate.Add(_ => { });

        gate.Deliver(new byte[ChunkBytes]);

        Assert.False(gate.FirstChunkBuffered.IsCompleted);
    }

    [Fact]
    public void FirstChunkBuffered_FirstChunkLargerThanCap_StaysPending()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);

        gate.Deliver(new byte[SmallCapBytes + 1]);

        Assert.False(gate.FirstChunkBuffered.IsCompleted);
    }

    [Fact]
    public void FirstChunkBuffered_DeliverAfterRelease_StaysPending()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);
        gate.Release();

        gate.Deliver(new byte[ChunkBytes]);

        Assert.False(gate.FirstChunkBuffered.IsCompleted);
    }

    [Fact]
    public void FirstChunkBuffered_SmallChunkAfterCapClosedTheBuffer_StaysPending()
    {
        OutputReplayGate gate = new(Owner, SmallCapBytes);
        gate.Deliver(new byte[SmallCapBytes + 1]);

        gate.Deliver(new byte[ChunkBytes]);

        Assert.False(gate.FirstChunkBuffered.IsCompleted);
    }
}
