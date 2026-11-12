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

namespace Heimdall.App.Services;

/// <summary>
/// The host's answer to the terminal page's <c>ready:</c>: how the page is to lay itself out
/// while its surface is hidden behind a veil.
/// </summary>
/// <remarks>
/// <para>A hidden WebView2 is arranged by WPF (its <c>ActualWidth</c> and <c>ActualHeight</c> are
/// right) but the native window it hosts is not positioned while it is not visible, so the page
/// keeps its creation bounds and fits to a box that is not the pane. The host therefore tells it
/// the box: <c>viewport:W,H</c>, in CSS pixels, while hidden; and <c>viewport:</c> once the
/// surface is shown, which means the page measures itself again.</para>
/// <para>CSS pixels equal device-independent pixels here: the control's zoom factor is 1 and zoom
/// control is disabled, and a factor other than 1 is divided out by the caller.</para>
/// </remarks>
internal static class TerminalViewportMessage
{
    /// <summary>The message prefix, shared by both forms.</summary>
    internal const string Prefix = "viewport:";

    /// <summary>The surface is shown: the page measures itself.</summary>
    internal const string Shown = Prefix;

    /// <summary>At most this many decimals in a CSS pixel value.</summary>
    private const string PixelFormat = "0.##";

    /// <summary>The page's diagnostic line when its surface never took its bounds.</summary>
    internal const string DiagSurfaceNeverTookBounds = "surface-never-took-bounds";

    /// <summary>
    /// Builds <c>viewport:W,H</c> for the box the native window will really get, or says there is
    /// nothing to send: layout has not happened yet, or the surface is not hidden.
    /// </summary>
    /// <remarks>
    /// <para>The native window behind the control is sized from the element's rectangle converted to
    /// device pixels with each edge rounded separately, so its width is
    /// <c>round(right) - round(left)</c>, which can differ by one device pixel from
    /// <c>round(width)</c> when the left edge sits on a fractional pixel. One pixel can flip a
    /// column, and a pipe session keeps its width for good, so the same arithmetic is done here.</para>
    /// <para>Rounding rule: <see cref="MidpointRounding.AwayFromZero"/>. I could not establish from
    /// documentation which midpoint rule the host window uses (WPF's own layout rounding is not
    /// documented to be the one that sizes the native window), and AwayFromZero is what the
    /// device-pixel conversion of a positive rectangle is generally taken to be.</para>
    /// </remarks>
    /// <param name="left">The element's left edge in DIPs, relative to the window root.</param>
    /// <param name="top">The element's top edge in DIPs, relative to the window root.</param>
    /// <param name="width">The element's laid-out width in DIPs.</param>
    /// <param name="height">The element's laid-out height in DIPs.</param>
    /// <param name="scaleX">Device pixels per DIP, horizontally.</param>
    /// <param name="scaleY">Device pixels per DIP, vertically.</param>
    /// <param name="surfaceHidden">Whether the surface is hidden.</param>
    /// <param name="message">The message, when one is built.</param>
    /// <param name="zoom">The control's zoom factor; CSS pixels are DIPs divided by it.</param>
    internal static bool TryBuild(
        double left,
        double top,
        double width,
        double height,
        double scaleX,
        double scaleY,
        bool surfaceHidden,
        out string message,
        double zoom = 1)
    {
        message = string.Empty;
        if (!surfaceHidden
            || !double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(width)
            || !double.IsFinite(height)
            || width <= 0
            || height <= 0
            || !double.IsFinite(scaleX)
            || !double.IsFinite(scaleY)
            || scaleX <= 0
            || scaleY <= 0
            || !double.IsFinite(zoom)
            || zoom <= 0)
        {
            return false;
        }

        double cssWidth = EdgeRoundedExtent(left, width, scaleX) / zoom;
        double cssHeight = EdgeRoundedExtent(top, height, scaleY) / zoom;
        if (cssWidth <= 0 || cssHeight <= 0)
        {
            return false;
        }

        message = Prefix
            + cssWidth.ToString(PixelFormat, CultureInfo.InvariantCulture)
            + ","
            + cssHeight.ToString(PixelFormat, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>The extent in DIPs of the device-pixel span between the two rounded edges.</summary>
    private static double EdgeRoundedExtent(double start, double extent, double scale)
    {
        double startPixels = Math.Round(start * scale, MidpointRounding.AwayFromZero);
        double endPixels = Math.Round((start + extent) * scale, MidpointRounding.AwayFromZero);
        return (endPixels - startPixels) / scale;
    }
}

/// <summary>What the first size reported once the surface was shown says about the host's box.</summary>
internal sealed record FirstShownSizeResult(TerminalSize Shown, TerminalSize HostDerived, bool Matches)
{
    /// <summary>The one support-log line that settles whether the host-given box was right.</summary>
    public string ToLogLine() =>
        $"EmbeddedSSH first shown size {Shown.Columns}x{Shown.Rows} vs host-derived "
        + $"{HostDerived.Columns}x{HostDerived.Rows}: {(Matches ? "match" : "MISMATCH")}";
}

/// <summary>
/// Compares the size the page derived while its surface was hidden (laid out in the box the host
/// gave it) with the one report it marks as the first measured with the surface shown.
/// </summary>
/// <remarks>
/// The decision is the page's, carried by the marker on that one report. It is not read from the
/// surface visibility at the time the host processes a message: a hidden-derived report still in
/// flight when the veil drops would be taken for the first shown size.
/// </remarks>
internal sealed class FirstShownSizeCheck
{
    private TerminalSize? _hostDerived;

    /// <summary>
    /// Notes a report. An unmarked one is hidden-derived and becomes the size to compare with; the
    /// marked one is compared with the last of those, and nothing else is.
    /// </summary>
    public FirstShownSizeResult? Observe(TerminalSize size, bool shownMarker)
    {
        ArgumentNullException.ThrowIfNull(size);
        if (!shownMarker)
        {
            _hostDerived = size;
            return null;
        }

        return _hostDerived is null ? null : new FirstShownSizeResult(size, _hostDerived, size == _hostDerived);
    }
}
