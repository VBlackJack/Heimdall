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

using System.Collections.Frozen;

namespace Heimdall.App.Services;

/// <summary>
/// Pure decision for whether a connect/disconnect EVENT should be recorded for a session,
/// given the global logging toggle and the session's connection type.
/// </summary>
/// <remarks>
/// This is the EVENT gate for the graphical protocols (RDP / VNC / Citrix), which have no
/// text stream and are logged as structured connect/disconnect events. It is deliberately
/// distinct from <see cref="SessionLogGatePolicy"/>, the TRANSCRIPT gate, which governs
/// auto-start of plain-text terminal transcripts for SSH / Telnet / Local Shell. The two
/// gates share the single global toggle but cover disjoint protocol sets.
/// </remarks>
public static class SessionEventGatePolicy
{
    /// <summary>
    /// Connection types whose lifecycle is recorded as structured events (graphical protocols).
    /// </summary>
    private static readonly FrozenSet<string> EventEligible =
        new[] { "RDP", "VNC", "CITRIX" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether connect/disconnect events should be logged for a session.
    /// </summary>
    /// <param name="sessionLoggingEnabled">The global <see cref="Heimdall.Core.Configuration.AppSettings.SessionLoggingEnabled"/> value.</param>
    /// <param name="connectionType">The session's connection type (e.g. <c>RDP</c>, <c>VNC</c>, <c>CITRIX</c>).</param>
    /// <returns><c>true</c> when events should be recorded; otherwise <c>false</c>.</returns>
    public static bool ShouldLog(bool sessionLoggingEnabled, string? connectionType)
    {
        if (!sessionLoggingEnabled || string.IsNullOrWhiteSpace(connectionType))
        {
            return false;
        }

        return EventEligible.Contains(connectionType);
    }
}
