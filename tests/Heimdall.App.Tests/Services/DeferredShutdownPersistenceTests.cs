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

using Heimdall.App.Services;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// The window state a logoff close skipped, handed to the exit tail. The SessionEnding
/// handler started it and left it, and it died with the process at its first yielding write.
/// </summary>
public sealed class DeferredShutdownPersistenceTests
{
    [Fact]
    public async Task RunAsync_RunsTheDeferredPersistenceOnce()
    {
        var deferred = new DeferredShutdownPersistence();
        int runs = 0;
        deferred.Defer(() =>
        {
            runs++;
            return Task.CompletedTask;
        });

        await deferred.RunAsync();
        await deferred.RunAsync();

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task RunAsync_NothingDeferred_CompletesAtOnce()
    {
        Task run = new DeferredShutdownPersistence().RunAsync();

        Assert.True(run.IsCompletedSuccessfully);
        await run;
    }

    [Fact]
    public async Task RunAsync_LaterDeferralReplacesTheEarlierOne()
    {
        var deferred = new DeferredShutdownPersistence();
        string? ran = null;
        deferred.Defer(() =>
        {
            ran = "first";
            return Task.CompletedTask;
        });
        deferred.Defer(() =>
        {
            ran = "second";
            return Task.CompletedTask;
        });

        await deferred.RunAsync();

        Assert.Equal("second", ran);
    }

    [Fact]
    public async Task RunAsync_SynchronousThrow_IsAFaultedTaskForTheBoundedStep()
    {
        var deferred = new DeferredShutdownPersistence();
        deferred.Defer(() => throw new InvalidOperationException("boom"));

        Task run = deferred.RunAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
    }
}
