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

namespace Heimdall.App.Tests;

/// <summary>
/// What a Plink launch falls back on when its terminal view is absent or silent, and what counts as
/// a size report in the first place. The page and the view cannot run here: the view's handling of
/// <c>ready:</c> and <c>resize:</c> is exercised through the same parser and the same report it uses.
/// </summary>
public sealed class TerminalSizeHintsTests
{
    private const string PaneKey = "pane-session-1";
    private const int WaitMilliseconds = 100;
    private const int ReportedColumns = 100;
    private const int ReportedRows = 30;
    private const int PreviousColumns = 132;
    private const int PreviousRows = 41;
    private const int WindowColumns = 125;
    private const int WindowRows = 38;

    private static readonly TimeSpan ShortWait = TimeSpan.FromMilliseconds(WaitMilliseconds);

    private static void ApplyPagePayload(TerminalSizeReport report, string payload)
    {
        // The view's ready: and resize: branches, which remember a size only when the payload has one.
        if (TerminalSizeReport.TryParseSize(payload.AsSpan(), out int columns, out int rows))
        {
            report.Remember(columns, rows);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(",")]
    [InlineData("80,")]
    [InlineData(",24")]
    [InlineData("0,0")]
    [InlineData("NaN,NaN")]
    public void ReadyWithoutMeasurableSize_DoesNotCompleteFirstReport(string payload)
    {
        TerminalSizeReport report = new();

        ApplyPagePayload(report, payload);

        report.FirstReported.IsCompleted.Should().BeFalse();
        report.Latest.Should().BeNull();
    }

    [Fact]
    public async Task ReadyWithSize_CompletesFirstReport()
    {
        TerminalSizeReport report = new();

        ApplyPagePayload(report, $"{ReportedColumns},{ReportedRows}");

        report.FirstReported.IsCompleted.Should().BeTrue();
        (await report.FirstReported).Should().Be(new TerminalSize(ReportedColumns, ReportedRows));
    }

    [Fact]
    public void AResizeAfterAReadyWithoutSize_CompletesFirstReport()
    {
        TerminalSizeReport report = new();
        ApplyPagePayload(report, string.Empty);

        ApplyPagePayload(report, $"{TerminalSize.DefaultColumns},{TerminalSize.DefaultRows}");

        report.FirstReported.IsCompleted.Should().BeTrue();
        report.Latest.Should().Be(TerminalSize.Default);
    }

    [Fact]
    public async Task ResolveAsync_NoView_WithPreviousViewSize_ReturnsIt()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious(PaneKey, new TerminalSize(PreviousColumns, PreviousRows));
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, hints, PaneKey);

