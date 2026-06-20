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

using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class TrySendCommandToSinkTests
{
    private sealed class FakeSink : ITerminalCommandSink
    {
        public List<string> Received { get; } = new();

        public void WriteCommand(string command) => Received.Add(command);
    }

    private static SessionPaneModel Leaf(object? hostControl) => new() { HostControl = hostControl };

    private static SplitContainerModel Split(ISplitContent first, ISplitContent second) => new()
    {
        First = first,
        Second = second,
        Orientation = SplitOrientation.Vertical
    };

    [Fact]
    public void TrySendCommandToFirstSink_SingleLeafWithSink_WritesAndReturnsTrue()
    {
        var sink = new FakeSink();
        var root = Leaf(sink);

        var result = EmbeddedSessionManager.TrySendCommandToFirstSink(root, "whoami");

        result.Should().BeTrue();
        sink.Received.Should().ContainSingle().Which.Should().Be("whoami");
    }

    [Fact]
    public void TrySendCommandToFirstSink_MultipleSinks_OnlyFirstReceives()
    {
        var first = new FakeSink();
        var second = new FakeSink();
        // Depth-first order: First child before Second child.
        var root = Split(Leaf(first), Leaf(second));

        var result = EmbeddedSessionManager.TrySendCommandToFirstSink(root, "ls -la");

        result.Should().BeTrue();
        first.Received.Should().ContainSingle().Which.Should().Be("ls -la");
        second.Received.Should().BeEmpty();
    }

    [Fact]
    public void TrySendCommandToFirstSink_NonSinkLeafIsSkipped()
    {
        var sink = new FakeSink();
        // First leaf hosts a plain object (e.g. an RDP surface) and is skipped;
        // the sink in the second leaf wins.
        var root = Split(Leaf(new object()), Leaf(sink));

        var result = EmbeddedSessionManager.TrySendCommandToFirstSink(root, "pwd");

        result.Should().BeTrue();
        sink.Received.Should().ContainSingle().Which.Should().Be("pwd");
    }

    [Fact]
    public void TrySendCommandToFirstSink_NullRoot_ReturnsFalseAndDoesNotThrow()
    {
        var act = () => EmbeddedSessionManager.TrySendCommandToFirstSink(null, "noop");

        act.Should().NotThrow();
        EmbeddedSessionManager.TrySendCommandToFirstSink(null, "noop").Should().BeFalse();
    }

    [Fact]
    public void TrySendCommandToFirstSink_NoSinkInTree_ReturnsFalse()
    {
        var root = Split(Leaf(new object()), Leaf(null));

        var result = EmbeddedSessionManager.TrySendCommandToFirstSink(root, "echo hi");

        result.Should().BeFalse();
    }

    [Fact]
    public void HasTerminalSink_TreeWithSink_ReturnsTrue()
    {
        // A non-terminal pane (e.g. RDP surface) plus one terminal sink.
        var root = Split(Leaf(new object()), Leaf(new FakeSink()));

        EmbeddedSessionManager.HasTerminalSink(root).Should().BeTrue();
    }

    [Fact]
    public void HasTerminalSink_NoSinkInTree_ReturnsFalse()
    {
        // Only non-terminal panes (RDP/VNC/SFTP) — nothing can receive a command.
        var root = Split(Leaf(new object()), Leaf(null));

        EmbeddedSessionManager.HasTerminalSink(root).Should().BeFalse();
    }

    [Fact]
    public void HasTerminalSink_NullRoot_ReturnsFalse()
    {
        EmbeddedSessionManager.HasTerminalSink(null).Should().BeFalse();
    }
}
