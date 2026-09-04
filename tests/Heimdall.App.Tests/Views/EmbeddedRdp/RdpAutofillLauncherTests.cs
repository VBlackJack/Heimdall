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

using System.Threading;
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the credential-dialog watcher does not start on the thread that asked for it.
/// </summary>
/// <remarks>
/// The watcher's first scan enumerates every visible top-level window, resolves a process name for
/// each one - a full process-table snapshot per window - and then walks this process's own threads.
/// Nothing is awaited before that, so a bare call runs scan one inline. The caller is the UI thread,
/// inside the render-priority operation that has just called Connect(), with the control's own
/// OnConnected callback queued behind it.
/// </remarks>
public sealed class RdpAutofillLauncherTests
{
    // What must not happen is the watcher's first scan running INSIDE the call, on the caller's
    // thread - that caller is the UI thread, mid render-priority operation, with the control's
    // OnConnected queued behind it.
    //
    // This asserted a different MANAGED THREAD ID instead, which is not the same property and is
    // not guaranteed. `Task.Run` queues the delegate, and the queue is served by the thread pool -
    // whose threads include the one the test itself is running on. Once the test awaits, that
    // thread goes back to the pool and may legitimately pick up the very work it queued. The
    // assertion held on a quiet machine and failed on a loaded CI runner, which is the behaviour
    // of a wrong oracle rather than of a slow one.
    //
    // Monitor.IsEntered is true only on a thread that holds the lock, and only while it holds it.
    // Read from inside the body it answers exactly the question: did this run synchronously,
    // inside the call, on the thread that made it. A pool thread that happens to carry the same
    // id later answers false, which is correct - that costs the UI thread nothing.
    [Fact]
    public async Task TheWatcherBodyDoesNotRunInsideTheCall()
    {
        object callerHeld = new();
        bool ranInsideTheCall = false;
        var entered = new TaskCompletionSource();

        Task started;
        lock (callerHeld)
        {
            started = RdpAutofillLauncher.StartAsync(
                _ =>
                {
                    ranInsideTheCall = Monitor.IsEntered(callerHeld);
                    entered.SetResult();
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        }

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await started.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(
            ranInsideTheCall,
            "The watcher ran synchronously inside StartAsync, on the caller's thread - which is "
                + "the UI thread, inside the render-priority operation that has just called "
                + "Connect(). Its first scan enumerates every visible window and snapshots the "
                + "process table for each one.");
    }

    [Fact]
    public async Task TheWatcherStillReceivesTheCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;

        await RdpAutofillLauncher.StartAsync(
            token =>
            {
                observed = token;
                return Task.CompletedTask;
            },
            cts.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public void TheAutofillStartRoutesThroughTheLauncher()
    {
        string starter = ViewSource.HandlerBody("private void StartCredentialAutofill");

        Assert.Contains("RdpAutofillLauncher.StartAsync(", starter, StringComparison.Ordinal);
    }
}
