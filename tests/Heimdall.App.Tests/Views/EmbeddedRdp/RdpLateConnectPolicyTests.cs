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
using System.Text.RegularExpressions;
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes what happens to a connect that completes after the attempt was abandoned.
/// </summary>
/// <remarks>
/// <para>Cancel asks the control to disconnect, but a handshake already in flight can finish first,
/// so OnConnected can arrive afterwards. The watchdog's abort was guarded against exactly that. The
/// user's Cancel was not, and it is the same abort: the session came up live behind a cancelled
/// attempt, and the user-disconnect flag it had raised stayed raised, so the next genuine drop of
/// that live session was read as a user disconnect - no overlay, no diagnostic, a session dying in
/// silence.</para>
/// <para>Two tests: the decision itself, and that the handler actually asks for it. The second one
/// matters because a correct decision nothing consults is worth nothing.</para>
/// </remarks>
public sealed class RdpLateConnectPolicyTests
{
    [Fact]
    public void AConnectAbandonedByTheUserIsRefusedJustLikeOneAbandonedByTheWatchdog()
    {
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: false, abandonedByUser: true));
    }

    [Fact]
    public void AnAttemptNobodyAbandonedIsPromoted()
    {
        Assert.Equal(
            RdpLateConnectDecision.Promote,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: false, abandonedByUser: false));
    }

    [Fact]
    public void TheWatchdogAbortStaysRefused()
    {
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: true, abandonedByUser: false));
        Assert.Equal(
            RdpLateConnectDecision.Refuse,
            RdpLateConnectPolicy.Resolve(abandonedByWatchdog: true, abandonedByUser: true));
    }

    [Fact]
    public void TheConnectedHandlerConsultsBothAbandonmentLatches()
    {
        string handler = ViewSource.HandlerBody("private void OnRdpConnected()");

        Assert.Contains("RdpLateConnectPolicy.Resolve(", handler, StringComparison.Ordinal);
        Assert.Contains("_connectAbandonedByWatchdog", handler, StringComparison.Ordinal);
        Assert.Contains(
            "_connectAbandonedByUser",
            handler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheUserCancelRaisesTheLatchAndAFreshAttemptClearsIt()
    {
        string cancel = ViewSource.HandlerBody("private void OnCancelConnectClick");
        Assert.Contains("_connectAbandonedByUser = true;", cancel, StringComparison.Ordinal);

        string begin = ViewSource.HandlerBody("private void BeginConnect()");
        Assert.Contains("_connectAbandonedByUser = false;", begin, StringComparison.Ordinal);
    }
}