        lookup.Source.Should().Be(TerminalSizeSource.PreviousView);
        lookup.Size.Should().Be(new TerminalSize(PreviousColumns, PreviousRows));
    }

    [Fact]
    public async Task ResolveAsync_NoView_WithWindowSize_ReturnsIt()
    {
        TerminalSizeHints hints = new();
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, hints, PaneKey);

        lookup.Source.Should().Be(TerminalSizeSource.Window);
        lookup.Size.Should().Be(new TerminalSize(WindowColumns, WindowRows));
    }

    [Fact]
    public async Task ResolveAsync_TimedOut_FallsBackToWindowSize()
    {
        TerminalSizeHints hints = new();
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            new TerminalSizeReport(), ShortWait, CancellationToken.None, hints, PaneKey);

        lookup.Source.Should().Be(TerminalSizeSource.Window);
        lookup.Size.Should().Be(new TerminalSize(WindowColumns, WindowRows));
        lookup.Waited.Should().Be(ShortWait);
    }

    [Fact]
    public async Task ResolveAsync_NoHintAtAll_ReturnsDefault()
    {
        TerminalSizeLookup noView = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, new TerminalSizeHints(), PaneKey);
        TerminalSizeLookup timedOut = await TerminalSizeReport.ResolveAsync(
            new TerminalSizeReport(), ShortWait, CancellationToken.None, new TerminalSizeHints(), PaneKey);

        noView.Size.Should().BeNull();
        noView.Source.Should().Be(TerminalSizeSource.NoView);
        timedOut.Size.Should().BeNull();
        timedOut.Source.Should().Be(TerminalSizeSource.TimedOut);
    }

    [Fact]
    public async Task ResolveAsync_ReportedSize_WinsOverEveryHint()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious(PaneKey, new TerminalSize(PreviousColumns, PreviousRows));
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));
        TerminalSizeReport report = new();
        report.Remember(ReportedColumns, ReportedRows);

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            report, ShortWait, CancellationToken.None, hints, PaneKey);

        lookup.Source.Should().Be(TerminalSizeSource.Reported);
        lookup.Size.Should().Be(new TerminalSize(ReportedColumns, ReportedRows));
    }

    /// <summary>
    /// A zero wait is the transport that resizes after it attaches; it keeps its lookup and must not
    /// be handed a guess.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_ZeroWait_IgnoresEveryHint()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious(PaneKey, new TerminalSize(PreviousColumns, PreviousRows));
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            null, TimeSpan.Zero, CancellationToken.None, hints, PaneKey);

        lookup.Should().Be(TerminalSizeLookup.NoView);
    }

    [Fact]
    public void AViewThatReportsFeedsTheWindowHint()
    {
        TerminalSizeHints hints = new();
        TerminalSizeReport report = new() { Hints = hints };

        report.Remember(WindowColumns, WindowRows);

        hints.Window.Should().Be(new TerminalSize(WindowColumns, WindowRows));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(TerminalSizeHints.MinimumHintColumns - 1, 24)]
    [InlineData(80, TerminalSizeHints.MinimumHintRows - 1)]
    public void DegenerateSize_IsNeverKeptAsAHint(int columns, int rows)
    {
        TerminalSizeHints hints = new();
        hints.RememberWindow(new TerminalSize(WindowColumns, WindowRows));
        TerminalSize degenerate = new(columns, rows);

        hints.RememberWindow(degenerate);
        hints.RememberPrevious(PaneKey, degenerate);
        TerminalSizeReport report = new() { Hints = hints };
        report.Remember(columns, rows);

        hints.Window.Should().Be(new TerminalSize(WindowColumns, WindowRows));
        hints.PreviousFor(PaneKey).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_OnlyDegenerateHints_ReturnsDefault()
    {
        TerminalSizeHints hints = new();
        hints.RememberWindow(new TerminalSize(2, 1));
        hints.RememberPrevious(PaneKey, new TerminalSize(2, 1));

        TerminalSizeLookup lookup = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, hints, PaneKey);

        lookup.Size.Should().BeNull();
        lookup.Source.Should().Be(TerminalSizeSource.NoView);
    }

    [Fact]
    public async Task PreviousHint_IsConsumedOnUse()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious(PaneKey, new TerminalSize(PreviousColumns, PreviousRows));

        TerminalSizeLookup first = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, hints, PaneKey);
        TerminalSizeLookup second = await TerminalSizeReport.ResolveAsync(
            null, ShortWait, CancellationToken.None, hints, PaneKey);

        first.Source.Should().Be(TerminalSizeSource.PreviousView);
        second.Source.Should().Be(TerminalSizeSource.NoView);
    }

    [Fact]
    public void Cap_EvictsOldestOnly()
    {
        TerminalSizeHints hints = new();
        TerminalSize size = new(PreviousColumns, PreviousRows);
        for (int index = 0; index < TerminalSizeHints.MaxRememberedSessions; index++)
        {
            hints.RememberPrevious($"session-{index}", size);
        }

        hints.RememberPrevious("one-more", size);

        hints.PreviousFor("session-0").Should().BeNull();
        hints.PreviousFor("session-1").Should().Be(size);
        hints.PreviousFor($"session-{TerminalSizeHints.MaxRememberedSessions - 1}").Should().Be(size);
        hints.PreviousFor("one-more").Should().Be(size);
    }

    [Fact]
    public void ForgettingASession_DropsItsHint()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious(PaneKey, new TerminalSize(PreviousColumns, PreviousRows));

        hints.Forget(PaneKey);

        hints.PreviousFor(PaneKey).Should().BeNull();
    }

    [Fact]
    public void MovingAPreviousSize_HandsItOverAndForgetsTheSource()
    {
        TerminalSizeHints hints = new();
        hints.RememberPrevious("profile-1", new TerminalSize(PreviousColumns, PreviousRows));

        hints.MovePrevious("profile-1", PaneKey);

        hints.PreviousFor(PaneKey).Should().Be(new TerminalSize(PreviousColumns, PreviousRows));
        hints.PreviousFor("profile-1").Should().BeNull();
    }
}
