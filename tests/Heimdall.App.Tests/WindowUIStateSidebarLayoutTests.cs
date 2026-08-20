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

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class WindowUIStateSidebarLayoutTests
{
    /// <summary>
    /// How long a completed STA thread is expected to take before it is worth reporting.
    /// </summary>
    /// <remarks>
    /// This was the assertion bound until a loaded CI runner missed it and reddened a run over
    /// thread scheduling rather than over anything this file tests. It is kept as the value that
    /// makes a slow run visible, not as the value that fails one.
    /// </remarks>
    private static readonly TimeSpan StaSchedulingExpectation = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The backstop against a thread that never finishes at all.
    /// </summary>
    /// <remarks>
    /// Matched to <c>TerminalTestHelpers.ProcessStartupBackstop</c>, whose own remarks record why
    /// the value alone is not the fix: widening a bound stops the timeouts and stops the evidence
    /// with them. So the wait below reports every completion that outlives the expectation above,
    /// even when it succeeds. The report, not the absence of failures, is what says whether the
    /// scheduling stall is gone.
    /// </remarks>
    private static readonly TimeSpan StaCompletionBackstop = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The greppable marker for a completion that outlived the expectation.
    /// </summary>
    internal const string StaOverExpectationMarker = "STA_WAIT_OVER_EXPECTATION";

    [Fact]
    public void VisibleSidebar_FullscreenRoundTrip_RestoresMeasuredWidth()
    {
        WindowUIState state = new();

        SidebarLayoutProjection fullscreen = state.EnterFullscreen(actualSidebarWidth: 450d);
        Assert.False(fullscreen.IsVisible);
        Assert.False(fullscreen.ShowRestoreButton);
        Assert.Equal(450d, fullscreen.Width);

        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.True(restored.IsVisible);
        Assert.False(restored.ShowRestoreButton);
        Assert.Equal(450d, restored.Width);
    }

    [Fact]
    public void HiddenSidebar_FullscreenRoundTrip_RemainsHidden_ThenSingleToggleRestoresWidth()
    {
        WindowUIState state = new()
        {
            IsSidebarHidden = true,
            SavedSidebarWidth = 450d
        };

        state.EnterFullscreen(actualSidebarWidth: 0d);
        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.False(restored.IsVisible);
        Assert.True(restored.ShowRestoreButton);
        Assert.Equal(450d, restored.Width);

        SidebarLayoutProjection toggled = state.ToggleSidebar(actualSidebarWidth: 0d);
        Assert.True(toggled.IsVisible);
        Assert.False(toggled.ShowRestoreButton);
        Assert.Equal(450d, toggled.Width);
    }

    [Fact]
    public void TabSuppressedSidebar_FullscreenRoundTrip_DoesNotCaptureTransientZero()
    {
        WindowUIState state = new();

        state.SetSidebarSuppressedByTab(isSuppressed: true, actualSidebarWidth: 450d);
        state.EnterFullscreen(actualSidebarWidth: 0d);
        SidebarLayoutProjection stillSuppressed = state.ExitFullscreen();
        Assert.False(stillSuppressed.IsVisible);
        Assert.False(stillSuppressed.ShowRestoreButton);
        Assert.Equal(450d, stillSuppressed.Width);

        SidebarLayoutProjection restored = state.SetSidebarSuppressedByTab(
            isSuppressed: false,
            actualSidebarWidth: 0d);
        Assert.True(restored.IsVisible);
        Assert.Equal(450d, restored.Width);
    }

    [Fact]
    public void ToggleSidebar_WhileFullscreen_ChangesPreferenceWithoutShowingSidebar()
    {
        WindowUIState state = new();
        state.EnterFullscreen(actualSidebarWidth: 420d);

        SidebarLayoutProjection fullscreen = state.ToggleSidebar(actualSidebarWidth: 0d);
        Assert.True(state.IsSidebarHidden);
        Assert.False(fullscreen.IsVisible);
        Assert.False(fullscreen.ShowRestoreButton);

        SidebarLayoutProjection restored = state.ExitFullscreen();
        Assert.False(restored.IsVisible);
        Assert.True(restored.ShowRestoreButton);
        Assert.Equal(420d, restored.Width);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EnterFullscreen_InvalidMeasuredWidth_DoesNotOverwriteSavedWidth(double measuredWidth)
    {
        WindowUIState state = new()
        {
            SavedSidebarWidth = 410d
        };

        SidebarLayoutProjection fullscreen = state.EnterFullscreen(measuredWidth);

        Assert.Equal(410d, fullscreen.Width);
    }

    [Theory]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, true, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, true, false, false)]
    public void SidebarLayout_ProjectsVisibilityAndRestoreButton(
        bool isFullscreen,
        bool isSidebarHidden,
        bool isSuppressedByTab,
        bool expectedVisible,
        bool expectedRestoreButton)
    {
        WindowUIState state = new()
        {
            IsFullscreen = isFullscreen,
            IsSidebarHidden = isSidebarHidden,
            IsSidebarSuppressedByTab = isSuppressedByTab
        };

        SidebarLayoutProjection projection = state.SidebarLayout;

        Assert.Equal(expectedVisible, projection.IsVisible);
        Assert.Equal(expectedRestoreButton, projection.ShowRestoreButton);
    }

    [Fact]
    public void ApplySidebarLayout_VisibleProjection_RestoresColumnSplitterAndButton()
    {
        RunOnStaThread(() =>
        {
            ColumnDefinition sidebarColumn = new();
            ColumnDefinition splitterColumn = new();
            Button restoreButton = new();
            SidebarLayoutProjection projection = new(
                IsVisible: true,
                ShowRestoreButton: false,
                Width: 450d);

            MainWindow.ApplySidebarLayout(
                projection,
                sidebarColumn,
                splitterColumn,
                restoreButton);

            Assert.Equal(WindowUIState.MinSidebarWidth, sidebarColumn.MinWidth);
            Assert.Equal(WindowUIState.MaxSidebarWidth, sidebarColumn.MaxWidth);
            Assert.Equal(450d, sidebarColumn.Width.Value);
            Assert.True(sidebarColumn.Width.IsAbsolute);
            Assert.True(splitterColumn.Width.IsAuto);
            Assert.Equal(Visibility.Collapsed, restoreButton.Visibility);
        });
    }

    [Theory]
    [InlineData(true, Visibility.Visible)]
    [InlineData(false, Visibility.Collapsed)]
    public void ApplySidebarLayout_HiddenProjection_CollapsesColumnsAndMapsRestoreButton(
        bool showRestoreButton,
        Visibility expectedVisibility)
    {
        RunOnStaThread(() =>
        {
            ColumnDefinition sidebarColumn = new()
            {
                MinWidth = WindowUIState.MinSidebarWidth,
                MaxWidth = WindowUIState.MaxSidebarWidth,
                Width = new GridLength(450d)
            };
            ColumnDefinition splitterColumn = new()
            {
                Width = GridLength.Auto
            };
            Button restoreButton = new()
            {
                Visibility = Visibility.Visible
            };
            SidebarLayoutProjection projection = new(
                IsVisible: false,
                ShowRestoreButton: showRestoreButton,
                Width: 450d);

            MainWindow.ApplySidebarLayout(
                projection,
                sidebarColumn,
                splitterColumn,
                restoreButton);

            Assert.Equal(0d, sidebarColumn.MinWidth);
            Assert.Equal(0d, sidebarColumn.MaxWidth);
            Assert.Equal(0d, sidebarColumn.Width.Value);
            Assert.True(sidebarColumn.Width.IsAbsolute);
            Assert.Equal(0d, splitterColumn.Width.Value);
            Assert.True(splitterColumn.Width.IsAbsolute);
            Assert.Equal(expectedVisibility, restoreButton.Visibility);
        });
    }

    /// <summary>
    /// The backstop has to sit above the expectation, or the evidence is unreachable.
    /// </summary>
    /// <remarks>
    /// If the two were swapped, or the backstop lowered to the expectation, the wait would fail
    /// again on a slow machine and the report below would never run - which is precisely the state
    /// this pair exists to leave behind.
    /// </remarks>
    [Fact]
    public void TheBackstopSitsAboveTheSchedulingExpectation()
    {
        Assert.True(
            StaCompletionBackstop > StaSchedulingExpectation,
            $"Backstop {StaCompletionBackstop} must exceed expectation {StaSchedulingExpectation}.");
    }

    /// <summary>
    /// The wait is bounded by the backstop and the report is triggered by the expectation.
    /// </summary>
    /// <remarks>
    /// Read from source: swapping the two values leaves both tests above green while restoring the
    /// failure on a loaded machine, and a machine under load is exactly what a normal run is not.
    /// </remarks>
    [Fact]
    public void TheWaitUsesTheBackstopAndTheReportUsesTheExpectation()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "tests",
            "Heimdall.App.Tests",
            "WindowUIStateSidebarLayoutTests.cs"));

        // Composed rather than written as one literal: a needle spelled out here would be found in
        // this very method, so the guard would pass while the real call site had been changed.
        string boundedWait = "completed.Wait(" + nameof(StaCompletionBackstop) + ")";
        string overExpectation = "stopwatch.Elapsed > " + nameof(StaSchedulingExpectation);

        Assert.Contains(boundedWait, source, StringComparison.Ordinal);
        Assert.Contains(overExpectation, source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }

    private void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using ManualResetEventSlim completed = new(initialState: false);
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Stopwatch stopwatch = Stopwatch.StartNew();
        bool finished = completed.Wait(StaCompletionBackstop);
        stopwatch.Stop();

        Assert.True(finished, $"STA test did not complete within {StaCompletionBackstop}.");

        if (stopwatch.Elapsed > StaSchedulingExpectation)
        {
            // Reported rather than asserted: a slow start is a fact about the machine, and losing
            // it to a widened bound is how a stall becomes invisible. Written to the console
            // because that is the channel a PASSING test reaches the `--verbosity normal` log
            // through - the same channel and the same greppable shape the terminal waits use, so
            // one query finds both families.
            Console.Out.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0} caller={1} elapsedMs={2:F3} expectationMs={3:F3} backstopMs={4:F3}",
                StaOverExpectationMarker,
                nameof(RunOnStaThread),
                stopwatch.Elapsed.TotalMilliseconds,
                StaSchedulingExpectation.TotalMilliseconds,
                StaCompletionBackstop.TotalMilliseconds));
        }

        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
