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

    /// <summary>
    /// Where the size of every report is also noted, so a later connect that has no view of its own
    /// can fall back on what the window last showed. Set once, before the page can speak.
    /// </summary>
    internal TerminalSizeHints? Hints { get; set; }

    /// <summary>The last size reported, or <see langword="null"/> before the page has spoken.</summary>
    public TerminalSize? Latest => Volatile.Read(ref _latest);

    /// <summary>Completes with the first size reported, and never faults.</summary>
    public Task<TerminalSize> FirstReported => _firstReport.Task;

    /// <summary>Records a size reported by the page. Both dimensions must be positive.</summary>
    public void Remember(int columns, int rows)
    {
        TerminalSize size = new(columns, rows);
        Volatile.Write(ref _latest, size);
        Hints?.RememberWindow(size);
        _firstReport.TrySetResult(size);
    }

    /// <summary>
    /// Reads the <c>cols,rows</c> payload of a <c>ready:</c> or <c>resize:</c> page message.
    /// </summary>
    /// <remarks>
    /// A <c>ready:</c> posted before the page could measure its surface carries no size, and an
    /// empty or partial payload is refused here so that it never counts as the first report: a
    /// size that xterm.js made up (its built-in 80x24) would otherwise release a launch that is
    /// still waiting for a real one.
    /// </remarks>
    internal static bool TryParseSize(ReadOnlySpan<char> payload, out int columns, out int rows) =>
        TryParseSizeReport(payload, out columns, out rows, out _);

    /// <summary>The marker the page appends to the one report it measured with its surface shown.</summary>
    internal const string ShownMarker = "shown";

    /// <summary>
    /// Reads <c>cols,rows</c> or <c>cols,rows,shown</c>. The size is read exactly as
    /// <see cref="TryParseSize"/> always did; the third field, when present, must be the marker.
    /// </summary>
    internal static bool TryParseSizeReport(
        ReadOnlySpan<char> payload,
        out int columns,
        out int rows,
        out bool shownMarker)
    {
        columns = 0;
        rows = 0;
        shownMarker = false;

        int separatorIndex = payload.IndexOf(',');
        if (separatorIndex <= 0 || separatorIndex >= payload.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> rowsAndMarker = payload[(separatorIndex + 1)..];
        int markerIndex = rowsAndMarker.IndexOf(',');
        ReadOnlySpan<char> rowsText = markerIndex < 0 ? rowsAndMarker : rowsAndMarker[..markerIndex];
        if (markerIndex >= 0)
        {
            if (!rowsAndMarker[(markerIndex + 1)..].SequenceEqual(ShownMarker))
            {
                return false;
            }

            shownMarker = true;
        }

        return int.TryParse(payload[..separatorIndex], out columns)
            && int.TryParse(rowsText, out rows)
            && columns > 0
            && rows > 0;
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
    /// <param name="hints">Sizes seen elsewhere, used when the view is absent or silent. Never
    /// consulted for a zero wait: that is the transport that resizes after it attaches, and it
    /// keeps the lookup it always had.</param>
    /// <param name="sessionKey">The key the previous view of this pane or tab was remembered under.</param>
    public static async Task<TerminalSizeLookup> ResolveAsync(
        TerminalSizeReport? report,
        TimeSpan wait,
        CancellationToken cancellationToken,
        TerminalSizeHints? hints = null,
        string? sessionKey = null)
    {
        if (report is null)
        {
            return FromHints(TerminalSizeLookup.NoView, wait, hints, sessionKey);
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
            return FromHints(TerminalSizeLookup.TimedOut(wait), wait, hints, sessionKey);
        }

        // The latest rather than the first: a resize can land between the report and this line.
        return TerminalSizeLookup.Reported(report.Latest ?? report.FirstReported.Result);
    }

    /// <summary>
    /// The best size known without a report: the one the terminal that previously held this pane or
    /// tab showed, then the one the window last showed, else the lookup unchanged.
    /// </summary>
    /// <remarks>
    /// The previous view comes first because a reconnect lands in the same place, at the same size.
    /// The window comes second: a tab opened in the background shares the layout of the visible
    /// one, which is a far better guess than the 80 columns of a default.
    /// </remarks>
    private static TerminalSizeLookup FromHints(
        TerminalSizeLookup missing,
        TimeSpan wait,
        TerminalSizeHints? hints,
        string? sessionKey)
    {
        if (hints is null || wait <= TimeSpan.Zero)
        {
            return missing;
        }

        if (sessionKey is not null && hints.TakePrevious(sessionKey) is { } previous)
        {
            return new TerminalSizeLookup(previous, TerminalSizeSource.PreviousView, missing.Waited);
        }

        return hints.Window is { } window
            ? new TerminalSizeLookup(window, TerminalSizeSource.Window, missing.Waited)
            : missing;
    }
}

/// <summary>Why the connect path got the size it got.</summary>
internal enum TerminalSizeSource
{
    /// <summary>The page reported a size.</summary>
    Reported,

    /// <summary>The terminal that previously held this pane or tab last showed this size.</summary>
    PreviousView,

    /// <summary>The terminal most recently measured in the window showed this size.</summary>
    Window,

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
