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
using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpRegionFrameLayoutTests
{
    /// <summary>
    /// The frame is the session rectangle, so it is never larger than the session.
    /// </summary>
    /// <remarks>
    /// <para>This case used to assert 1200x900 for a 1024x768 session in a 1600x900 pane - the
    /// aspect-fit answer, which is right when the image is scaled to the frame. Letterboxing is
    /// used exactly when it is not: the mode is Fixed with scaling off, so the remote image
    /// occupies 1024x768 and no more. The extra 176x132 inside the pinned host is Win32 background
    /// rather than the surface brush, which is the bleed the pinning exists to remove.</para>
    /// <para>Assertions the old expectation carried and this one keeps: the frame is top-left
    /// aligned with a margin that centres it, and the host is pinned rather than stretched.</para>
    /// </remarks>
    [Fact]
    public void FromPaneAndContent_WhenPaneIsWiderThanContent_NeverAllocatesMoreThanTheSession()
    {
        var layout = RdpRegionFrameLayout.FromPaneAndContent(1600, 900, 1024, 768);

        AssertClose(1024, layout.FrameWidth);
        AssertClose(768, layout.FrameHeight);
        AssertClose(288, layout.FrameMargin.Left);
        AssertClose(66, layout.FrameMargin.Top);
        Assert.True(layout.IsLetterboxActive);
        Assert.Equal(System.Windows.HorizontalAlignment.Left, layout.FrameHorizontalAlignment);
        Assert.Equal(System.Windows.VerticalAlignment.Top, layout.FrameVerticalAlignment);
        // RDP-LIVE-24: when letterbox is active, the WindowsFormsHost is pinned
        // to the frame size so the Win32 HWND does not bleed past it.
        Assert.Equal(System.Windows.HorizontalAlignment.Left, layout.HostHorizontalAlignment);
        Assert.Equal(System.Windows.VerticalAlignment.Top, layout.HostVerticalAlignment);
        Assert.Equal(new Thickness(0), layout.HostMargin);
        AssertClose(1024, layout.HostWidth);
        AssertClose(768, layout.HostHeight);
    }

    /// <summary>
    /// A pane smaller than the session still gets the aspect fit, because there is nothing else to
    /// give it: the bound only ever removes upscale, never downscale.
    /// </summary>
    [Fact]
    public void FromPaneAndContent_WhenPaneIsSmallerThanContent_StillFitsToThePane()
    {
        var layout = RdpRegionFrameLayout.FromPaneAndContent(800, 600, 1920, 1080);

        AssertClose(800, layout.FrameWidth);
        AssertClose(450, layout.FrameHeight);
        Assert.True(layout.IsLetterboxActive);
    }

    [Fact]
    public void FromPaneAndContent_WhenLetterboxInactive_HostStaysStretched()
    {
        var layout = RdpRegionFrameLayout.FromPaneAndContent(1600, 900, 1920, 1080);

        Assert.False(layout.IsLetterboxActive);
        Assert.Equal(System.Windows.HorizontalAlignment.Left, layout.HostHorizontalAlignment);
        Assert.True(double.IsNaN(layout.HostWidth));
        Assert.True(double.IsNaN(layout.HostHeight));
    }

    [Fact]
    public void FromPaneAndContent_WhenAspectRatioMatches_FillsFrameAndMarksLetterboxInactive()
    {
        var layout = RdpRegionFrameLayout.FromPaneAndContent(1600, 900, 1920, 1080);

        Assert.Equal(new Thickness(0), layout.FrameMargin);
        AssertClose(1600, layout.FrameWidth);
        AssertClose(900, layout.FrameHeight);
        Assert.False(layout.IsLetterboxActive);
    }

    [Fact]
    public void HasLetterboxBands_WhenFrameIsSmallerThanPane_ReturnsTrue()
    {
        var active = RdpRegionFrameLayout.HasLetterboxBands(
            (0, 75, 800, 450),
            800,
            600);

        Assert.True(active);
    }

    private static void AssertClose(double expected, double actual)
        => Assert.InRange(actual, expected - 0.0001, expected + 0.0001);
}
