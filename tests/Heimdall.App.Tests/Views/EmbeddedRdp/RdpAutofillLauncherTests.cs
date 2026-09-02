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
    [Fact]
    public async Task TheWatcherBodyDoesNotRunOnTheStartingThread()
    {
        int startingThread = Environment.CurrentManagedThreadId;
        int observedThread = 0;
        var entered = new TaskCompletionSource();

        Task started = RdpAutofillLauncher.StartAsync(
            _ =>
            {
                observedThread = Environment.CurrentManagedThreadId;
                entered.SetResult();
                return Task.CompletedTask;
            },
            CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await started.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(startingThread, observedThread);
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
