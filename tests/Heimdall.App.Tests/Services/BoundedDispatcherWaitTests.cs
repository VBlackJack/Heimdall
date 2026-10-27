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

using System.Diagnostics;
using System.Windows.Threading;
using Heimdall.App.Services;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// The bounded wait the synchronous exit handlers use, on a dispatcher thread of its own and
/// without any Window. The exit tail needs the UI thread for its continuations, so a wait
/// that blocked instead of pumping would never see it finish.
/// </summary>
public sealed class BoundedDispatcherWaitTests
{
    /// <summary>Far beyond what a pumped continuation takes; reached only by a broken wait.</summary>
    private static readonly TimeSpan GenerousBudget = TimeSpan.FromSeconds(5);

    /// <summary>The bound the budget test expects the wait to honour.</summary>
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(200);

    /// <summary>Resolution of the timer behind the budget, below which an early return is not a fault.</summary>
    private static readonly TimeSpan TimerResolution = TimeSpan.FromMilliseconds(30);

    /// <summary>Completes a task the wait under test would otherwise never see finish, so a broken bound fails instead of hanging.</summary>
    private static readonly TimeSpan SafetyCeiling = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ContinuationDelay = TimeSpan.FromMilliseconds(20);

    [Fact]
    public void Wait_TaskCompletingThroughDispatcherContinuations_FinishesInsideTheFrame()
    {
        StaDispatcherRunner.Run(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            int uiThread = Environment.CurrentManagedThreadId;
            int? resumedOn = null;

            async Task WorkResumingOnTheUiThread()
            {
                await Task.Delay(ContinuationDelay);
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                resumedOn = Environment.CurrentManagedThreadId;
            }

            var clock = Stopwatch.StartNew();
            bool completed = BoundedDispatcherWait.Wait(dispatcher, WorkResumingOnTheUiThread(), GenerousBudget, _ => { });

            Assert.True(completed, "a blocking wait never lets the UI-thread continuation run");
            Assert.True(clock.Elapsed < GenerousBudget, $"the wait took {clock.Elapsed}, the whole budget");
            Assert.Equal(uiThread, resumedOn);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Wait_NeverCompletingTask_ReturnsWhenTheBudgetExpires()
    {
        StaDispatcherRunner.Run(() =>
        {
            var neverInTime = new TaskCompletionSource();
            _ = Task.Delay(SafetyCeiling).ContinueWith(_ => neverInTime.TrySetResult(), TaskScheduler.Default);

            var clock = Stopwatch.StartNew();
            bool completed = BoundedDispatcherWait.Wait(Dispatcher.CurrentDispatcher, neverInTime.Task, ShortBudget, _ => { });

            Assert.False(completed, "the wait outlived its budget and saw the safety completion");
            Assert.True(clock.Elapsed >= ShortBudget - TimerResolution, $"the wait returned after {clock.Elapsed}, before its budget");
            Assert.True(clock.Elapsed < SafetyCeiling, $"the wait took {clock.Elapsed}");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Wait_CalledFromInsideAPumpingWait_RefusesToNestAnotherFrame()
    {
        StaDispatcherRunner.Run(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            var outer = new TaskCompletionSource();
            var innerNever = new TaskCompletionSource();
            _ = Task.Delay(SafetyCeiling).ContinueWith(_ => innerNever.TrySetResult(), TaskScheduler.Default);
            var warnings = new List<string>();
            bool? innerCompleted = null;
            TimeSpan innerElapsed = TimeSpan.Zero;

            dispatcher.BeginInvoke(() =>
            {
                var clock = Stopwatch.StartNew();
                innerCompleted = BoundedDispatcherWait.Wait(dispatcher, innerNever.Task, SafetyCeiling, warnings.Add);
                innerElapsed = clock.Elapsed;
                outer.SetResult();
            });

            bool outerCompleted = BoundedDispatcherWait.Wait(dispatcher, outer.Task, SafetyCeiling, _ => { });

            Assert.True(outerCompleted);
            Assert.False(innerCompleted, "the inner wait pumped a second nested frame");
            Assert.True(innerElapsed < GenerousBudget, $"the inner wait took {innerElapsed}");
            Assert.Single(warnings);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Wait_CompletedTask_ReturnsAtOnce()
    {
        StaDispatcherRunner.Run(() =>
        {
            Assert.True(BoundedDispatcherWait.Wait(Dispatcher.CurrentDispatcher, Task.CompletedTask, ShortBudget, _ => { }));
            return Task.CompletedTask;
        });
    }
}
