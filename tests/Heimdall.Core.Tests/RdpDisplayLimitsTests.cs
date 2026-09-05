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
using Heimdall.Core.Rdp;

namespace Heimdall.Core.Tests;

public sealed class RdpDisplayLimitsTests
{
    // The floor is the depth the session gets, so a value below it normalises onto it and never
    // below; above 24 the only depth left is 32.
    [Theory]
    [InlineData(0, 16)]
    [InlineData(8, 16)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(20, 24)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(64, 32)]
    public void NormalizeColorDepth_LandsOnTheThreeDepthsTheControlKnows(int raw, int expected)
    {
        Assert.Equal(expected, RdpDisplayLimits.NormalizeColorDepth(raw));
        Assert.InRange(expected, RdpDisplayLimits.MinimumColorDepth, RdpDisplayLimits.MaximumColorDepth);
    }

    /// <summary>
    /// The range messages are the one place the bounds are spelled out a second
    /// time, because a <c>[Range]</c> attribute needs a constant string and C#
    /// will not interpolate integer constants into one. This test is what stops
    /// the prose from drifting away from what is enforced.
    /// </summary>
    [Fact]
    public void RangeMessages_QuoteTheEnforcedBounds()
    {
        string min = RdpDisplayLimits.MinimumFixedDimension.ToString(CultureInfo.InvariantCulture);
        string maxWidth = RdpDisplayLimits.MaximumFixedWidth.ToString(CultureInfo.InvariantCulture);
        string maxHeight = RdpDisplayLimits.MaximumFixedHeight.ToString(CultureInfo.InvariantCulture);

        Assert.Equal(
            RdpDisplayLimits.FixedWidthRangeMessage,
            $"RDP fixed width must be between {min} and {maxWidth}.");

        Assert.Equal(
            RdpDisplayLimits.FixedHeightRangeMessage,
            $"RDP fixed height must be between {min} and {maxHeight}.");

        string minSession =
            RdpDisplayLimits.MinimumSessionResolution.ToString(CultureInfo.InvariantCulture);
        string maxSession =
            RdpDisplayLimits.MaximumSessionResolution.ToString(CultureInfo.InvariantCulture);

        string minDepth = RdpDisplayLimits.MinimumColorDepth.ToString(CultureInfo.InvariantCulture);
        string maxDepth = RdpDisplayLimits.MaximumColorDepth.ToString(CultureInfo.InvariantCulture);

        Assert.Equal(
            RdpDisplayLimits.ColorDepthRangeMessage,
            $"Color depth must be between {minDepth} and {maxDepth}.");

        Assert.Equal(
            RdpDisplayLimits.DefaultResolutionWidthRangeMessage,
            $"Default resolution width must be between {minSession} and {maxSession} pixels.");

        Assert.Equal(
            RdpDisplayLimits.DefaultResolutionHeightRangeMessage,
            $"Default resolution height must be between {minSession} and {maxSession} pixels.");
    }

    /// <summary>
    /// The session bounds and the fixed-desktop bounds share their upper number today and
    /// are still separate decisions. This pins that they are declared separately, so a
    /// later change to one cannot silently move the other.
    /// </summary>
    [Fact]
    public void SessionAndFixedBounds_AreDeclaredSeparately()
    {
        Assert.NotEqual(
            RdpDisplayLimits.MinimumFixedDimension,
            RdpDisplayLimits.MinimumSessionResolution);

        Assert.Equal(640, RdpDisplayLimits.MinimumSessionResolution);
        Assert.Equal(200, RdpDisplayLimits.MinimumFixedDimension);
    }

    [Theory]
    [InlineData(20000, 7680)]
    [InlineData(7681, 7680)]
    [InlineData(10, 200)]
    [InlineData(-4, 200)]
    public void ClampFixedWidth_BringsOutOfRangeValuesInside(int requested, int expected)
    {
        Assert.Equal(expected, RdpDisplayLimits.ClampFixedWidth(requested));
    }

    [Theory]
    [InlineData(20000, 4320)]
    [InlineData(4321, 4320)]
    [InlineData(10, 200)]
    [InlineData(-4, 200)]
    public void ClampFixedHeight_BringsOutOfRangeValuesInside(int requested, int expected)
    {
        Assert.Equal(expected, RdpDisplayLimits.ClampFixedHeight(requested));
    }

    /// <summary>
    /// Width and height do not share a maximum. A clamp that used one bound for
    /// both would pass every width case above and still be wrong.
    /// </summary>
    [Fact]
    public void MaximumWidthAndHeight_AreDistinct()
    {
        Assert.NotEqual(RdpDisplayLimits.MaximumFixedWidth, RdpDisplayLimits.MaximumFixedHeight);
        Assert.Equal(4320, RdpDisplayLimits.ClampFixedHeight(RdpDisplayLimits.MaximumFixedWidth));
    }
}
