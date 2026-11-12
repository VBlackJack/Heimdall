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
using Heimdall.Core.Terminal;

namespace Heimdall.Core.Tests.Terminal;

/// <summary>
/// The memory-shaped subscription of <see cref="OutputReplayGate"/>, used by the sessions whose
/// event carries <see cref="ReadOnlyMemory{T}"/>.
/// </summary>
public sealed class OutputReplayGateMemoryHandlerTests
{
    private const string Owner = "GateTests";
    private readonly StringBuilder _received = new();

    [Fact]
    public void AddMemoryThenRemoveMemory_MethodGroupHandler_StopsReceiving()
    {
        OutputReplayGate gate = new(Owner);
        gate.AddMemory(OnData);
        gate.Deliver(Bytes("one|"));

        // A method group creates a new delegate at each use: removal must match by equality.
        gate.RemoveMemory(OnData);
        gate.Deliver(Bytes("two"));

        Assert.Equal("one|", _received.ToString());
    }

    [Fact]
    public void AddMemory_FirstSubscriber_GetsTheReplay()
    {
        OutputReplayGate gate = new(Owner);
        gate.Deliver(Bytes("banner|"));
        gate.Deliver(Bytes("prompt"));

        gate.AddMemory(OnData);
        gate.Deliver(Bytes("!"));

        Assert.Equal("banner|prompt!", _received.ToString());
    }

    private void OnData(ReadOnlyMemory<byte> data) => _received.Append(Encoding.UTF8.GetString(data.Span));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
