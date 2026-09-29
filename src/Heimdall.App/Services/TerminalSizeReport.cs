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
/// The handoff of the terminal size from the page that measures it to the connect path that needs
/// it: the latest size reported, and a task that completes on the first report.
/// </summary>
/// <remarks>
/// <para>Written on the UI thread, from the page's <c>ready:</c> and <c>resize:</c> messages; read
/// from the connect path on a pool thread. The latest size is a reference swapped atomically. The
/// first report completes a <see cref="TaskCompletionSource{TResult}"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so completing it on the UI
/// thread never runs the connect path's continuation inline there.</para>
/// <para>The first report is what a fresh tab has to wait for: the view exists before the connect
/// starts, but its WebView2 page loads in parallel, and on the Plink path the launch used to reach
/// the size lookup before the page had spoken, every time.</para>
/// </remarks>
internal sealed class TerminalSizeReport
{
    private readonly TaskCompletionSource<TerminalSize> _firstReport =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TerminalSize? _latest;

    /// <summary>The last size reported, or <see langword="null"/> before the page has spoken.</summary>
    public TerminalSize? Latest => Volatile.Read(ref _latest);

    /// <summary>Completes with the first size reported, and never faults.</summary>
    public Task<TerminalSize> FirstReported => _firstReport.Task;

    /// <summary>Records a size reported by the page. Both dimensions must be positive.</summary>
    public void Remember(int columns, int rows)
    {
        TerminalSize size = new(columns, rows);
        Volatile.Write(ref _latest, size);
        _firstReport.TrySetResult(size);
    }

    /// <summary>
    /// The size to create the PTY at, waiting at most <paramref name="wait"/> for the page's first
    /// report when none has arrived yet.
    /// </summary>
    /// <param name="report">The connecting view's report, or <see langword="null"/> when no view is
    /// connecting for the session.</param>
    /// <param name="wait">The longest time to wait. Zero never waits and completes synchronously,
    /// which is what a transport that can resize after start asks for.</param>
    /// <param name="cancellationToken">The connection token; cancelling it ends the wait with
    /// <see cref="OperationCanceledException"/>.</param>
    public static async Task<TerminalSizeLookup> ResolveAsync(
        TerminalSizeReport? report,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        if (report is null)
        {
            return TerminalSizeLookup.NoView;
        }

        if (report.Latest is { } known)
        {
            return TerminalSizeLookup.Reported(known);
        }

        if (wait <= TimeSpan.Zero)
        {
            return TerminalSizeLookup.NotReportedYet;
        }

        try
        {
            await report.FirstReported.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return TerminalSizeLookup.TimedOut(wait);
        }

        // The latest rather than the first: a resize can land between the report and this line.
        return TerminalSizeLookup.Reported(report.Latest ?? report.FirstReported.Result);
    }
}

/// <summary>Why the connect path got the size it got.</summary>
internal enum TerminalSizeSource
{
    /// <summary>The page reported a size.</summary>
    Reported,

    /// <summary>No terminal view is connecting for the session.</summary>
    NoView,

    /// <summary>The page had not reported yet and the caller did not wait.</summary>
    NotReportedYet,

    /// <summary>The page did not report within the wait.</summary>
    TimedOut,
}

/// <summary>
/// The answer to a terminal size lookup: the reported size, or why there is none.
/// </summary>
internal sealed record TerminalSizeLookup(TerminalSize? Size, TerminalSizeSource Source, TimeSpan Waited)
{
    public static TerminalSizeLookup NoView { get; } = new(null, TerminalSizeSource.NoView, TimeSpan.Zero);

    public static TerminalSizeLookup NotReportedYet { get; } =
        new(null, TerminalSizeSource.NotReportedYet, TimeSpan.Zero);

    public static TerminalSizeLookup Reported(TerminalSize size) =>
        new(size, TerminalSizeSource.Reported, TimeSpan.Zero);

    public static TerminalSizeLookup TimedOut(TimeSpan waited) =>
        new(null, TerminalSizeSource.TimedOut, waited);
}
