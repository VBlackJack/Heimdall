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
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Tests;

/// <summary>
/// Unit tests for <see cref="RdpSessionEventFactory"/>, the pure builder behind the RDP event
/// seam. Producers (verified on live tip): connect emitted from
/// <c>EmbeddedRdpView.OnRdpConnected</c> (src/Heimdall.App/Views/EmbeddedRdpView.xaml.cs, right
/// after <c>_connectedAtUtc</c> is set); disconnect emitted from
/// <c>EmbeddedRdpView.OnRdpDisconnected</c> (after the watchdog-abandoned guard) and from the
/// auto-reconnect bounce in <c>OnRdpAutoReconnecting</c>; reconnect-success connect from
/// <c>OnRdpAutoReconnected</c>. Reason decode reuses the tested
/// <c>RdpActiveXHost.GetDisconnectReasonKey</c> / <c>GetExtendedDisconnectReasonKey</c>.
/// The reasonless teardown disconnect is emitted from the Dispose backstop
/// (<c>EmbeddedRdpView.EmitTeardownDisconnectEvent</c>, called at the top of <c>Dispose</c> before
/// <c>_disposed = true</c> around src/Heimdall.App/Views/EmbeddedRdpView.xaml.cs:473), since the COM
/// OnRdpDisconnected handler short-circuits on <c>_disposed</c> for a user-initiated tab close.
/// </summary>
public sealed class RdpSessionEventFactoryTests
{
    [Fact]
    public void BuildConnected_SetsProtocolHostTitle_AndNoDisconnectFields()
    {
        SessionEventRecord record = RdpSessionEventFactory.BuildConnected("host.example", "Prod RDP");

        record.Protocol.Should().Be("RDP");
        record.Kind.Should().Be(SessionEventKind.Connected);
        record.Host.Should().Be("host.example");
        record.Title.Should().Be("Prod RDP");
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
        record.DurationMs.Should().BeNull();
        record.EndTrigger.Should().BeNull();
    }

    [Fact]
    public void BuildDisconnected_SetsReasonKeyReasonCodeDuration_AndNullEndTrigger()
    {
        DateTime connectedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = connectedAt.AddSeconds(30);

        // 2055 = BadCredentials in RdpActiveXHost.GetDisconnectReasonKey.
        SessionEventRecord record = RdpSessionEventFactory.BuildDisconnected(
            "host.example",
            "Prod RDP",
            reason: 2055,
            extendedReason: RdpActiveXHost.NoExtendedDisconnectReason,
            connectedAtUtc: connectedAt,
            nowUtc: now);

        record.Protocol.Should().Be("RDP");
        record.Kind.Should().Be(SessionEventKind.Disconnected);
        record.ReasonKey.Should().Be("RDP_BAD_CREDENTIALS");
        record.ReasonCode.Should().Be(2055);
        record.DurationMs.Should().Be(30_000);
        record.EndTrigger.Should().BeNull();
    }

    [Theory]
    [InlineData(DisconnectReason.UserAction, "user")]
    [InlineData(DisconnectReason.TabClose, "teardown")]
    [InlineData(DisconnectReason.FailedSession, "teardown")]
    [InlineData(DisconnectReason.ReconnectInitiated, "teardown")]
    public void ResolveTeardownTrigger_MapsUserActionToUser_OthersToTeardown(
        DisconnectReason reason, string expected)
    {
        RdpSessionEventFactory.ResolveTeardownTrigger(reason).Should().Be(expected);
    }

    [Fact]
    public void BuildTeardownDisconnected_NonUserReason_HasNullReasonFields_TeardownTrigger_AndDuration()
    {
        DateTime connectedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = connectedAt.AddSeconds(75);

        SessionEventRecord record = RdpSessionEventFactory.BuildTeardownDisconnected(
            "host.example",
            "Prod RDP",
            DisconnectReason.TabClose,
            connectedAtUtc: connectedAt,
            nowUtc: now);

        record.Protocol.Should().Be("RDP");
        record.Kind.Should().Be(SessionEventKind.Disconnected);
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
        record.EndTrigger.Should().Be("teardown");
        record.DurationMs.Should().Be(75_000);
    }

    [Fact]
    public void BuildTeardownDisconnected_UserAction_TagsUserTrigger()
    {
        DateTime connectedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime now = connectedAt.AddSeconds(10);

        SessionEventRecord record = RdpSessionEventFactory.BuildTeardownDisconnected(
            "host.example", "Prod RDP", DisconnectReason.UserAction, connectedAt, now);

        record.EndTrigger.Should().Be("user");
        record.ReasonKey.Should().BeNull();
        record.ReasonCode.Should().BeNull();
        record.DurationMs.Should().Be(10_000);
    }

