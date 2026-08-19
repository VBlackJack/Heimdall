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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

public sealed class RdpActiveXHostTests
{
    [Fact]
    public void StripScrollbarBits_RemovesOnlyNativeScrollbarStyles()
    {
        const long style = unchecked((long)0x8000_0000_0030_1234UL);
        const long expected = unchecked((long)0x8000_0000_0000_1234UL);

        var actual = RdpActiveXHost.StripScrollbarBits(style);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StripScrollbarBits_LeavesStyleWithoutScrollbarBitsUnchanged()
    {
        const long style = 0x0000_0000_0000_1234L;

        var actual = RdpActiveXHost.StripScrollbarBits(style);

        Assert.Equal(style, actual);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void CanAttemptResolutionUpdate_ReturnsExpectedResult(
        bool disposed,
        bool isConnected,
        bool expected)
    {
        bool actual = RdpActiveXHost.CanAttemptResolutionUpdate(disposed, isConnected);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0, "NoInfo")]
    [InlineData(3, "AdminDisconnect")]
    [InlineData(260, "DnsLookupFailed")]
    [InlineData(1800, "ConsoleSessionInProgress")]
    [InlineData(2055, "BadCredentials")]
    [InlineData(2308, "SocketClosed")]
    [InlineData(3848, "CredSspPolicyError")]
    [InlineData(4360, "ResolutionChangeTimeout")]
    public void GetDisconnectReasonKey_KnownCode_ReturnsKey(int reason, string expected)
    {
        string? actual = RdpActiveXHost.GetDisconnectReasonKey(reason);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(9999)]
    [InlineData(-1)]
    public void GetDisconnectReasonKey_UnknownCode_ReturnsNull(int reason)
    {
        string? actual = RdpActiveXHost.GetDisconnectReasonKey(reason);

        Assert.Null(actual);
    }

    [Theory]
    [InlineData(1800, "RDP_CONSOLE_SESSION_IN_PROGRESS · 1800")]
    [InlineData(2308, "RDP_SOCKET_CLOSED · 2308")]
    [InlineData(3848, "RDP_CRED_SSP_POLICY_ERROR · 3848")]
    [InlineData(0, "RDP_NO_INFO · 0")]
    public void FormatDisconnectCode_KnownCode_ReturnsSymbolicCode(int reason, string expected)
    {
        string actual = RdpActiveXHost.FormatDisconnectCode(reason);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatDisconnectCode_UnknownCode_ReturnsUnknownSymbolicCode()
    {
        string actual = RdpActiveXHost.FormatDisconnectCode(9999);

        Assert.Equal("RDP_UNKNOWN · 9999", actual);
    }

    [Theory]
    [InlineData(260)]
    [InlineData(264)]
    [InlineData(516)]
    [InlineData(772)]
    [InlineData(2308)]
    [InlineData(3080)]
    [InlineData(4360)]
    public void GetDisconnectSeverity_TransientCode_ReturnsTransient(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.Transient, actual);
    }

    [Theory]
    [InlineData(2055)]
    [InlineData(2567)]
    [InlineData(3335)]
    [InlineData(3591)]
    [InlineData(3847)]
    public void GetDisconnectSeverity_AuthIssueCode_ReturnsAuthIssue(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.AuthIssue, actual);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(1800)]
    [InlineData(2825)]
    [InlineData(9999)]
    public void GetDisconnectSeverity_TerminalCode_ReturnsTerminalError(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }

    [Theory]
    [InlineData(260)]
    [InlineData(264)]
    [InlineData(516)]
    [InlineData(772)]
    [InlineData(2308)]
    [InlineData(3080)]
    [InlineData(4360)]
    public void AllowsAutoReconnect_TransientCode_ReturnsTrue(int reason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason);

