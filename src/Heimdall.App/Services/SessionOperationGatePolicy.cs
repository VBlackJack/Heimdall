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
/// Pure decision for whether a file-transfer OPERATION should be recorded for a session, given the
/// global logging toggle and the session's connection type.
/// </summary>
/// <remarks>
/// This is the TRANSFER operations gate for the file-browser protocols (SFTP / FTP), whose
/// uploads / downloads / deletes / renames / mkdirs are logged as structured operation records. It is
/// deliberately distinct from <see cref="SessionLogGatePolicy"/>, the TRANSCRIPT gate for the text
/// terminals {SSH, TELNET, LOCAL}, and from <see cref="SessionEventGatePolicy"/>, the EVENT gate for
/// the graphical protocols {RDP, VNC, CITRIX}. All three gates share the single global toggle but
/// cover disjoint protocol sets.
/// </remarks>
public static class SessionOperationGatePolicy
{
    /// <summary>
    /// Connection types whose file operations are recorded in the operations log (transfer protocols).
    /// </summary>
    private static readonly FrozenSet<string> OperationEligible =
        new[] { "SFTP", "FTP" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Determines whether file-transfer operations should be logged for a session.
    /// </summary>
    /// <param name="sessionLoggingEnabled">The global <see cref="Heimdall.Core.Configuration.AppSettings.SessionLoggingEnabled"/> value.</param>
    /// <param name="connectionType">The session's connection type (e.g. <c>SFTP</c>, <c>FTP</c>).</param>
    /// <returns><c>true</c> when operations should be recorded; otherwise <c>false</c>.</returns>
    public static bool ShouldLog(bool sessionLoggingEnabled, string? connectionType)
    {
        if (!sessionLoggingEnabled || string.IsNullOrWhiteSpace(connectionType))
        {
            return false;
        }

        return OperationEligible.Contains(connectionType);
    }
}
