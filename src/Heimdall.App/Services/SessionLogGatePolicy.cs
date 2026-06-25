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
/// Pure decision for whether a session transcript should AUTO-START on connect, given the
/// global logging toggle and the session's connection type.
/// </summary>
public static class SessionLogGatePolicy
{
    /// <summary>
    /// Connection types whose output is an interactive text terminal eligible for auto-logging.
    /// </summary>
    /// <remarks>
    /// <c>WINRM</c> is intentionally excluded: it spawns a local PowerShell host whose credential
    /// bootstrap output can be replayed into the terminal stream, so auto-logging could capture
    /// sensitive material. WinRM transcripts remain manual-only until a later lot adds explicit
    /// bootstrap-output exclusion. The graphical (RDP/VNC/Citrix) and transfer (SFTP/FTP) protocols
    /// are not terminal sessions and never reach this gate.
    /// </remarks>
    private static readonly FrozenSet<string> AutoStartEligible =
        new[] { "SSH", "TELNET", "LOCAL" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether logging should auto-start for a freshly connected session.
    /// </summary>
    /// <param name="sessionLoggingEnabled">The global <see cref="Heimdall.Core.Configuration.AppSettings.SessionLoggingEnabled"/> value.</param>
    /// <param name="connectionType">The session's connection type (e.g. <c>SSH</c>, <c>LOCAL</c>, <c>WINRM</c>).</param>
    /// <returns><c>true</c> when the transcript should auto-start; otherwise <c>false</c>.</returns>
    public static bool ShouldAutoStart(bool sessionLoggingEnabled, string? connectionType)
    {
        if (!sessionLoggingEnabled || string.IsNullOrWhiteSpace(connectionType))
        {
            return false;
        }

        return AutoStartEligible.Contains(connectionType);
    }
}
