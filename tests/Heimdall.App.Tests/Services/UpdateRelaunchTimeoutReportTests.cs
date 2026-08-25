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

namespace Heimdall.App.Tests.Services;

/// <summary>
/// What a timed-out relauncher run is able to say about itself.
/// </summary>
/// <remarks>
/// BL-0067. On 2026-08-24 the blocking lane went red on two PowerShell 5.1 runs that each
/// exhausted the 60 s ceiling while a third passed in 7 s. The whole report was
/// <c>relauncher script exceeded 60 s under ...powershell.exe</c>: the two stream readers
/// were already draining the child, and every byte they held was thrown away with the
/// exception. A run that printed nothing and a run that printed its way to the last line
/// before stalling produced the identical message, so the occurrence could not be
/// characterised - and the item it blocks says explicitly not to paper over the flake with
/// retries or a longer ceiling. Reporting what is already known is the one move that makes
/// the next occurrence diagnosable without changing what is measured.
/// </remarks>
public sealed class UpdateRelaunchTimeoutReportTests
{
    private static readonly TimeSpan ShortCeiling = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task Drain_ReturnsWhatTheChildHadAlreadyWritten()
    {
        string captured = await UpdateRelaunchScriptExecutionTests.DrainAfterKillAsync(
            Task.FromResult("stage prepared\nverifying signature"),
            ShortCeiling);

        Assert.Equal("stage prepared\nverifying signature", captured);
    }

    [Fact]
    public async Task Drain_SaysSoWhenTheChildWroteNothing()
    {
        // "The script printed nothing" is a finding. It must not read the same as a
        // reader that could not be collected.
        string captured = await UpdateRelaunchScriptExecutionTests.DrainAfterKillAsync(
            Task.FromResult("   \n  "),
            ShortCeiling);

        Assert.Equal("<empty>", captured);
    }

    [Fact]
    public async Task Drain_BoundsItselfWhenTheReaderNeverCompletes()
    {
        // A timeout must not become a hang. The bound is what keeps this path from
        // replacing the failure it was meant to explain.
        TaskCompletionSource<string> never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string captured = await UpdateRelaunchScriptExecutionTests.DrainAfterKillAsync(
            never.Task,
            ShortCeiling);

        Assert.Contains("not readable", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("<empty>", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_ReportsItsOwnFailureRatherThanReplacingTheTimeout()
    {
        // This runs while a TimeoutException is being built. Throwing here would swap a
        // diagnosable report for a confusing one, so the secondary failure is described
        // in place of the text.
        string captured = await UpdateRelaunchScriptExecutionTests.DrainAfterKillAsync(
            Task.FromException<string>(new InvalidOperationException("pipe already closed")),
            ShortCeiling);

        Assert.Contains("unreadable", captured, StringComparison.Ordinal);
        Assert.Contains("pipe already closed", captured, StringComparison.Ordinal);
    }
}
