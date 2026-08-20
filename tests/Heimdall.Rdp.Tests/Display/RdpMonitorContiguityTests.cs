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

using System.Drawing;
using Heimdall.Rdp.Display;

namespace Heimdall.Rdp.Tests.Display;

/// <summary>
/// The connectedness predicate for a multi-monitor selection.
/// </summary>
public sealed class RdpMonitorContiguityTests
{
    private static Rectangle Screen(int x, int y) => new(x, y, 1920, 1080);

    /// <summary>
    /// The anti-regression oracle for this whole feature.
    /// </summary>
    /// <remarks>
    /// Windows Vista required a multi-monitor session to span a rectangle. Windows 7 removed that
    /// requirement. An L-shape is therefore a valid arrangement, and a predicate that tests for
    /// rectangularity, or for the selection covering its own bounding box, would reject sessions
    /// that work. That is the single most likely way to get this feature wrong, which is why this
    /// test exists before any of the negative ones.
    /// </remarks>
    [Fact]
    public void AnLShapedArrangementIsContiguous()
    {
        List<Rectangle> monitors = [Screen(0, 0), Screen(1920, 0), Screen(0, 1080)];

        Assert.True(RdpMonitorContiguity.AreContiguous(monitors));
    }

    /// <summary>
    /// The two arrangements a naive edge test accepts, kept in one theory because they are not
    /// separable.
    /// </summary>
    /// <remarks>
    /// A widely copied implementation tests only whether an edge coordinate of one rectangle equals
    /// an edge coordinate of the other, with no requirement that the shared border have length. It
    /// accepts both rows below. Relaxing the length requirement to allow zero accepts only the
    /// first; dropping the requirement that the other axis overlap accepts only the second. Either
    /// row alone therefore leaves the mutant the other row kills.
    /// </remarks>
    [Theory]
    [InlineData(1920, 1080, "monitors meeting only at a corner share no border")]
    [InlineData(1920, 5000, "a flush vertical edge with no vertical overlap is still a gap")]
    public void ArrangementsThatOnlyLookAdjacentAreNotContiguous(int x, int y, string because)
    {
        List<Rectangle> monitors = [Screen(0, 0), Screen(x, y)];

        Assert.False(RdpMonitorContiguity.AreContiguous(monitors), because);
    }

    [Fact]
    public void MonitorsSideBySideAreContiguous()
    {
        Assert.True(RdpMonitorContiguity.AreContiguous([Screen(0, 0), Screen(1920, 0)]));
    }

    [Fact]
    public void MonitorsStackedAreContiguous()
    {
        Assert.True(RdpMonitorContiguity.AreContiguous([Screen(0, 0), Screen(0, 1080)]));
    }

    /// <summary>
    /// The finding's own arrangement: the first and third of three dense monitors.
    /// </summary>
    [Fact]
    public void TheFirstAndThirdOfThreeDenseMonitorsAreNotContiguous()
    {
        Assert.False(RdpMonitorContiguity.AreContiguous([Screen(0, 0), Screen(3840, 0)]));
    }

    /// <summary>
    /// Connectedness is transitive through a third monitor, so a chain counts even though its ends
    /// do not touch each other.
    /// </summary>
    [Fact]
    public void AChainIsContiguousEvenThoughItsEndsDoNotTouch()
    {
        List<Rectangle> chain = [Screen(0, 0), Screen(3840, 0), Screen(1920, 0)];

        Assert.False(RdpMonitorContiguity.Touch(chain[0], chain[1]));
        Assert.True(RdpMonitorContiguity.AreContiguous(chain));
    }

    [Fact]
    public void TwoSeparateBlocksAreNotContiguous()
    {
        List<Rectangle> monitors =
        [
            Screen(0, 0),
            Screen(1920, 0),
            Screen(10000, 0),
            Screen(11920, 0),
        ];

        Assert.False(RdpMonitorContiguity.AreContiguous(monitors));
    }

    [Fact]
    public void OverlappingMonitorsAreContiguous()
    {
        Assert.True(RdpMonitorContiguity.AreContiguous([Screen(0, 0), Screen(960, 0)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FewerThanTwoMonitorsAreContiguousByDefinition(int count)
    {
        List<Rectangle> monitors = [.. Enumerable.Range(0, count).Select(index => Screen(index * 1920, 0))];

        Assert.True(RdpMonitorContiguity.AreContiguous(monitors));
    }

    /// <summary>
    /// Monitors of different sizes still share a border, as long as the shared span has length.
    /// </summary>
    [Fact]
    public void MonitorsOfDifferentHeightsStillShareABorder()
    {
        Rectangle wide = new(0, 0, 1920, 1080);
        Rectangle tall = new(1920, 500, 1200, 1920);

        Assert.True(RdpMonitorContiguity.Touch(wide, tall));
        Assert.True(RdpMonitorContiguity.AreContiguous([wide, tall]));
    }

    /// <summary>
    /// The shared span must have length: aligned edges that meet at exactly one point do not count.
    /// </summary>
    [Fact]
    public void MonitorsSharingASinglePointDoNotShareABorder()
    {
        Rectangle first = new(0, 0, 1920, 1080);
        Rectangle second = new(1920, 1080, 1200, 1920);

        Assert.False(RdpMonitorContiguity.Touch(first, second));
    }

    /// <summary>
    /// Coordinates far out on the virtual desktop must not overflow while being compared.
    /// </summary>
    [Fact]
    public void FarEdgesDoNotOverflow()
    {
        Rectangle first = new(int.MaxValue - 1920, 0, 1920, 1080);
        Rectangle second = new(int.MaxValue - 3840, 0, 1920, 1080);

        Assert.True(RdpMonitorContiguity.Touch(first, second));
        Assert.False(RdpMonitorContiguity.Touch(first, new Rectangle(int.MinValue, 0, 1920, 1080)));
    }

    [Fact]
    public void ANullSetIsRejectedRatherThanTreatedAsEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => RdpMonitorContiguity.AreContiguous(null!));
    }
}
