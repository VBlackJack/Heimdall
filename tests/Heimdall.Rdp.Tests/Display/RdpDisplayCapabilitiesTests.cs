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

public sealed class RdpDisplayCapabilitiesTests
{
    [Fact]
    public void IsMultimonAvailable_FalseForZeroScreens()
    {
        Assert.False(RdpDisplayCapabilities.IsMultimonAvailable(0));
    }

    [Fact]
    public void IsMultimonAvailable_FalseForOneScreen()
    {
        Assert.False(RdpDisplayCapabilities.IsMultimonAvailable(1));
    }

    [Fact]
    public void IsMultimonAvailable_TrueForTwoScreens()
    {
        Assert.True(RdpDisplayCapabilities.IsMultimonAvailable(2));
    }

    [Fact]
    public void IsMultimonAvailable_TrueForThreeScreens()
    {
        Assert.True(RdpDisplayCapabilities.IsMultimonAvailable(3));
    }

    [Fact]
    public void FromMonitorBounds_KeepsTheCountAndTheBoundsInStep()
    {
        List<Rectangle> bounds =
        [
            new(0, 0, 1920, 1080),
            new(1920, 0, 2560, 1440),
        ];

        RdpDisplayCapabilities capabilities = RdpDisplayCapabilities.FromMonitorBounds(bounds);

        Assert.Equal(2, capabilities.MonitorCount);
        Assert.Equal(bounds, capabilities.MonitorBounds);
    }

    /// <summary>
    /// A decision has to stay attached to the topology it was taken against, so the caller's list
    /// cannot be changed underneath it.
    /// </summary>
    [Fact]
    public void FromMonitorBounds_CopiesTheCallersList()
    {
        List<Rectangle> bounds = [new(0, 0, 1920, 1080)];

        RdpDisplayCapabilities capabilities = RdpDisplayCapabilities.FromMonitorBounds(bounds);
        bounds.Add(new Rectangle(1920, 0, 1920, 1080));

        Assert.Single(capabilities.MonitorBounds);
        Assert.Equal(1, capabilities.MonitorCount);
    }

    /// <summary>
    /// A host whose screens could not be enumerated is a count without a topology, which callers
    /// have to be able to tell apart from a known one.
    /// </summary>
    [Fact]
    public void TheBoundsAreEmptyWhenOnlyACountIsKnown()
    {
        RdpDisplayCapabilities capabilities = new(3);

        Assert.Empty(capabilities.MonitorBounds);
        Assert.Equal(3, capabilities.MonitorCount);
    }

    [Fact]
    public void FromMonitorBounds_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => RdpDisplayCapabilities.FromMonitorBounds(null!));
    }
}
