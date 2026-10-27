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

using System.Windows.Threading;

namespace Heimdall.App.Services;

/// <summary>
/// Waits on the UI thread for a task, inside one nested dispatcher frame bounded by a budget.
/// </summary>
/// <remarks>
/// <para>For the synchronous handlers WPF gives the exit path - <c>OnExit</c> and
/// <c>SessionEnding</c> - which must not return before their asynchronous work is done: WPF
/// shuts the dispatcher down behind them, and the continuations still queued never run.
/// Blocking the thread instead would deadlock, since those continuations need the UI thread;
/// the nested frame keeps it pumping while the caller waits.</para>
/// <para>Single and bounded by construction. A wait already pumping on a thread refuses to
/// push another frame inside it - unbounded nested pumps once ended in a stack overflow - and
/// the frame is released when the task completes or the budget expires, whichever is first.</para>
/// </remarks>
internal static class BoundedDispatcherWait
{
    [ThreadStatic]
    private static bool t_pumping;

    /// <summary>
    /// Pumps <paramref name="dispatcher"/> until <paramref name="task"/> completes or
    /// <paramref name="budget"/> elapses. Must be called on the dispatcher's thread. Never
    /// throws on the wait itself; a fault of the task is the caller's to read.
    /// </summary>
    /// <returns>True when the task completed, in any state, before the wait ended.</returns>
    public static bool Wait(Dispatcher dispatcher, Task task, TimeSpan budget, Action<string> logWarn)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(logWarn);

        if (task.IsCompleted)
        {
            return true;
        }

        if (t_pumping)
        {
            logWarn("[App] a bounded dispatcher wait is already pumping on this thread; not nesting another.");
            return task.IsCompleted;
        }

        if (!dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
        {
            logWarn("[App] bounded dispatcher wait called off its dispatcher or after its shutdown; not pumping.");
            return task.IsCompleted;
        }

        var frame = new DispatcherFrame();

        // Both releases come from the thread pool and reach the frame at Send priority, the
        // highest there is. A DispatcherTimer would queue the deadline behind the very work
        // that overran it: under load a Background-priority tick may never come, and the
        // process would hang in this frame instead of exiting.
        void Release() => dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
        using var deadline = new System.Threading.Timer(_ => Release(), null, budget, Timeout.InfiniteTimeSpan);
        _ = task.ContinueWith(
            _ => Release(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        t_pumping = true;
        try
        {
            Dispatcher.PushFrame(frame);
        }
        catch (InvalidOperationException ex)
        {
            // Dispatcher processing suspended: nothing can be pumped from here.
            logWarn($"[App] bounded dispatcher wait could not pump: {ex.Message}");
        }
        finally
        {
            t_pumping = false;
        }

        return task.IsCompleted;
    }
}
