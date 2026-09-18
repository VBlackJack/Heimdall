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

using Heimdall.App.Views.Tools;

namespace Heimdall.App.Tests;

/// <summary>
/// What the placement bar draws, asked without a window being open.
/// </summary>
/// <remarks>
/// The bar drew five evenly spaced marks whenever the real places got close together. Four of the
/// five sat where no character could go, and none of them moved when the password got longer, so
/// the marks said the same thing about a twelve character password and a fifty character one.
/// </remarks>
public sealed class PlacementBarGeometryTests
{
    /// <summary>
    /// There is one place per character, and one more, because a character can also go before the
    /// first of them.
    /// </summary>
    [Fact]
    public void ThePlaces_AreCountedFromThePasswordOnScreen()
    {
        // Twenty characters of which two are digits and two are specials: sixteen were drawn, so
        // there are seventeen places to put a digit among them.
        Assert.Equal(17, PlacementBarGeometry.SlotCount(20, 2, 2, digits: true));

        // The specials are placed into the string the digits are already in, which is two
        // characters longer, so their row has two more places than the row above it.
        Assert.Equal(19, PlacementBarGeometry.SlotCount(20, 2, 2, digits: false));

        // And a longer password has more of them, which is the whole point.
        Assert.Equal(27, PlacementBarGeometry.SlotCount(30, 2, 2, digits: true));
        Assert.Equal(11, PlacementBarGeometry.SlotCount(14, 2, 2, digits: true));

        // Nothing was drawn, so there is nowhere to put anything.
        Assert.Equal(0, PlacementBarGeometry.SlotCount(4, 2, 2, digits: true));
    }

    /// <summary>
    /// One notch per place, never a fixed number of them.
    /// </summary>
    [Fact]
    public void TheNotches_AreOnePerPlace()
    {
        Assert.Equal(17, PlacementBarGeometry.TickCount(17, trackWidth: 400));
        Assert.Equal(27, PlacementBarGeometry.TickCount(27, trackWidth: 400));
        Assert.Equal(65, PlacementBarGeometry.TickCount(65, trackWidth: 400));
    }

    /// <summary>
    /// Places too close together to tell apart are not drawn as notches at all.
    /// </summary>
    /// <remarks>
    /// A row of notches a pixel apart is a grey band, and a grey band says less about the bar than
    /// a bare track does. What it must not do is draw a different, rounder number of them: marks
    /// nothing can settle on are marks in the wrong place.
    /// </remarks>
    [Fact]
    public void NotchesTooCloseToTellApart_AreNotDrawn()
    {
        // A cursor is fourteen wide, so a sixty-four wide track has fifty to spread across.
        Assert.Equal(0, PlacementBarGeometry.TickCount(60, trackWidth: 64));
        Assert.Equal(21, PlacementBarGeometry.TickCount(21, trackWidth: 64));

        // One place is no place to choose between.
        Assert.Equal(0, PlacementBarGeometry.TickCount(1, trackWidth: 400));
        Assert.Equal(0, PlacementBarGeometry.TickCount(0, trackWidth: 400));
    }

    /// <summary>
    /// One arrow key moves the cursor by one place, and anything in between settles on one.
    /// </summary>
    /// <remarks>
    /// The key used to move a fixed two percent of the bar. On a seventeen place row that is a
    /// third of a place, so the cursor came to rest between two notches: a position no character
    /// could be placed at, shown on a bar whose notches say otherwise.
    /// </remarks>
    [Fact]
    public void OnePress_IsWorthOnePlace()
    {
        Assert.Equal(6.25, PlacementBarGeometry.StepPercent(17), 6);
        Assert.Equal(0, PlacementBarGeometry.StepPercent(1), 6);

        Assert.Equal(6.25, PlacementBarGeometry.SnapToSlot(17, 7), 6);
        Assert.Equal(12.5, PlacementBarGeometry.SnapToSlot(17, 11), 6);
        Assert.Equal(100, PlacementBarGeometry.SnapToSlot(17, 120), 6);
        Assert.Equal(0, PlacementBarGeometry.SnapToSlot(17, -5), 6);
    }

    /// <summary>
    /// The first and last notch sit under the first and last place a cursor can reach.
    /// </summary>
    /// <remarks>
    /// A cursor is placed by its left edge and is fourteen wide, so the track's own ends are not
    /// where its middle can go. Notches drawn edge to edge would be half a cursor out at both
    /// ends, which is a bar that disagrees with itself by a whole place on a short password.
    /// </remarks>
    [Fact]
    public void TheEndNotches_SitWhereTheCursorEnds()
    {
        Assert.Equal(7, PlacementBarGeometry.TickX(0, 17, trackWidth: 400), 6);
        Assert.Equal(393, PlacementBarGeometry.TickX(16, 17, trackWidth: 400), 6);

        Assert.Equal(0, PlacementBarGeometry.CursorLeft(0, trackWidth: 400), 6);
        Assert.Equal(386, PlacementBarGeometry.CursorLeft(100, trackWidth: 400), 6);
    }
}
