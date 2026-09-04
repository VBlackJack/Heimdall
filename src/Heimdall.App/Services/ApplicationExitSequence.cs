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

namespace Heimdall.App.Services;

/// <summary>
/// The first two steps of application exit: what to remember, then what to tear down.
/// </summary>
/// <remarks>
/// <para><b>The sessions are closed before anything is awaited, and that order is the point.</b>
/// <c>App.OnExit</c> is <c>async void</c>, and WPF calls it from inside
/// <c>Application.DoShutdown</c>, whose <c>finally</c> clears <c>Application.Current</c> and
/// <c>MainWindow</c> the moment the override returns - which, for an asynchronous override, is
/// its first incomplete await. Everything after that await runs in an application that no
/// longer exists. The snapshot save is such an await, and the session close used to follow it,
/// so every host was torn down with <c>Application.Current</c> null. The shipped logs show the
/// cost: "UI dispatcher is not available" from every connection state observer, and a
/// NullReferenceException from the one pane control still loaded, whose main-view-model lookup
/// dereferenced the cleared singleton.</para>
/// <para>The entries are captured before the close, because closing empties the collection they
/// are projected from; a capture taken afterwards would clear the snapshot instead of writing
/// it. A capture that throws skips the snapshot step altogether rather than clearing a file that
/// was fine.</para>
/// <para>A close that throws still gets the snapshot written: the two steps protect different
/// things, and neither failure is a reason to skip the other.</para>
/// </remarks>
internal static class ApplicationExitSequence
{
    public static async Task SaveSnapshotAndCloseSessionsAsync(
        Func<IReadOnlyList<SessionSnapshotEntry>> captureSessions,
        Action closeSessions,
        ISessionSnapshotService? snapshotService,
        TimeSpan snapshotBudget,
        Action<string> logWarn)
    {
        ArgumentNullException.ThrowIfNull(captureSessions);
        ArgumentNullException.ThrowIfNull(closeSessions);
        ArgumentNullException.ThrowIfNull(logWarn);

        IReadOnlyList<SessionSnapshotEntry>? sessions = null;
        if (snapshotService is not null)
        {
            try
            {
                sessions = captureSessions();
            }
            catch (Exception ex)
            {
                logWarn($"[App] session snapshot capture failed: {ex.Message}");
            }
        }

        // Synchronous, and before the first await below: see the type remarks.
        try
        {
            closeSessions();
        }
        catch (Exception ex)
        {
            logWarn($"[App] session cleanup: {ex.Message}");
        }

        if (snapshotService is null || sessions is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(snapshotBudget);
            if (sessions.Count > 0)
            {
                await snapshotService.SaveAsync(sessions, cts.Token);
            }
            else
            {
                await snapshotService.ClearAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logWarn("[App] session snapshot save timed out during shutdown.");
        }
        catch (Exception ex)
        {
            logWarn($"[App] session snapshot save failed: {ex.Message}");
        }
    }
}
