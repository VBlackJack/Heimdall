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

using Heimdall.App.Services.SessionSnapshot;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// Selects which open sessions belong in the workspace snapshot, and in what order.
/// </summary>
/// <remarks>
/// What is excluded matters more than what is kept, because the snapshot is replayed by
/// <c>ServerList.RestoreServerAsync</c> on the next launch: a session with no server to point at
/// cannot be restored, and a tool tab is not a server at all, so restoring one would reopen the
/// wrong thing.
/// </remarks>
public static class SessionSnapshotProjection
{
    /// <summary>
    /// Projects the sessions worth restoring, most-recently-ordered last.
    /// </summary>
    public static IReadOnlyList<SessionSnapshotEntry> FromSessions(
        IEnumerable<SessionTabViewModel> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        return sessions
            .Where(session => !string.IsNullOrWhiteSpace(session.OriginalServerId))
            .Where(session => !string.IsNullOrWhiteSpace(session.ConnectionType))
            .Where(session => !ConnectionTypeCatalog.IsToolConnectionType(session.ConnectionType))

            // Numbered AFTER filtering, so the order is a gapless sequence over what is actually
            // stored. The restore path sorts on it, so what has to hold is the relative sequence;
            // numbering before the filters would leave holes that describe tabs the snapshot does
            // not contain.
            .Select((session, order) => new SessionSnapshotEntry
            {
                ServerId = session.OriginalServerId,
                ConnectionType = session.ConnectionType,
                Order = order,
            })
            .ToList();
    }
}
