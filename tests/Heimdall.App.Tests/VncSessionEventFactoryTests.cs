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

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="VncSessionEventFactory"/>, the pure builder behind the VNC event
/// seam. Producers (verified on live tip, src/Heimdall.App/Views/EmbeddedVncView.xaml.cs): connect
/// from the "connected:" web message handler (right after <c>SessionConnected</c> is raised);
/// disconnect funnelled through one idempotent <c>EmitDisconnect</c> from the "disconnected:" web
/// message ("remote"), <c>OnDisconnectClick</c> ("user"), the "error:" web message ("remote"), and
/// the <c>Dispose</c> backstop ("teardown"). VNC carries no protocol reason code.
/// </summary>
public sealed class VncSessionEventFactoryTests
{
    [Fact]
    public void BuildConnected_SetsProtocolHostTitle_AndNoReasonOrDisconnectFields()
    {
        SessionEventRecord record = VncSessionEventFactory.BuildConnected("10.0.0.5", "ubuntu-desktop");

        record.Protocol.Should().Be("VNC");
        record.Kind.Should().Be(SessionEventKind.Connected);
        record.Host.Should().Be("10.0.0.5");
        record.Title.Should().Be("ubuntu-desktop");
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
        record.DurationMs.Should().BeNull();
        record.EndTrigger.Should().BeNull();
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("user")]
    [InlineData("teardown")]
    public void BuildDisconnected_PassesThroughEndTrigger_WithNullReasonFields(string endTrigger)
    {
        SessionEventRecord record = VncSessionEventFactory.BuildDisconnected(
            "10.0.0.5", "ubuntu-desktop", durationMs: 12_000, endTrigger);

        record.Protocol.Should().Be("VNC");
        record.Kind.Should().Be(SessionEventKind.Disconnected);
        record.DurationMs.Should().Be(12_000);
        record.EndTrigger.Should().Be(endTrigger);
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void BuildDisconnected_DefaultConnectInstant_YieldsNullDuration()
    {
        // A disconnect with no recorded connect (e.g. the teardown backstop after a never-connected
        // session) reports null duration via the shared helper, not a huge span.
        long? duration = GraphicalSessionEventHelpers.ResolveDurationMs(default, DateTime.UtcNow);

        SessionEventRecord record = VncSessionEventFactory.BuildDisconnected(
            "10.0.0.5", "ubuntu-desktop", duration, "teardown");

        record.DurationMs.Should().BeNull();
    }

    [Theory]
    [InlineData("admin@10.0.0.5", "10.0.0.5")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void BuildConnected_StripsUserPrefixFromHost(string rawHost, string expected)
    {
        VncSessionEventFactory.BuildConnected(rawHost, "title").Host.Should().Be(expected);
    }

    [Fact]
    public void BuildConnected_EmptyHost_FallsBackToTitle()
    {
        VncSessionEventFactory.BuildConnected(rawHost: "  ", title: "ubuntu-desktop")
            .Host.Should().Be("ubuntu-desktop");
    }
}
