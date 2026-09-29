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

using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;

namespace Heimdall.App.Tests;

/// <summary>
/// The handoff of the page's terminal size to the connect path, and the bounded wait for it.
/// </summary>
/// <remarks>
/// The real producer is <c>src/Heimdall.App/Assets/terminal.html:472</c>,
/// <c>postMessage('ready:' + term.cols + ',' + term.rows)</c>, posted at the end of
/// <c>initializeTerminal()</c> once xterm.js has opened and fitted its surface. The view receives
/// it in <c>EmbeddedSshView.OnWebMessageReceived</c> (the <c>MsgReady</c> branch,
/// <c>EmbeddedSshView.xaml.cs:1234</c>) and hands it to <see cref="TerminalSizeReport.Remember"/>
/// through <c>RememberTerminalSize</c> (line 1698). <see cref="TerminalSizeReport.Remember"/> is
/// called directly below in its place, from a pool thread where the real one runs on the UI thread.
/// </remarks>
public sealed class TerminalSizeReportTests
{
    private const int Columns = 132;
    private const int Rows = 43;

    /// <summary>Generous so a loaded machine cannot turn a delayed report into a timeout.</summary>
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ASizeAlreadyKnownResolvesImmediatelyWithoutWaiting()
    {
        TerminalSizeReport report = new();
        report.Remember(Columns, Rows);

        Task<TerminalSizeLookup> lookup = TerminalSizeReport.ResolveAsync(report, LongWait, CancellationToken.None);

        lookup.IsCompletedSuccessfully.Should().BeTrue();
        (await lookup).Should().Be(TerminalSizeLookup.Reported(new TerminalSize(Columns, Rows)));
    }

    [Fact]
    public async Task AZeroWaitNeverWaitsAndSaysTheSizeIsNotReportedYet()
    {
        Task<TerminalSizeLookup> lookup =
            TerminalSizeReport.ResolveAsync(new TerminalSizeReport(), TimeSpan.Zero, CancellationToken.None);

        lookup.IsCompletedSuccessfully.Should().BeTrue();
        TerminalSizeLookup resolved = await lookup;
        resolved.Source.Should().Be(TerminalSizeSource.NotReportedYet);
        resolved.Size.Should().BeNull();
    }

    [Fact]
    public async Task NoViewResolvesImmediatelyAndSaysSo()
    {
        Task<TerminalSizeLookup> lookup = TerminalSizeReport.ResolveAsync(null, LongWait, CancellationToken.None);

        lookup.IsCompletedSuccessfully.Should().BeTrue();
        (await lookup).Source.Should().Be(TerminalSizeSource.NoView);
    }

    [Fact]
    public async Task ASizeReportedAfterADelayShorterThanTheWaitIsUsed()
    {
        TerminalSizeReport report = new();

        Task<TerminalSizeLookup> lookup = TerminalSizeReport.ResolveAsync(report, LongWait, CancellationToken.None);
        lookup.IsCompleted.Should().BeFalse();

        await Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            report.Remember(Columns, Rows);
        });

        (await lookup).Should().Be(TerminalSizeLookup.Reported(new TerminalSize(Columns, Rows)));
    }

    [Fact]
    public async Task NoReportBeforeTheWaitEndsTimesOutWithoutASize()
    {
        TimeSpan wait = TimeSpan.FromMilliseconds(200);

        TerminalSizeLookup lookup =
            await TerminalSizeReport.ResolveAsync(new TerminalSizeReport(), wait, CancellationToken.None);

        lookup.Should().Be(TerminalSizeLookup.TimedOut(wait));
        SshHandler.DescribeMissingSize(lookup).Should().Contain("within 200 ms");
    }

    [Fact]
    public async Task CancellingTheWaitPropagates()
    {
        using CancellationTokenSource cancellation = new();
        Task<TerminalSizeLookup> lookup =
            TerminalSizeReport.ResolveAsync(new TerminalSizeReport(), LongWait, cancellation.Token);

        cancellation.Cancel();

        Func<Task> awaiting = () => lookup;
        await awaiting.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The first report completes on the UI thread in the product. The waiter's continuation must
    /// not run inline on the reporting thread, or the connect path would continue on the UI thread.
    /// </summary>
    [Fact]
    public async Task TheFirstReportDoesNotRunTheWaiterInlineOnTheReportingThread()
    {
        TerminalSizeReport report = new();
        TaskCompletionSource<int> continuationThread = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<int> reportingThread = new(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await report.FirstReported;
            continuationThread.TrySetResult(Environment.CurrentManagedThreadId);
        });
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // A dedicated background thread stands in for the UI thread. It is never joined: with an
        // inline continuation this test's own continuation could land on it, and joining itself
        // would hang instead of failing.
        Thread reporter = new(() =>
        {
            reportingThread.TrySetResult(Environment.CurrentManagedThreadId);
            report.Remember(Columns, Rows);
        })
        {
            IsBackground = true,
        };
        reporter.Start();

        int reporting = await reportingThread.Task.WaitAsync(LongWait);
        int continuation = await continuationThread.Task.WaitAsync(LongWait);

        continuation.Should().NotBe(reporting);
    }

    [Fact]
    public async Task TheLatestSizeWinsOverTheFirst()
    {
        TerminalSizeReport report = new();
        report.Remember(80, 24);
        report.Remember(Columns, Rows);

        report.Latest.Should().Be(new TerminalSize(Columns, Rows));
        (await report.FirstReported).Should().Be(new TerminalSize(80, 24));
    }
}
