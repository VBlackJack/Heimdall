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

namespace Heimdall.App.Views.EmbeddedRdp;

internal readonly record struct RdpRegionFrameLayout(
    Thickness FrameMargin,
    double FrameWidth,
    double FrameHeight,
    bool IsLetterboxActive)
{
    private const double LayoutTolerance = 0.5;

    public System.Windows.HorizontalAlignment FrameHorizontalAlignment => System.Windows.HorizontalAlignment.Left;

    public System.Windows.VerticalAlignment FrameVerticalAlignment => System.Windows.VerticalAlignment.Top;

    public System.Windows.HorizontalAlignment HostHorizontalAlignment => System.Windows.HorizontalAlignment.Left;

    public System.Windows.VerticalAlignment HostVerticalAlignment => System.Windows.VerticalAlignment.Top;

    public Thickness HostMargin => new(0);

    /// <summary>
    /// Pin the WindowsFormsHost width/height to the frame size so the
    /// hosted Win32 HWND is allocated exactly the RDP region rectangle.
    /// Without this, the HostVisual can extend past the frame and the Win32
    /// gray system background bleeds through the letterbox bands instead of
    /// the SurfaceBrush from the parent SurfaceContainer (RDP-LIVE-24).
    /// </summary>
    public double HostWidth => IsLetterboxActive ? FrameWidth : double.NaN;

    public double HostHeight => IsLetterboxActive ? FrameHeight : double.NaN;

    /// <summary>
    /// Sizes the frame for a session rendered at a fixed resolution with scaling off.
    /// </summary>
    /// <remarks>
    /// The aspect fit is only half the answer here. Letterboxing is used exactly when the session
    /// is not scaled, so the remote image occupies its own pixel size and nothing more: a frame
    /// larger than that is not the region rectangle it is pinned to be, and the leftover inside it
    /// is Win32 background rather than the surface brush - the bleed the pinning exists to remove.
    /// So the aspect-fit result is bounded by the content and re-centred.
    /// </remarks>
    public static RdpRegionFrameLayout FromPaneAndContent(
        double paneWidth,
        double paneHeight,
        double contentWidth,
        double contentHeight)
    {
        var rect = LetterboxLayoutCalculator.Compute(
            paneWidth,
            paneHeight,
            contentWidth,
            contentHeight);

        rect = BoundToContent(rect, paneWidth, paneHeight, contentWidth, contentHeight);

        return new RdpRegionFrameLayout(
            new Thickness(rect.HostX, rect.HostY, 0, 0),
            rect.HostWidth,
            rect.HostHeight,
            HasLetterboxBands(rect, paneWidth, paneHeight));
    }

    private static (double HostX, double HostY, double HostWidth, double HostHeight) BoundToContent(
        (double HostX, double HostY, double HostWidth, double HostHeight) rect,
        double paneWidth,
        double paneHeight,
        double contentWidth,
        double contentHeight)
    {
        if (rect.HostWidth <= 0 || rect.HostHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            return rect;
        }

        double width = Math.Min(rect.HostWidth, contentWidth);
        double height = Math.Min(rect.HostHeight, contentHeight);

        return (
            Math.Max(0, (paneWidth - width) / 2.0),
            Math.Max(0, (paneHeight - height) / 2.0),
            width,
            height);
    }

    public static bool HasLetterboxBands(
        (double HostX, double HostY, double HostWidth, double HostHeight) rect,
        double paneWidth,
        double paneHeight)
    {
        return rect.HostX > LayoutTolerance
            || rect.HostY > LayoutTolerance
            || rect.HostWidth < paneWidth - LayoutTolerance
            || rect.HostHeight < paneHeight - LayoutTolerance;
    }
}
