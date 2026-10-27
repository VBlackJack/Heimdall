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

namespace Heimdall.App.Services;

/// <summary>
/// The window state persistence a shutdown-time close skipped, handed to the exit tail.
/// </summary>
/// <remarks>
/// At Windows logoff the main window's close pass returns before it saves anything, since
/// the application is already shutting down. The persistence was started from the
/// SessionEnding handler and left, and its first write that yielded died with the process:
/// the tree expand state and the window bounds were lost. Waiting there is not an option -
/// the handler runs inside WM_QUERYENDSESSION, an input-synchronous cross-process call - so
/// the close pass captures what it would have saved and the exit tail, which already waits
/// in a bounded frame, runs it.
/// </remarks>
internal sealed class DeferredShutdownPersistence
{
    private Func<Task>? _pending;

    /// <summary>Records the persistence to run at exit, replacing any recorded before.</summary>
    public void Defer(Func<Task> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        Volatile.Write(ref _pending, persist);
    }

    /// <summary>
    /// Runs the recorded persistence once, or completes at once when none was recorded. A
    /// synchronous throw is returned as a faulted task, for the bounded step to log.
    /// </summary>
    public Task RunAsync()
    {
        Func<Task>? persist = Interlocked.Exchange(ref _pending, null);
        if (persist is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            return persist();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }
}