        Assert.True(actual);
    }

    [Theory]
    [InlineData(2308, 4)]
    [InlineData(2308, 7)]
    [InlineData(2308, 8)]
    [InlineData(2308, 9)]
    [InlineData(2308, 10)]
    public void GetDisconnectSeverity_AuthExtendedReason_ReturnsAuthIssue(
        int reason,
        int extendedReason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual =
            RdpActiveXHost.GetDisconnectSeverity(reason, extendedReason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.AuthIssue, actual);
    }

    [Theory]
    [InlineData(2308, 9)]
    [InlineData(2308, 7)]
    [InlineData(2308, 8)]
    [InlineData(2308, 10)]
    [InlineData(2308, 4)]
    public void AllowsAutoReconnect_AuthExtendedReason_ReturnsFalse(
        int reason,
        int extendedReason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason, extendedReason);

        Assert.False(actual);
    }

    [Theory]
    [InlineData(2308, 256)]
    [InlineData(2308, 260)]
    [InlineData(2308, 265)]
    [InlineData(2308, 266)]
    [InlineData(2308, 267)]
    public void GetDisconnectSeverity_LicenseExtendedReason_ReturnsTerminalError(
        int reason,
        int extendedReason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual =
            RdpActiveXHost.GetDisconnectSeverity(reason, extendedReason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }

    [Theory]
    [InlineData(2308, 256)]
    [InlineData(2308, 260)]
    [InlineData(2308, 265)]
    [InlineData(2308, 266)]
    [InlineData(2308, 267)]
    public void AllowsAutoReconnect_LicenseExtendedReason_ReturnsFalse(
        int reason,
        int extendedReason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason, extendedReason);

        Assert.False(actual);
    }

    // The extended reason for a rejected credential exchange. It is raised by the client's own
    // security layer, so it can accompany any high-level reason, including the network-class ones
    // the auto-reconnect veto sees.
    [Theory]
    [InlineData(260)]
    [InlineData(264)]
    [InlineData(516)]
    [InlineData(772)]
    [InlineData(2308)]
    [InlineData(3080)]
    [InlineData(4360)]
    public void GetDisconnectSeverity_InvalidCredentialsExtendedReason_ReturnsAuthIssue(int reason)
    {
        // Every row is a reason the single-argument mapping calls transient, so the assertion below
        // can only hold because the extended reason decided it.
        Assert.Equal(
            RdpActiveXHost.RdpDisconnectSeverity.Transient,
            RdpActiveXHost.GetDisconnectSeverity(reason));

        RdpActiveXHost.RdpDisconnectSeverity actual =
            RdpActiveXHost.GetDisconnectSeverity(reason, 768);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.AuthIssue, actual);
    }

    // The fail-open this closes: the veto on the control's own auto-reconnect used to ignore the
    // extended reason entirely, so a transient-looking high-level reason let the reconnect proceed
    // while the credentials were being refused.
    [Theory]
    [InlineData(260)]
    [InlineData(264)]
    [InlineData(516)]
    [InlineData(772)]
    [InlineData(2308)]
    [InlineData(3080)]
    [InlineData(4360)]
    public void AllowsAutoReconnect_InvalidCredentialsExtendedReason_ReturnsFalse(int reason)
    {
        Assert.True(RdpActiveXHost.AllowsAutoReconnect(reason));

        Assert.False(RdpActiveXHost.AllowsAutoReconnect(reason, 768));
    }

    [Theory]
    [InlineData(768, "BadCredentials")]
    [InlineData(266, "LicenseError")]
    [InlineData(267, "LicenseError")]
    [InlineData(265, "LicenseError")]
    [InlineData(9, "BadCredentials")]
    [InlineData(4, "ServerLogonTimeout")]
    public void GetExtendedDisconnectReasonKey_MapsTheDecodedReasons(
        int extendedReason,
        string expectedKey)
    {
        Assert.Equal(expectedKey, RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason));
    }

    // The decoder stays silent on what it does not know, so the caller can fall back to the
    // high-level reason instead of being handed a wrong label.
    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(268)]
    [InlineData(4096)]
    public void GetExtendedDisconnectReasonKey_UnmappedReason_ReturnsNull(int extendedReason)
    {
        Assert.Null(RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason));
    }

    [Fact]
    public void GetDisconnectSeverity_NoExtendedInfo_PreservesSocketClosedAsTransient()
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(2308, 0);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.Transient, actual);
    }

    [Fact]
    public void AllowsAutoReconnect_NoExtendedInfo_PreservesSocketClosedRetry()
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(2308, 0);

        Assert.True(actual);
    }

    [Fact]
    public void GetDisconnectSeverity_NoExtendedInfo_PreservesBadCredentialsAsAuthIssue()
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(2055, 0);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.AuthIssue, actual);
    }

    [Fact]
    public void GetDisconnectSeverity_NoExtendedInfo_PreservesConnectionTimeoutAsTransient()
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(264, 0);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.Transient, actual);
    }

    [Fact]
    public void GetDisconnectSeverity_NoExtendedInfo_PreservesConsoleSessionInProgressAsTerminalError()
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(1800, 0);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }

    [Theory]
    [InlineData(2055)]
    [InlineData(2567)]
    [InlineData(3335)]
    [InlineData(3591)]
    [InlineData(3847)]
    public void AllowsAutoReconnect_AuthIssueCode_ReturnsFalse(int reason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason);

        Assert.False(actual);
    }

    [Theory]
    [InlineData(1030)]
    [InlineData(1796)]
    [InlineData(1800)]
    [InlineData(2056)]
    [InlineData(2311)]
    [InlineData(2822)]
    [InlineData(3848)]
    public void AllowsAutoReconnect_SecurityOrTerminalCode_ReturnsFalse(int reason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason);

        Assert.False(actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(999999)]
    public void AllowsAutoReconnect_CleanExitOrUnknownCode_ReturnsFalse(int reason)
    {
        bool actual = RdpActiveXHost.AllowsAutoReconnect(reason);

        Assert.False(actual);
    }

    [Fact]
    public void PostConnectStripTimer_BeginStartsTicksAndStopsAfterMaxDuration()
    {
        var clock = new FakeClock();
        var timers = new List<FakeStripTimer>();
        var stripCount = 0;
        var logs = new List<string>();
        var timer = new RdpPostConnectStripTimer(
            () =>
            {
                var fake = new FakeStripTimer();
                timers.Add(fake);
                return fake;
            },
            clock,
            () => stripCount++,
            logs.Add,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(750));

        timer.Begin("test");

        Assert.True(timer.IsRunning);
        Assert.Single(timers);
        Assert.True(timers[0].Started);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        timers[0].RaiseTick();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        timers[0].RaiseTick();
        clock.Advance(TimeSpan.FromMilliseconds(250));
        timers[0].RaiseTick();

        Assert.Equal(3, stripCount);
        Assert.False(timer.IsRunning);
        Assert.True(timers[0].Stopped);
        Assert.True(timers[0].Disposed);
        Assert.Contains(logs, log => log.Contains("started", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Contains("max-duration", StringComparison.Ordinal));
    }

    [Fact]
    public void PostConnectStripTimer_DisposeStopsTimerCleanly()
    {
        var clock = new FakeClock();
        var fake = new FakeStripTimer();
        var timer = new RdpPostConnectStripTimer(
            () => fake,
            clock,
            () => { },
            _ => { },
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(750));

        timer.Begin("test");
        timer.Dispose();

        Assert.False(timer.IsRunning);
        Assert.True(fake.Stopped);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void PostConnectStripTimer_BeginTwiceDisposesPreviousTimer()
    {
        var clock = new FakeClock();
        var timers = new List<FakeStripTimer>();
        var timer = new RdpPostConnectStripTimer(
            () =>
            {
                var fake = new FakeStripTimer();
                timers.Add(fake);
                return fake;
            },
            clock,
            () => { },
            _ => { },
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(750));

        timer.Begin("first");
        timer.Begin("second");

        Assert.True(timer.IsRunning);
        Assert.Equal(2, timers.Count);
        Assert.True(timers[0].Stopped);
        Assert.True(timers[0].Disposed);
        Assert.True(timers[1].Started);
        Assert.False(timers[1].Disposed);
    }

    private sealed class FakeClock : IRdpPostConnectStripTimerClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-05-11T00:00:00Z");

        public void Advance(TimeSpan elapsed)
        {
            UtcNow += elapsed;
        }
    }

    private sealed class FakeStripTimer : IRdpStripTimer
    {
        public event EventHandler? Tick;

        public TimeSpan Interval { get; set; }

        public bool Started { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public void Start()
        {
            Started = true;
            Stopped = false;
        }

        public void Stop()
        {
            Stopped = true;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public void RaiseTick()
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
