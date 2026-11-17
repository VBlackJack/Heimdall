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

using System.Globalization;
using FluentAssertions;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The box the host gives a hidden terminal page, and the check of what the page then measures.
/// The page script itself is covered by the hand test only: nothing here executes it.
/// </summary>
public sealed class TerminalViewportMessageTests
{
    private const double PaneLeft = 100.5;
    private const double PaneWidth = 553.5;
    private const double PaneHeight = 640.333;
    private const double UnitScale = 1.0;
    private const int HostDerivedColumns = 150;
    private const int HostDerivedRows = 40;
    private const int ShownColumns = 149;

    [Fact]
    public void ViewportMessage_FormatsDipAsInvariantCssPixels()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");

            // 545 px at 1.5 is 363.33... DIPs of device pixels: a fractional value in French culture.
            bool built = TerminalViewportMessage.TryBuild(
                0, 0, 545, PaneHeight, 1.5, 1.5, surfaceHidden: true, out string message);

            built.Should().BeTrue();
            message.Should().Be("viewport:545.33,640");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(double.NaN, 600)]
    [InlineData(800, double.PositiveInfinity)]
    public void ViewportMessage_NotBuiltWhileNotLaidOut(double width, double height)
    {
        TerminalViewportMessage.TryBuild(0, 0, width, height, UnitScale, UnitScale, surfaceHidden: true, out string message)
            .Should().BeFalse();
        message.Should().BeEmpty();
    }

    [Fact]
    public void ViewportMessage_BuiltOnlyWhileSurfaceHidden()
    {
        TerminalViewportMessage.TryBuild(PaneLeft, 0, PaneWidth, PaneHeight, UnitScale, UnitScale, surfaceHidden: false, out string shown)
            .Should().BeFalse();
        TerminalViewportMessage.TryBuild(PaneLeft, 0, PaneWidth, PaneHeight, UnitScale, UnitScale, surfaceHidden: true, out string hidden)
            .Should().BeTrue();

        shown.Should().BeEmpty();
        hidden.Should().StartWith(TerminalViewportMessage.Prefix);
        TerminalViewportMessage.Shown.Should().Be("viewport:");
    }

    /// <summary>
    /// The native window is sized from its rectangle with each edge rounded to a device pixel, so
    /// the width is round(right) - round(left), not round(width). Each row is a case where the two
    /// differ or may differ by one device pixel.
    /// </summary>
    [Theory]
    [InlineData(100.5, 553.5, 1.0, "viewport:553,600")]
    [InlineData(100.4, 553.5, 1.25, "viewport:552.8,600")]
    [InlineData(0.0, 545.0, 1.5, "viewport:545.33,600")]
    public void ViewportMessage_UsesEdgeRoundedDevicePixels(double left, double width, double scale, string expected)
    {
        TerminalViewportMessage.TryBuild(left, 0, width, 600, scale, scale, surfaceHidden: true, out string message)
            .Should().BeTrue();

        message.Should().Be(expected);
    }

    [Fact]
    public void ViewportMessage_DividesOutTheZoomFactor()
    {
        TerminalViewportMessage.TryBuild(0, 0, 800, 600, UnitScale, UnitScale, surfaceHidden: true, out string message, zoom: 2)
            .Should().BeTrue();

        message.Should().Be("viewport:400,300");
    }

    /// <summary>
    /// The page's first <c>resize:</c> after the host's <c>viewport:W,H</c> goes through the same
    /// report as before: it completes the first report, which is what the plink wait is waiting on.
    /// </summary>
    [Fact]
    public async Task ResizeAfterViewport_CompletesFirstReport_AndReleasesPlinkWait()
    {
        TerminalSizeReport report = new();
        Task<TerminalSizeLookup> lookup = TerminalSizeReport.ResolveAsync(
            report, TimeSpan.FromSeconds(30), CancellationToken.None);

        // "ready:" carries no size, and the page is silent until the host has spoken.
        TerminalSizeReport.TryParseSize(string.Empty.AsSpan(), out _, out _).Should().BeFalse();
        lookup.IsCompleted.Should().BeFalse();

        // The host's viewport message is answered with the page's resize:, remembered as ever.
        TerminalSizeReport.TryParseSize($"{HostDerivedColumns},{HostDerivedRows}".AsSpan(), out int columns, out int rows)
            .Should().BeTrue();
        report.Remember(columns, rows);

        TerminalSizeLookup resolved = await lookup.WaitAsync(TimeSpan.FromSeconds(5));
        resolved.Source.Should().Be(TerminalSizeSource.Reported);
        resolved.Size.Should().Be(new TerminalSize(HostDerivedColumns, HostDerivedRows));
    }

    [Fact]
    public void SizeReport_AcceptsTheShownMarkerAndNothingElseAsAThirdField()
    {
        TerminalSizeReport.TryParseSizeReport("76,39,shown".AsSpan(), out int columns, out int rows, out bool marked)
            .Should().BeTrue();
        columns.Should().Be(76);
        rows.Should().Be(39);
        marked.Should().BeTrue();

        TerminalSizeReport.TryParseSizeReport("76,39".AsSpan(), out _, out _, out bool plainMarked).Should().BeTrue();
        plainMarked.Should().BeFalse();
        TerminalSizeReport.TryParseSizeReport("76,39,other".AsSpan(), out _, out _, out _).Should().BeFalse();
        TerminalSizeReport.TryParseSize("76,39,shown".AsSpan(), out int plainColumns, out int plainRows).Should().BeTrue();
        (plainColumns, plainRows).Should().Be((76, 39));
    }

    [Fact]
    public void FirstShownSize_EqualToHostDerived_ReportsMatch()
    {
        FirstShownSizeCheck check = new();
        check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: false);

        FirstShownSizeResult? result = check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: true);

        result.Should().NotBeNull();
        result!.Matches.Should().BeTrue();
        result.ToLogLine().Should().Be("EmbeddedSSH first shown size 150x40 vs host-derived 150x40: match");
    }

    [Fact]
    public void FirstShownSize_Differs_ReportsMismatch()
    {
        FirstShownSizeCheck check = new();
        check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: false);

        FirstShownSizeResult? result = check.Observe(new TerminalSize(ShownColumns, HostDerivedRows), shownMarker: true);

        result.Should().NotBeNull();
        result!.Matches.Should().BeFalse();
        result.ToLogLine().Should().Be("EmbeddedSSH first shown size 149x40 vs host-derived 150x40: MISMATCH");
    }

    [Fact]
    public void FirstShownSize_NothingToCompareWithoutAHiddenReport()
    {
        new FirstShownSizeCheck().Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: true)
            .Should().BeNull();
    }

    /// <summary>The marker is the only thing that makes a report the first shown size.</summary>
    [Fact]
    public void ShownMarker_IsTheOnlyReportCompared()
    {
        FirstShownSizeCheck check = new();
        check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: false).Should().BeNull();
        check.Observe(new TerminalSize(ShownColumns, HostDerivedRows), shownMarker: false).Should().BeNull();

        FirstShownSizeResult? result = check.Observe(new TerminalSize(ShownColumns, HostDerivedRows), shownMarker: true);

        result.Should().NotBeNull();
        result!.HostDerived.Should().Be(new TerminalSize(ShownColumns, HostDerivedRows));
        result.Matches.Should().BeTrue();
    }

    /// <summary>
    /// A hidden-derived report still in flight when the veil drops is not the first shown size,
    /// whatever the surface visibility is by the time the host reads it.
    /// </summary>
    [Fact]
    public void PlainResizeAfterVeilDrops_IsNotTakenForTheFirstShownSize()
    {
        FirstShownSizeCheck check = new();
        check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: false);

        // The late hidden-derived report arrives after the surface became visible.
        FirstShownSizeResult? late = check.Observe(new TerminalSize(HostDerivedColumns, HostDerivedRows), shownMarker: false);
        FirstShownSizeResult? real = check.Observe(new TerminalSize(ShownColumns, HostDerivedRows), shownMarker: true);

        late.Should().BeNull();
        real.Should().NotBeNull();
        real!.Matches.Should().BeFalse();
    }
}
