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

namespace Heimdall.Core.Rdp;

/// <summary>
/// The single definition of how large a fixed-resolution RDP desktop may be.
/// </summary>
/// <remarks>
/// <para>
/// Five places used to carry their own copy of the pair 7680 x 4320: the schema
/// validator, the external-session profile resolver, the server dialog's range
/// attributes, the range messages that repeat the numbers in prose, and an
/// inline pattern in the custom-resolution parser. They agreed only because
/// nobody had changed one of them yet.
/// </para>
/// <para>
/// This type lives in <c>Heimdall.Core</c> deliberately. Core carries no project
/// reference of its own, so it is visible from <c>Heimdall.App</c> and from
/// <c>Heimdall.Rdp</c> alike. That is what makes a shared limit possible at all:
/// an earlier attempt put the clamp in <c>Heimdall.Rdp</c> and had to duplicate
/// the constants there, because <c>Heimdall.Rdp</c> cannot reference
/// <c>Heimdall.App</c>.
/// </para>
/// <para>
/// The members are <see langword="const" /> rather than <see langword="static" />
/// <see langword="readonly" /> because two consumers need compile-time constants:
/// the <c>[Range]</c> attributes on the server dialog, and the relational
/// patterns in the custom-resolution parser.
/// </para>
/// <para>
/// <c>SchemaValidator.MaxResolution</c> is a different decision that happens to
/// hold the same number today - it bounds a session's screen size, not a fixed
/// desktop - so it is deliberately not folded in here. Sharing text that merely
/// matches would couple two rules that are free to diverge.
/// </para>
/// </remarks>
public static class RdpDisplayLimits
{
    /// <summary>Smallest width or height a fixed RDP desktop may request, in pixels.</summary>
    public const int MinimumFixedDimension = 200;

    /// <summary>Largest width a fixed RDP desktop may request, in pixels (8K).</summary>
    public const int MaximumFixedWidth = 7680;

    /// <summary>Largest height a fixed RDP desktop may request, in pixels (8K).</summary>
    public const int MaximumFixedHeight = 4320;

    /// <summary>Validation message for an out-of-range fixed width.</summary>
    /// <remarks>
    /// The numbers are spelled out rather than interpolated from the constants
    /// above: a <see langword="const" /> interpolated string accepts only constant
    /// <see cref="string" /> operands, and these bounds are integers. The message
    /// must stay a compile-time constant because it is the <c>ErrorMessage</c> of a
    /// <c>[Range]</c> attribute and the key of the localization lookup that maps it.
    /// <c>RdpDisplayLimitsTests.RangeMessages_QuoteTheEnforcedBounds</c> fails if a
    /// bound changes and the prose does not follow.
    /// </remarks>
    public const string FixedWidthRangeMessage =
        "RDP fixed width must be between 200 and 7680.";

    /// <summary>Validation message for an out-of-range fixed height.</summary>
    /// <remarks>Guarded by the same test as <see cref="FixedWidthRangeMessage" />.</remarks>
    public const string FixedHeightRangeMessage =
        "RDP fixed height must be between 200 and 4320.";

    /// <summary>Smallest default screen size a session may request, in pixels.</summary>
    /// <remarks>
    /// A session's default screen size is a DIFFERENT decision from a fixed desktop, and
    /// these members stay separate from <see cref="MinimumFixedDimension" /> and
    /// <see cref="MaximumFixedWidth" /> even though the upper bound holds the same number
    /// today. They live here anyway because the decision itself had grown three holders -
    /// the schema validator and both default-resolution fields on the settings panel - which
    /// is the situation this type exists to end.
    /// </remarks>
    public const int MinimumSessionResolution = 640;

    /// <summary>Largest default screen size a session may request, in pixels.</summary>
    /// <remarks>See <see cref="MinimumSessionResolution" />.</remarks>
    public const int MaximumSessionResolution = 7680;

    /// <summary>Validation message for an out-of-range default screen width.</summary>
    /// <remarks>Guarded by the same test as <see cref="FixedWidthRangeMessage" />.</remarks>
    public const string DefaultResolutionWidthRangeMessage =
        "Default resolution width must be between 640 and 7680 pixels.";

    /// <summary>Validation message for an out-of-range default screen height.</summary>
    /// <remarks>Guarded by the same test as <see cref="FixedWidthRangeMessage" />.</remarks>
    public const string DefaultResolutionHeightRangeMessage =
        "Default resolution height must be between 640 and 7680 pixels.";

    /// <summary>Lowest colour depth a profile or the defaults may carry, in bits per pixel.</summary>
    /// <remarks>
    /// Sixteen rather than eight: the connect-time resolver has always rewritten anything at or
    /// below 16 to 16 (the control and the .rdp format know 16, 24 and 32), so a bound of 8
    /// accepted a value the session silently did not get. The floor now says what is enforced;
    /// importers bring lower depths onto it through <see cref="NormalizeColorDepth" />.
    /// </remarks>
    public const int MinimumColorDepth = 16;

    /// <summary>Highest colour depth a profile or the defaults may carry, in bits per pixel.</summary>
    public const int MaximumColorDepth = 32;

    /// <summary>Validation message for an out-of-range colour depth.</summary>
    /// <remarks>Guarded by the same test as <see cref="FixedWidthRangeMessage" />.</remarks>
    public const string ColorDepthRangeMessage =
        "Color depth must be between 16 and 32.";

    /// <summary>
    /// The depth the session is actually given for a requested one: 16, 24 or 32, the three
    /// the control and the .rdp format know. Anything at or below 16 is 16, anything above 24
    /// is 32.
    /// </summary>
    public static int NormalizeColorDepth(int colorDepth) => colorDepth switch
    {
        <= MinimumColorDepth => MinimumColorDepth,
        <= 24 => 24,
        _ => MaximumColorDepth
    };

    /// <summary>
    /// Brings a requested fixed width inside <see cref="MinimumFixedDimension" />
    /// and <see cref="MaximumFixedWidth" />.
    /// </summary>
    public static int ClampFixedWidth(int width) =>
        Math.Clamp(width, MinimumFixedDimension, MaximumFixedWidth);

    /// <summary>
    /// Brings a requested fixed height inside <see cref="MinimumFixedDimension" />
    /// and <see cref="MaximumFixedHeight" />.
    /// </summary>
    public static int ClampFixedHeight(int height) =>
        Math.Clamp(height, MinimumFixedDimension, MaximumFixedHeight);
}
