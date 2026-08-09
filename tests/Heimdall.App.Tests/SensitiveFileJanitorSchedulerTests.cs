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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.Services.WinRm;

namespace Heimdall.App.Tests;

public sealed class SensitiveFileJanitorSchedulerTests
{
    private static readonly DateTime FixedUtcNow =
        new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RunAsync_WithoutNextEligible_RunsOneSweepWithoutDelay()
    {
        int sweepCount = 0;
        int delayCount = 0;
        SensitiveFileJanitorScheduler scheduler = new SensitiveFileJanitorScheduler(
            "TestJanitor",
            () =>
            {
                sweepCount++;
                return new SensitiveFileJanitorSweepResult(0, null);
            },
            (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            },
            () => FixedUtcNow);

        await scheduler.RunAsync(CancellationToken.None);

        Assert.Equal(1, sweepCount);
        Assert.Equal(0, delayCount);
    }

    [Fact]
    public async Task RunAsync_WithNextEligible_DelaysExactlyUntilDeadlineThenSweepsAgain()
    {
        DateTime currentUtc = FixedUtcNow;
        DateTime nextEligibleUtc = FixedUtcNow.AddMinutes(7);
        Queue<SensitiveFileJanitorSweepResult> results = new Queue<SensitiveFileJanitorSweepResult>(
            new SensitiveFileJanitorSweepResult[]
            {
                new SensitiveFileJanitorSweepResult(0, nextEligibleUtc),
                new SensitiveFileJanitorSweepResult(1, null),
            });
        List<TimeSpan> delays = new List<TimeSpan>();
        SensitiveFileJanitorScheduler scheduler = CreateScheduler(results, delays, () => currentUtc, delay =>
        {
            currentUtc += delay;
        });

        await scheduler.RunAsync(CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(new TimeSpan[] { TimeSpan.FromMinutes(7) }, delays);
    }

    [Fact]
    public async Task RunAsync_WithStrictlyLaterDeadline_ContinuesToThirdSweep()
    {
        DateTime currentUtc = FixedUtcNow;
        Queue<SensitiveFileJanitorSweepResult> results = new Queue<SensitiveFileJanitorSweepResult>(
            new SensitiveFileJanitorSweepResult[]
            {
                new SensitiveFileJanitorSweepResult(0, FixedUtcNow.AddMinutes(5)),
                new SensitiveFileJanitorSweepResult(1, FixedUtcNow.AddMinutes(9)),
                new SensitiveFileJanitorSweepResult(1, null),
            });
        List<TimeSpan> delays = new List<TimeSpan>();
        SensitiveFileJanitorScheduler scheduler = CreateScheduler(results, delays, () => currentUtc, delay =>
        {
            currentUtc += delay;
        });

        await scheduler.RunAsync(CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(
            new TimeSpan[] { TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4) },
            delays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunAsync_WithNonProgressingDeadline_StopsAfterSecondSweep(int deadlineOffsetMinutes)
    {
        int sweepCount = 0;
        DateTime firstDeadlineUtc = FixedUtcNow.AddMinutes(5);
        Queue<SensitiveFileJanitorSweepResult> results = new Queue<SensitiveFileJanitorSweepResult>(
            new SensitiveFileJanitorSweepResult[]
            {
                new SensitiveFileJanitorSweepResult(0, firstDeadlineUtc),
                new SensitiveFileJanitorSweepResult(0, firstDeadlineUtc.AddMinutes(deadlineOffsetMinutes)),
            });
        List<TimeSpan> delays = new List<TimeSpan>();
        SensitiveFileJanitorScheduler scheduler = new SensitiveFileJanitorScheduler(
            "TestJanitor",
            () =>
            {
                sweepCount++;
                return results.Dequeue();
            },
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            () => FixedUtcNow);

        await scheduler.RunAsync(CancellationToken.None);

        Assert.Equal(2, sweepCount);
        Assert.Empty(results);
        Assert.Equal(new TimeSpan[] { TimeSpan.FromMinutes(5) }, delays);
    }

    [Fact]
    public async Task RunAsync_WhenSweepThrows_CatchesFaultAndStops()
    {
        int sweepCount = 0;
        int delayCount = 0;
        SensitiveFileJanitorScheduler scheduler = new SensitiveFileJanitorScheduler(
            "ThrowingJanitor",
            () =>
            {
                sweepCount++;
                throw new IOException("sweep failed");
            },
            (_, _) =>
            {
                delayCount++;
                return Task.CompletedTask;
            },
            () => FixedUtcNow);

        Exception? exception = await Record.ExceptionAsync(
            () => scheduler.RunAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, sweepCount);
        Assert.Equal(0, delayCount);
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringDelay_StopsWithoutAnotherSweep()
    {
        using CancellationTokenSource cancellationSource = new CancellationTokenSource();
        int sweepCount = 0;
        SensitiveFileJanitorScheduler scheduler = new SensitiveFileJanitorScheduler(
            "TestJanitor",
            () =>
            {
                sweepCount++;
                return new SensitiveFileJanitorSweepResult(0, FixedUtcNow.AddMinutes(5));
            },
            (_, cancellationToken) =>
            {
                cancellationSource.Cancel();
                return Task.FromCanceled(cancellationToken);
            },
            () => FixedUtcNow);

        await scheduler.RunAsync(cancellationSource.Token);

        Assert.Equal(1, sweepCount);
    }

    [Fact]
    public async Task RunAsync_WhenUtcClockMovesBackward_StopsWithYoungFileRemaining()
    {
        const string scriptPath = @"C:\Temp\heimdall_winrm_clock_rollback.ps1";
        DateTime currentUtc = FixedUtcNow;
        DateTime lastWriteTimeUtc = FixedUtcNow.AddMinutes(-5);
        List<string> deleted = new List<string>();
        int sweepCount = 0;
        WinRmBootstrapJanitor janitor = new WinRmBootstrapJanitor(
            tempDirectory: () => @"C:\Temp",
            enumerateScripts: _ => new string[] { scriptPath },
            getLastWriteTimeUtc: _ => lastWriteTimeUtc,
            delete: path => deleted.Add(path),
            utcNow: () => currentUtc,
            maxAge: TimeSpan.FromMinutes(10));
        SensitiveFileJanitorScheduler scheduler = new SensitiveFileJanitorScheduler(
            nameof(WinRmBootstrapJanitor),
            () =>
            {
                sweepCount++;
                return janitor.SweepStale();
            },
            (_, _) =>
            {
                currentUtc = FixedUtcNow.AddMinutes(-10);
                return Task.CompletedTask;
            },
            () => currentUtc);

        await scheduler.RunAsync(CancellationToken.None);

        Assert.Equal(2, sweepCount);
        Assert.Empty(deleted);
    }

    private static SensitiveFileJanitorScheduler CreateScheduler(
        Queue<SensitiveFileJanitorSweepResult> results,
        List<TimeSpan> delays,
        Func<DateTime> utcNow,
        Action<TimeSpan> onDelay)
    {
        return new SensitiveFileJanitorScheduler(
            "TestJanitor",
            results.Dequeue,
            (delay, _) =>
            {
                delays.Add(delay);
                onDelay(delay);
                return Task.CompletedTask;
            },
            utcNow);
    }
}
