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

using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpDisconnectSeverityTests
{
    /// <summary>
    /// Heimdall's own connect timeout is painted the way the stack's connect timeout is painted.
    /// </summary>
    /// <remarks>
    /// One unreachable host, two ways of noticing it. If the control times out first, code 264
    /// arrives, is classified Transient, and the overlay shows the notice strip. If Heimdall's
    /// connect watchdog expires first - which for a black-holed port is the normal case - the
    /// diagnostic it builds carries no code, so the severity resolver used to fall through to
    /// TerminalError and the same condition was painted as a terminal failure. Which strip the
    /// user saw depended only on which timer won.
    /// </remarks>
    [Fact]
    public void ResolveOverlaySeverity_ForTheConnectWatchdogTimeout_MatchesTheStacksOwnTimeout()
    {
        RdpActiveXHost.RdpDisconnectSeverity watchdog = Heimdall.App.Views.EmbeddedRdpView.ResolveOverlaySeverity(
            RdpHostDiagnosticFactory.FromConnectTimeout(),
            RdpActiveXHost.NoExtendedDisconnectReason);

        RdpActiveXHost.RdpDisconnectSeverity stack = Heimdall.App.Views.EmbeddedRdpView.ResolveOverlaySeverity(
            RdpHostDiagnosticFactory.FromDisconnect(264),
            RdpActiveXHost.NoExtendedDisconnectReason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.Transient, stack);
        Assert.Equal(stack, watchdog);
    }

    /// <summary>
    /// Positive control: a code-less diagnostic that is not the connect timeout still resolves to
    /// the terminal default, so the case above is a special case and not a blanket demotion.
    /// </summary>
    [Fact]
    public void ResolveOverlaySeverity_ForAFatalError_StaysTerminal()
    {
        RdpActiveXHost.RdpDisconnectSeverity severity = Heimdall.App.Views.EmbeddedRdpView.ResolveOverlaySeverity(
            RdpHostDiagnosticFactory.FromFatalError(-2147467259),
            RdpActiveXHost.NoExtendedDisconnectReason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, severity);
    }

    [Theory]
    [InlineData(260)]
    [InlineData(264)]
    [InlineData(516)]
    [InlineData(772)]
    [InlineData(2308)]
    [InlineData(3080)]
    // 3592 carries the identical message to 4360 and used to fall through to TerminalError, so one
    // disconnect retried under one of its two codes and gave up under the other.
    [InlineData(3592)]
    [InlineData(4360)]
    public void GetDisconnectSeverity_MapsTransientCodes(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.Transient, actual);
    }

    /// <summary>
    /// Two codes that say the same thing to the user must be treated the same way by the retry
    /// policy.
    /// </summary>
    /// <remarks>
    /// <para>Severity drives two things a user sees: whether the client is allowed to keep
    /// reconnecting, and whether the overlay reads as a notice or as an error. The message comes
    /// from the decoder and the severity from a separate list, and nothing connected them, so one
    /// disconnect could retry twenty times under a blue notice with one of its codes and be torn
    /// down on the first bounce under a red error with the other, beneath a word-for-word identical
    /// sentence.</para>
    /// <para>This is the severity half of the guard already covering the overlay's primary action.
    /// The pairs are derived by sweeping the decoder rather than listed here, so a code added to an
    /// existing message arm is covered the day it is added.</para>
    /// </remarks>
    [Fact]
    public void CodesSharingAMessage_ShareASeverity()
    {
        Dictionary<string, List<int>> byMessage = [];
        for (int reason = 0; reason <= ushort.MaxValue; reason++)
        {
            string? key = RdpActiveXHost.GetDisconnectReasonKey(reason);
            if (key is null)
            {
                continue;
            }

            if (!byMessage.TryGetValue(key, out List<int>? codes))
            {
                codes = [];
                byMessage[key] = codes;
            }

            codes.Add(reason);
        }

        List<string> disagreements = [];
        int sharedMessages = 0;
        foreach ((string key, List<int> codes) in byMessage)
        {
            if (codes.Count < 2)
            {
                continue;
            }

            sharedMessages++;
            List<RdpActiveXHost.RdpDisconnectSeverity> severities =
                [.. codes.Select(static code => RdpActiveXHost.GetDisconnectSeverity(code))];
            if (severities.Distinct().Count() > 1)
            {
                disagreements.Add(
                    $"'{key}' is shown for codes {string.Join(", ", codes)} but their severities "
                        + $"are {string.Join(", ", severities)}");
            }
        }

        // Guarding the guard: with no message shared by two codes the loop compares nothing and
        // would still report success.
        Assert.True(sharedMessages > 0, "no message is shared by two codes, so nothing was compared");
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    // The retry decision follows from the severity, so the codes that share a message must also
    // agree on whether the client may keep reconnecting. Asserted separately because that is the
    // consequence a user actually lives with.
    [Fact]
    public void CodesSharingAMessage_AgreeOnAutoReconnect()
    {
        Assert.Equal(
            RdpActiveXHost.GetDisconnectReasonKey(3592),
            RdpActiveXHost.GetDisconnectReasonKey(4360));

        Assert.Equal(
            RdpActiveXHost.AllowsAutoReconnect(4360),
            RdpActiveXHost.AllowsAutoReconnect(3592));
    }

    [Theory]
    [InlineData(2055)]
    [InlineData(2567)]
    [InlineData(3335)]
    [InlineData(3591)]
    [InlineData(3847)]
    public void GetDisconnectSeverity_MapsAuthIssueCodes(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.AuthIssue, actual);
    }

    [Theory]
    [InlineData(262)]
    [InlineData(1030)]
    [InlineData(1796)]
    [InlineData(2056)]
    [InlineData(2311)]
    [InlineData(2825)]
    [InlineData(2822)]
    [InlineData(3848)]
    public void GetDisconnectSeverity_MapsTerminalErrorCodes(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }

    [Fact]
    public void GetDisconnectSeverity_MapsUnknownCodeToTerminalError()
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(9999);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GetDisconnectSeverity_MapsSuppressedCleanExitCodesToTerminalError(int reason)
    {
        RdpActiveXHost.RdpDisconnectSeverity actual = RdpActiveXHost.GetDisconnectSeverity(reason);

        Assert.Equal(RdpActiveXHost.RdpDisconnectSeverity.TerminalError, actual);
    }
}
