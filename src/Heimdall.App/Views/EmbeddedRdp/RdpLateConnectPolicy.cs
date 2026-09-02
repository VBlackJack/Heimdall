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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>What to do with an OnConnected that arrives after the attempt was abandoned.</summary>
internal enum RdpLateConnectDecision
{
    /// <summary>Promote the attempt to a live session.</summary>
    Promote,

    /// <summary>Refuse the promotion and hard-disconnect the control.</summary>
    Refuse,
}

/// <summary>
/// Decides whether a connect callback that arrives after the attempt was abandoned may still be
/// promoted to a live session.
/// </summary>
/// <remarks>
/// <para>Abandoning a connect asks the control to disconnect, but the handshake already in flight
/// can complete first, so OnConnected can arrive after the abort. Promoting it hands the user a
/// live session they asked to stop, and it leaves the user-disconnect flag raised on a session that
/// stays alive - the next genuine drop is then read as a user disconnect, so it gets no overlay and
/// no diagnostic.</para>
/// <para>The watchdog abort was already guarded. The user's Cancel was not, and both abandon the
/// same attempt the same way, so both belong in the same decision.</para>
/// </remarks>
internal static class RdpLateConnectPolicy
{
    internal static RdpLateConnectDecision Resolve(bool abandonedByWatchdog, bool abandonedByUser)
        => abandonedByWatchdog || abandonedByUser
            ? RdpLateConnectDecision.Refuse
            : RdpLateConnectDecision.Promote;
}
