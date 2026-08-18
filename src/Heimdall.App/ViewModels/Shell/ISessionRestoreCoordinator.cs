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

namespace Heimdall.App.ViewModels.Shell;

/// <summary>
/// The narrow surface a session restore needs from the server inventory that will replay it.
/// </summary>
/// <remarks>
/// The host is supplied per call rather than injected, so the coordinator holds no reference to
/// the inventory and no reference to the shell that owns it.
/// </remarks>
public interface ISessionRestoreHost
{
    /// <summary>
    /// The servers a snapshot entry can be matched against. Entries pointing outside this set
    /// are shown as unresolved by the restore dialog.
    /// </summary>
    IEnumerable<ServerItemViewModel> RestorableServers { get; }

    /// <summary>
    /// Reopens one server session. Returns <c>false</c> when the server is no longer in the
    /// inventory or the connection did not come up, and may throw when reopening it fails outright.
    /// </summary>
    Task<bool> RestoreServerAsync(string originalServerId, CancellationToken cancellationToken);
}

/// <summary>
/// Replays the previous run's session snapshot at launch: asks the user what to reopen, reopens it,
/// then tries once to delete the snapshot.
/// </summary>
/// <remarks>
/// This is the counterpart of <see cref="SessionSnapshotProjection"/>, which decides what gets
/// written at exit.
/// </remarks>
public interface ISessionRestoreCoordinator
{
    /// <summary>
    /// Runs the whole restore sequence. Does nothing when no snapshot is present or it holds no
    /// session. Once the user has answered - by declining or by having the sessions replayed -
    /// deletion of the snapshot is attempted exactly once; a snapshot the user never got to answer
    /// for is deliberately left in place.
    /// </summary>
    /// <remarks>
    /// <para>The attempt is not a guarantee of deletion, and nothing here promises exactly-once
    /// replay. <see cref="ISessionSnapshotService.ClearAsync"/> reports a delete it could not
    /// perform - a locked or unwritable file - as an ordinary completion, and a cancellation or a
    /// process exit between the replay and the delete leaves the file behind. In any of those cases
    /// the same sessions are offered again at the next launch, and sessions already reopened once
    /// can be reopened a second time. Detecting that would take a durable record of the replay,
    /// which this type does not keep.</para>
    /// <para>An individual session that fails to reopen is logged and skipped, so one unreachable
    /// server cannot cancel the sessions queued behind it. Cancellation is different: whether it is
    /// observed between sessions or raised by a reopen itself, it propagates, stops the replay, and
    /// is never counted as a session failure nor reported as a partial restore - and no deletion is
    /// attempted, so the sessions still owed are not discarded here.</para>
    /// </remarks>
    Task RestoreAsync(ISessionRestoreHost host, CancellationToken cancellationToken = default);
}
