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

using System.Windows;
using Heimdall.App.Behaviors;

namespace Heimdall.App.Tests;

/// <summary>
/// The rule deciding how small a window may be told it must stay.
/// </summary>
/// <remarks>
/// A declared minimum larger than the working area does not make a window bigger - it makes it
/// unusable, because the user can neither shrink it to fit nor reach what falls off the screen.
/// The rule is pure arithmetic and belongs in the blocking lane; attaching it to a live window
/// is what needs a desktop.
/// </remarks>
public sealed class WorkingAreaMinimumBehaviorTests
{
    [Fact]
    public void Resolve_MinimumFitsTheWorkingArea_LeavesItAlone()
    {
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(800, 600),
            workingArea: new Size(1920, 1080));

        Assert.Equal(new Size(800, 600), resolved);
        Assert.False(WorkingAreaMinimumPolicy.ExceedsWorkingArea(new Size(800, 600), new Size(1920, 1080)));
    }

    [Fact]
    public void Resolve_MinimumWiderThanTheWorkingArea_ClampsWidthOnly()
    {
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(800, 600),
            workingArea: new Size(720, 1080));

        Assert.Equal(new Size(720, 600), resolved);
    }

    [Fact]
    public void Resolve_MinimumTallerThanTheWorkingArea_ClampsHeightOnly()
    {
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(800, 600),
            workingArea: new Size(1920, 540));

        Assert.Equal(new Size(800, 540), resolved);
    }

    [Fact]
    public void Resolve_TheCaseTheFindingDescribes_BringsTheMinimumWithinReach()
    {
        // 1366x768 at 150%: the logical work area is roughly 911x470, below the 800x600 the main
        // window declares. Before clamping, the window could not be made to fit the screen at all.
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(800, 600),
            workingArea: new Size(911, 470));

        Assert.Equal(new Size(800, 470), resolved);
        Assert.True(WorkingAreaMinimumPolicy.ExceedsWorkingArea(new Size(800, 600), new Size(911, 470)));
    }

    // A negative extent is absent from this list because WPF's Size refuses to hold one: the
    // policy's guard against it is unreachable defence, not a case a test can construct.
    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Resolve_UnmeasurableWorkingArea_KeepsTheDeclaredMinimum(double unusable)
    {
        // A measurement that failed is never a reason to shrink a window.
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(800, 600),
            workingArea: new Size(unusable, unusable));

        Assert.Equal(new Size(800, 600), resolved);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    public void Resolve_WindowDeclaringNoMinimum_IsLeftAlone(double unset)
    {
        // WPF's own default is 0, and NaN reaches here from an unset dependency property. Neither
        // is a minimum anyone asked for, so neither is clamped to the screen.
        Size resolved = WorkingAreaMinimumPolicy.Resolve(
            declaredMinimum: new Size(unset, unset),
            workingArea: new Size(1920, 1080));

        Assert.Equal(unset, resolved.Width);
        Assert.Equal(unset, resolved.Height);
    }

    // --- The declared minimum must survive being clamped -----------------------------------------
    // Resolving from the window's CURRENT minimum works once and then poisons itself: the clamped
    // value becomes the declared one, and a later application on a larger working area can no
    // longer restore what the XAML asked for. A window clamped to 800x470 on a small display
    // stayed at 470 after being moved to a 1080-tall one.

    [Fact]
    public void Tracker_SmallAreaThenLargeArea_RestoresTheDeclaredMinimum()
    {
        WorkingAreaMinimumTracker tracker = new();
        tracker.Capture(new Size(800, 600));

        Assert.Equal(new Size(800, 470), tracker.Resolve(new Size(911, 470)));

        // The window moved to a display that can supply the whole declared minimum.
        Assert.Equal(new Size(800, 600), tracker.Resolve(new Size(1920, 1080)));
    }

    [Fact]
    public void Tracker_RepeatedApplicationOnTheSameArea_IsStable()
    {
        WorkingAreaMinimumTracker tracker = new();
        tracker.Capture(new Size(800, 600));

        Size first = tracker.Resolve(new Size(911, 470));
        Size second = tracker.Resolve(new Size(911, 470));
        Size third = tracker.Resolve(new Size(911, 470));

        // The behavior re-applies on every DPI change; repetition must not ratchet downwards.
        Assert.Equal(new Size(800, 470), first);
        Assert.Equal(first, second);
        Assert.Equal(second, third);
    }

    [Fact]
    public void Tracker_ReleaseAfterAClamp_GivesBackTheDeclaredMinimum()
    {
        WorkingAreaMinimumTracker tracker = new();
        tracker.Capture(new Size(800, 600));
        tracker.Resolve(new Size(911, 470));

        Assert.Equal(new Size(800, 600), tracker.Release());
        Assert.False(tracker.HasCapture);
    }

    [Fact]
    public void Tracker_SecondCapture_DoesNotOverwriteTheDeclaredMinimum()
    {
        WorkingAreaMinimumTracker tracker = new();
        tracker.Capture(new Size(800, 600));
        tracker.Resolve(new Size(911, 470));

        // What a re-activation would offer after a clamp: the window's own, already-clamped value.
        tracker.Capture(new Size(800, 470));

        Assert.Equal(new Size(800, 600), tracker.DeclaredMinimum);
        Assert.Equal(new Size(800, 600), tracker.Resolve(new Size(1920, 1080)));
    }

    [Fact]
    public void Tracker_CaptureAfterRelease_RecordsTheNewValue()
    {
        WorkingAreaMinimumTracker tracker = new();
        tracker.Capture(new Size(800, 600));
        tracker.Release();

        // Releasing restored the declared minimum on the window, so a fresh capture is correct.
        tracker.Capture(new Size(640, 480));

        Assert.Equal(new Size(640, 480), tracker.DeclaredMinimum);
    }

    [Fact]
    public void Resolve_IsIdempotent()
    {
        Size workingArea = new(720, 470);
        Size once = WorkingAreaMinimumPolicy.Resolve(new Size(800, 600), workingArea);
        Size twice = WorkingAreaMinimumPolicy.Resolve(once, workingArea);

        // The behavior re-applies on every DPI change, so a second pass must not shrink further.
        Assert.Equal(once, twice);
    }
}
