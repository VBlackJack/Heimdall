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

using System.Linq;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

public sealed class RdpActiveXHostTests
{
    /// <summary>
    /// The one place a disconnect is turned into a cause, so two consumers cannot disagree.
    /// </summary>
    /// <remarks>
    /// The extended code wins, because it is what the server said about the attempt. The single
    /// exception is a generic credential rejection meeting a primary code that names WHICH account
    /// state caused it: those two answers agree, and the specific one is worth more.
    /// </remarks>
    [Theory]
    // The extended code carries information the primary one does not.
    [InlineData(2308, 768, "BadCredentials", "a socket close the server explains as bad credentials")]
    [InlineData(2308, 4, "ServerLogonTimeout", "the extended code is the only one that knows")]
    [InlineData(2308, 257, "LicenseError", "licensing is never overridden by a primary code")]
    // A specific account state is not overwritten by a generic rejection.
    [InlineData(3335, 768, "AccountLockedOut", "locked out, not merely refused")]
    [InlineData(3591, 7, "AccountExpired", "expired, not merely refused")]
    [InlineData(3847, 9, "PasswordExpired", "password expired, not merely refused")]
    [InlineData(3335, 8, "AccountLockedOut", "every generic rejection yields the same way")]
    [InlineData(3591, 10, "AccountExpired", "every generic rejection yields the same way")]
    // ... but only against a GENERIC rejection.
    [InlineData(3335, 4, "ServerLogonTimeout", "a logon timeout is not a credential verdict")]
    [InlineData(3335, 256, "LicenseError", "a licensing failure is not a credential verdict")]
    // 2567 is deliberately not an account state: extended 9 says the server knew the principal.
    [InlineData(2567, 9, "BadCredentials", "user-not-found must not overrule server-knew-the-user")]
    [InlineData(2567, 768, "BadCredentials", "excluded uniformly, not case by case")]
    // 2055 decodes the same on both sides, so there is nothing to choose.
    [InlineData(2055, 768, "BadCredentials", "no-op member")]
    // With no extended information the primary code stands alone.
    [InlineData(3335, 0, "AccountLockedOut", "no extended information")]
    [InlineData(516, 0, "SocketConnectFailed", "no extended information")]
    public void ResolveDisconnectReasonKey_PrefersTheMoreInformativeCode(
        int reason,
        int extendedReason,
        string expected,
        string because)
    {
        _ = because;

        Assert.Equal(expected, RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason));
    }

    [Fact]
    public void ResolveDisconnectReasonKey_ReturnsNothingWhenNeitherCodeDecodes()
    {
        Assert.Null(RdpActiveXHost.ResolveDisconnectReasonKey(999_999, 0));
    }

    /// <summary>
    /// Every code the severity table calls an authentication issue is classified here, one way or
    /// the other.
    /// </summary>
    /// <remarks>
    /// The account-state set is deliberately NOT the severity arm - the two answer different
    /// questions - but it is drawn from the same population, so a code added to that arm without a
    /// verdict here would silently inherit "does not yield". This fails until someone decides.
    /// </remarks>
    [Fact]
    public void EveryAuthenticationIssueCodeHasAVerdictOnYielding()
    {
        int[] authenticationIssueCodes = [2055, 2567, 3335, 3591, 3847];
        int[] yields = [3335, 3591, 3847];
        int[] doesNotYield = [2055, 2567];

        foreach (int code in authenticationIssueCodes)
        {
            Assert.Equal(
                RdpActiveXHost.RdpDisconnectSeverity.AuthIssue,
                RdpActiveXHost.GetDisconnectSeverity(code));

            Assert.True(
                yields.Contains(code) ^ doesNotYield.Contains(code),
                $"Code {code} is an authentication issue with no verdict on whether it yields.");

            Assert.Equal(yields.Contains(code), RdpActiveXHost.NamesAnAccountState(code));
        }

        // Guards the guard: the severity arm is the population this is drawn from, so a code added
        // there and nowhere here has to be caught rather than skipped.
        foreach (int code in Enumerable.Range(0, 5000))
        {
            if (RdpActiveXHost.GetDisconnectSeverity(code) == RdpActiveXHost.RdpDisconnectSeverity.AuthIssue)
            {
                Assert.Contains(code, authenticationIssueCodes);
            }
        }
    }

    /// <summary>
    /// Choosing a message must not move the severity, which drives the auto-reconnect veto.
    /// </summary>
    [Theory]
    [InlineData(2308, 768)]
    [InlineData(3335, 768)]
    [InlineData(2567, 9)]
    [InlineData(3591, 4)]
    public void ResolvingAMessageLeavesTheSeverityAlone(int reason, int extendedReason)
    {
        RdpActiveXHost.RdpDisconnectSeverity before =
            RdpActiveXHost.GetDisconnectSeverity(reason, extendedReason);

        _ = RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason);

        Assert.Equal(before, RdpActiveXHost.GetDisconnectSeverity(reason, extendedReason));
    }

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
    [InlineData(4360, "ReconnectFailed")]
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
    [InlineData(1800, "RDP_CONSOLE_SESSION_IN_PROGRESS | 1800")]
    [InlineData(2308, "RDP_SOCKET_CLOSED | 2308")]
    [InlineData(3848, "RDP_CRED_SSP_POLICY_ERROR | 3848")]
    [InlineData(0, "RDP_NO_INFO | 0")]
    public void FormatDisconnectCode_KnownCode_ReturnsSymbolicCode(int reason, string expected)
    {
        string actual = RdpActiveXHost.FormatDisconnectCode(reason);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatDisconnectCode_UnknownCode_ReturnsUnknownSymbolicCode()
    {
        string actual = RdpActiveXHost.FormatDisconnectCode(9999);

        Assert.Equal("RDP_UNKNOWN | 9999", actual);
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

    // Three labels in the disconnect table named something the code does not mean. They are pinned
    // by value here, next to the neighbours they were confused with, so a future edit cannot swap
    // them back without saying so.
    [Theory]
    [InlineData(1796, "TimeoutOccurred")]
    [InlineData(264, "ConnectionTimeout")]
    [InlineData(2825, "NlaNotSupported")]
    [InlineData(3080, "ClientDecompressionFailed")]
    [InlineData(4360, "ReconnectFailed")]
    [InlineData(3592, "ReconnectFailed")]
    public void GetDisconnectReasonKey_CorrectedCodes_MapToTheirRealMeaning(int reason, string expected)
    {
        Assert.Equal(expected, RdpActiveXHost.GetDisconnectReasonKey(reason));
    }

    // The labels those three used to carry must not survive anywhere in the table, or the same
    // wrong meaning would simply move to another code.
    [Theory]
    [InlineData("InternalError")]
    [InlineData("DecompressionError")]
    [InlineData("ResolutionChangeTimeout")]
    public void GetDisconnectReasonKey_RetiredLabels_AreNoLongerProduced(string retired)
    {
        List<int> producers = [];
        for (int reason = 0; reason <= 10000; reason++)
        {
            if (string.Equals(RdpActiveXHost.GetDisconnectReasonKey(reason), retired, StringComparison.Ordinal))
            {
                producers.Add(reason);
            }
        }

        Assert.Empty(producers);
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
    // The overlay prints a symbolic cause beside the message. Deriving that name from the primary
    // reason alone printed one cause next to a message naming another: the primary code for a
    // gateway refusal is the socket close it produced, so a gateway timeout was labelled
    // RDP_SOCKET_CLOSED.
    [Theory]
    [InlineData(2308, 0x0300_0032, "RDP_RD_GATEWAY_TIMEOUT | 2308 | EXT 50331698")]
    [InlineData(2308, 0x0300_000C, "RDP_RD_GATEWAY_UNREACHABLE | 2308 | EXT 50331660")]
    [InlineData(2308, 0x0200_0001, "RDP_REMOTE_APP_ERROR | 2308 | EXT 33554433")]
    [InlineData(2308, 768, "RDP_BAD_CREDENTIALS | 2308 | EXT 768")]
    public void FormatDisconnectCode_WithExtendedReason_NamesTheResolvedCauseAndBothNumbers(
        int reason,
        int extendedReason,
        string expected)
    {
        Assert.Equal(expected, RdpActiveXHost.FormatDisconnectCode(reason, extendedReason));
    }

    // An extended reason that decodes to nothing still gets printed. It is the only specific thing
    // anyone has to go on, and withholding it is what sent readers to the log to find it.
    [Fact]
    public void FormatDisconnectCode_UndecodedExtendedReason_StillPrintsIt()
    {
        Assert.Equal(
            "RDP_SOCKET_CLOSED | 2308 | EXT 4096",
            RdpActiveXHost.FormatDisconnectCode(2308, 4096));
    }

    // Absent an extended reason the two overloads must be indistinguishable, because the one-argument
    // form is the whole of the previous behaviour and nothing about it was meant to change.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2055)]
    [InlineData(2308)]
    [InlineData(3592)]
    [InlineData(4360)]
    [InlineData(65535)]
    public void FormatDisconnectCode_NoExtendedReason_MatchesTheSingleArgumentForm(int reason)
    {
        Assert.Equal(
            RdpActiveXHost.FormatDisconnectCode(reason),
            RdpActiveXHost.FormatDisconnectCode(reason, RdpActiveXHost.NoExtendedDisconnectReason));
    }

    // The point of the change, stated as a property rather than as a list of examples: the symbolic
    // name is the resolved key and not a second opinion about it. Swept over both channels so an
    // arm added later cannot quietly reintroduce the divergence.
    [Fact]
    public void FormatDisconnectCode_SymbolicNameAlwaysMatchesTheResolvedKey()
    {
        int[] extendedProbes =
        [
            RdpActiveXHost.NoExtendedDisconnectReason, 4, 9, 265, 266, 267, 768, 4096,
            0x0300_0015, 0x0300_0003, 0x0300_000C, 0x0300_0032, 0x0300_4242,
            0x0200_0000, 0x0200_FFFF,
        ];

        int compared = 0;
        for (int reason = 0; reason <= ushort.MaxValue; reason++)
        {
            foreach (int extendedReason in extendedProbes)
            {
                string? key = RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason);
                if (key is null)
                {
                    continue;
                }

                string formatted = RdpActiveXHost.FormatDisconnectCode(reason, extendedReason);
                Assert.StartsWith("RDP_", formatted, StringComparison.Ordinal);
                Assert.DoesNotContain("RDP_UNKNOWN", formatted, StringComparison.Ordinal);
                compared++;
            }
        }

        // Guarding the guard: a sweep that resolved nothing would report success having compared
        // nothing at all.
        Assert.True(compared > 100, $"only {compared} pairs resolved, so the sweep proves nothing");
    }

    // The gateway block, the six values in it with an established meaning.
    [Theory]
    [InlineData(0x0300_0015, "RdGatewayCredentials")]
    [InlineData(0x0300_0003, "RdGatewayCertificate")]
    [InlineData(0x0300_0005, "RdGatewayCertificate")]
    [InlineData(0x0300_000C, "RdGatewayUnreachable")]
    [InlineData(0x0300_0032, "RdGatewayTimeout")]
    [InlineData(0x0300_0033, "RdGatewayTimeout")]
    public void GetExtendedDisconnectReasonKey_MapsTheNamedGatewayReasons(
        int extendedReason,
        string expectedKey)
    {
        Assert.Equal(expectedKey, RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason));
    }

    // A member of either block with no established meaning is named by its family rather than
    // left to the unknown-code message. Telling someone whose connection goes through a gateway
    // that the gateway is what refused them is the single most useful thing available here.
    [Theory]
    [InlineData(0x0300_0000, "RdGatewayError")]
    [InlineData(0x0300_4242, "RdGatewayError")]
    [InlineData(0x0300_FFFF, "RdGatewayError")]
    [InlineData(0x0200_0000, "RemoteAppError")]
    [InlineData(0x0200_0001, "RemoteAppError")]
    [InlineData(0x0200_FFFF, "RemoteAppError")]
    public void GetExtendedDisconnectReasonKey_UnnamedFamilyMember_NamesTheFamily(
        int extendedReason,
        string expectedKey)
    {
        Assert.Equal(expectedKey, RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason));
    }

    // Both families are bounded, and each bound is asserted one value past it. Widening either
    // range would start naming a gateway as the cause of a disconnect that has nothing to do
    // with one, which is exactly the kind of confident wrong answer the unknown-code message
    // exists to avoid.
    [Theory]
    [InlineData(0x01FF_FFFF)]
    [InlineData(0x0201_0000)]
    [InlineData(0x02FF_FFFF)]
    [InlineData(0x0301_0000)]
    public void GetExtendedDisconnectReasonKey_OutsideBothFamilies_ReturnsNull(int extendedReason)
    {
        Assert.Null(RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason));
    }

    // Primary reasons with a documented meaning that the decoder used to leave unnamed.
    [Theory]
    [InlineData(2823, "AccountDisabled")]
    [InlineData(3079, "TimeOfDayRestriction")]
    [InlineData(4617, "NlaRequired")]
    [InlineData(6151, "NoAuthenticationAuthority")]
    [InlineData(6919, "CertificateExpired")]
    [InlineData(7431, "ClockSkew")]
    public void GetDisconnectReasonKey_MapsTheNewlySourcedReasons(int reason, string expectedKey)
    {
        Assert.Equal(expectedKey, RdpActiveXHost.GetDisconnectReasonKey(reason));
    }

    // Deliberately left unmapped, and asserted so that a later reader does not complete the table
    // from a guess. 1032 is documented with contradictory meanings by its own vendor; the others
    // are reported in the wild with no established meaning at all. An invented label is worse than
    // the unknown-code message, which at least does not mislead.
    [Theory]
    [InlineData(1032)]
    [InlineData(2820)]
    [InlineData(1028)]
    [InlineData(2312)]
    [InlineData(4615)]
    [InlineData(4361)]
    public void GetDisconnectReasonKey_ReasonWithNoEstablishedMeaning_StaysUnmapped(int reason)
    {
        Assert.Null(RdpActiveXHost.GetDisconnectReasonKey(reason));
    }

    // This lot named disconnects; it did not re-decide any of them. Severity drives the
    // auto-reconnect veto, which is a different question from what the message says, and the only
    // risky direction would be making a code newly eligible for automatic retry.
    [Theory]
    [InlineData(2823)]
    [InlineData(3079)]
    [InlineData(4617)]
    [InlineData(6151)]
    [InlineData(6919)]
    [InlineData(7431)]
    public void GetDisconnectSeverity_NewlyNamedReasons_StayTerminalAndUnretried(int reason)
    {
        Assert.Equal(
            RdpActiveXHost.RdpDisconnectSeverity.TerminalError,
            RdpActiveXHost.GetDisconnectSeverity(reason));
        Assert.False(RdpActiveXHost.AllowsAutoReconnect(reason));
    }

    // Naming a family did not move the retry decision either: it still comes from the primary
    // reason, exactly as it did when the extended code decoded to nothing. The second assertion
    // is the one that matters, because it compares against the same call without the extended
    // code rather than against a value written here.
    [Theory]
    [InlineData(2308, 0x0300_0032, true)]
    [InlineData(2823, 0x0300_0032, false)]
    [InlineData(2308, 0x0200_0001, true)]
    public void AllowsAutoReconnect_FamilyNamedExtendedReason_StillFollowsThePrimary(
        int reason,
        int extendedReason,
        bool expected)
    {
        Assert.Equal(expected, RdpActiveXHost.AllowsAutoReconnect(reason, extendedReason));
        Assert.Equal(
            RdpActiveXHost.AllowsAutoReconnect(reason),
            RdpActiveXHost.AllowsAutoReconnect(reason, extendedReason));
    }

    // The resolver's rule is unchanged by the new families: the extended code wins, because it is
    // what the server said about the attempt, and a gateway refusing the attempt is not one of the
    // generic credential rejections that lose to a primary code naming an account state. A gateway
    // turns you away before the server ever reads the account, so the account state is stale.
    [Theory]
    [InlineData(2308, 0x0300_000C, "RdGatewayUnreachable")]
    [InlineData(2823, 0x0300_4242, "RdGatewayError")]
    [InlineData(3335, 0x0300_0015, "RdGatewayCredentials")]
    public void ResolveDisconnectReasonKey_FamilyNamedExtendedCode_WinsOverThePrimary(
        int reason,
        int extendedReason,
        string expected)
    {
        Assert.Equal(expected, RdpActiveXHost.ResolveDisconnectReasonKey(reason, extendedReason));
    }
}
