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

namespace Heimdall.App.Tests;

/// <summary>
/// The stage timer wraps launch work without changing what it does: the result and the failure
/// of the stage pass through untouched, and the log line names the stage and its duration.
/// </summary>
public sealed class StartupStageTimerTests
{
    [Fact]
    public void TheLogLine_NamesTheStageAndItsDuration()
    {
        Assert.Equal(
            "[Startup] stage 'TwinShell EnsureCreated' took 42ms",
            StartupStageTimer.FormatLine("TwinShell EnsureCreated", 42));
    }

    [Fact]
    public async Task AStageWithAResult_ReturnsIt()
    {
        var result = await StartupStageTimer.RunAsync("count", () => Task.FromResult(527));

        Assert.Equal(527, result);
    }

    [Fact]
    public async Task AStageThatFails_StillFailsWithTheSameException()
    {
        var failure = new InvalidOperationException("stage failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartupStageTimer.RunAsync("failing", () => Task.FromException(failure)));

        Assert.Same(failure, thrown);
    }
}