    [Fact]
    public void BuildTeardownDisconnected_DefaultConnectInstant_YieldsNullDuration()
    {
        // The backstop after a never-connected session would have a default timestamp; the latch
        // suppresses that case in the view, but the factory still reports null rather than a huge span.
        RdpSessionEventFactory.BuildTeardownDisconnected(
            "host.example", "Prod RDP", DisconnectReason.TabClose, default, DateTime.UtcNow)
            .DurationMs.Should().BeNull();
    }

    [Fact]
    public void ResolveReasonKey_UnknownPrimary_FallsBackToUnknownToken()
    {
        // 9999 is not a mapped primary code and NoInfo extended yields no key.
        RdpSessionEventFactory.ResolveReasonKey(9999, RdpActiveXHost.NoExtendedDisconnectReason)
            .Should().Be("RDP_UNKNOWN");
    }

    [Fact]
    public void ResolveDurationMs_DefaultConnectInstant_IsNull()
    {
        // A disconnect with no prior connect (failed connect) reports null duration, not a huge span.
        RdpSessionEventFactory.ResolveDurationMs(default, DateTime.UtcNow).Should().BeNull();
    }

    [Fact]
    public void ResolveDurationMs_NegativeSpan_ClampsToZero()
    {
        DateTime now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        RdpSessionEventFactory.ResolveDurationMs(now.AddSeconds(5), now).Should().Be(0);
    }

    [Theory]
    [InlineData("admin@10.0.0.5", "10.0.0.5")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void ResolveHost_StripsUserPrefix(string rawHost, string expected)
    {
        RdpSessionEventFactory.ResolveHost(rawHost, "Display").Should().Be(expected);
    }

    [Fact]
    public void ResolveHost_EmptyHost_FallsBackToDisplayName()
    {
        RdpSessionEventFactory.ResolveHost(rawHost: "  ", displayName: "Display Name")
            .Should().Be("Display Name");
    }
    /// <summary>
    /// The message on screen and the line in the log name the same cause.
    /// </summary>
    /// <remarks>
    /// <para>This is the defect the lot exists for. The two consumers composed the same pair of
    /// decoders in opposite orders, so for a disconnect where both decode, a support engineer
    /// correlating a user's screenshot against the event log read two different causes for one
    /// event. On (2308, 768) the overlay said the credentials were not accepted while the log
    /// persisted RDP_SOCKET_CLOSED.</para>
    /// <para>Asserted as an agreement between the two, not as two expected strings: expected
    /// strings would drift apart exactly the way the implementations did.</para>
    /// </remarks>
    [Theory]
    [InlineData(2308, 768)]
    [InlineData(2308, 9)]
    [InlineData(2308, 4)]
    [InlineData(2308, 257)]
    [InlineData(3335, 768)]
    [InlineData(3591, 7)]
    [InlineData(3847, 9)]
    [InlineData(2567, 9)]
    [InlineData(2055, 768)]
    [InlineData(516, 0)]
    [InlineData(3335, 0)]
    public void TheOverlayAndTheLogNameTheSameCause(int reason, int extendedReason)
    {
        SessionDiagnostic diagnostic = RdpHostDiagnosticFactory.FromDisconnect(reason, extendedReason);
        string loggedKey = RdpSessionEventFactory.ResolveReasonKey(reason, extendedReason);

        string overlaySuffix = diagnostic.MessageKey["RdpDisconnect".Length..];
        string expectedLoggedKey = $"RDP_{ToUpperSnake(overlaySuffix)}";

        Assert.Equal(expectedLoggedKey, loggedKey);
    }

    [Fact]
    public void TheLockedOutAccountIsNamedRatherThanCalledABadPassword()
    {
        // The half of the finding that is about message quality rather than agreement: a generic
        // credential rejection used to overwrite the primary code that says which account state
        // caused it, so a locked account was announced as "verify your username and password".
        Assert.Equal("RDP_ACCOUNT_LOCKED_OUT", RdpSessionEventFactory.ResolveReasonKey(3335, 768));

        SessionDiagnostic diagnostic = RdpHostDiagnosticFactory.FromDisconnect(3335, 768);
        Assert.Equal("RdpDisconnectAccountLockedOut", diagnostic.MessageKey);
    }

    [Fact]
    public void TheDisconnectRecordCarriesBothCodes()
    {
        // The key is resolved from both codes, so a reader given only the primary number could not
        // always re-derive it. Both are on the line.
        SessionEventRecord record = RdpSessionEventFactory.BuildDisconnected(
            "host.example.com",
            "Session",
            reason: 3335,
            extendedReason: 768,
            connectedAtUtc: DateTime.UtcNow.AddSeconds(-5),
            nowUtc: DateTime.UtcNow);

        Assert.Equal(3335, record.ReasonCode);
        Assert.Equal(768, record.ExtendedReasonCode);
        Assert.Equal("RDP_ACCOUNT_LOCKED_OUT", record.ReasonKey);
    }

    private static string ToUpperSnake(string value)
    {
        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

}
