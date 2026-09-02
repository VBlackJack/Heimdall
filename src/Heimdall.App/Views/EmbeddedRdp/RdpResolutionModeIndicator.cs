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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Effective resolution state observed at the toolbar level: the live mode
/// derived from the per-session manual override (if any) combined with the
/// persisted profile mode and dimensions.
/// </summary>
internal readonly record struct RdpEffectiveResolutionState(
    RdpResolutionMode Mode,
    int? Width,
    int? Height);

/// <summary>
/// Pure helpers behind the Resolution toolbar button: resolves which mode is
/// effectively active right now, picks a glyph and a localization key per
/// mode, and formats the menu header / tooltip strings.
///
/// Kept static and stateless so it can be unit-tested without WPF / STA.
/// </summary>
internal static class RdpResolutionModeIndicator
{
    // Segoe MDL2 Assets glyphs as raw Private Use Area codepoints.
    private const string GlyphAuto = "";        // Settings - system decides
    private const string GlyphFitWindow = "";   // FullScreen - follows the window
    private const string GlyphSmartSizing = ""; // BackToWindow - smart shrink-fit
    private const string GlyphFixed = "";       // TileSize - fixed rectangle
    private const string GlyphMultimon = "";    // TVMonitor - multi-display

    /// <summary>
    /// Resolves the live effective mode: a session-scoped manual override
    /// (Width/Height passed via the Resolution menu) always wins and is
    /// reported as <see cref="RdpResolutionMode.Fixed"/> with those dims.
    /// Otherwise the persisted profile mode is returned, with profile dims
    /// surfaced for <see cref="RdpResolutionMode.Fixed"/>.
    /// </summary>
    public static RdpEffectiveResolutionState Resolve(
        RdpResolutionMode profileMode,
        int manualWidth,
        int manualHeight,
        int profileFixedWidth,
        int profileFixedHeight)
    {
        if (manualWidth > 0 && manualHeight > 0)
        {
            return new RdpEffectiveResolutionState(RdpResolutionMode.Fixed, manualWidth, manualHeight);
        }

        if (profileMode == RdpResolutionMode.Fixed
            && profileFixedWidth > 0 && profileFixedHeight > 0)
        {
            return new RdpEffectiveResolutionState(RdpResolutionMode.Fixed, profileFixedWidth, profileFixedHeight);
        }

        return new RdpEffectiveResolutionState(profileMode, null, null);
    }

    /// <summary>Segoe MDL2 glyph picked for each mode (5 distinct codepoints).</summary>
    public static string GetGlyph(RdpResolutionMode mode) => mode switch
    {
        RdpResolutionMode.Auto => GlyphAuto,
        RdpResolutionMode.FitWindow => GlyphFitWindow,
        RdpResolutionMode.SmartSizing => GlyphSmartSizing,
        RdpResolutionMode.Fixed => GlyphFixed,
        RdpResolutionMode.Multimon => GlyphMultimon,
        _ => GlyphFitWindow,
    };

    /// <summary>
    /// Localization key for the short mode label used in the menu header and
    /// tooltip. Reuses the existing ServerDialog labels except for Auto, which
    /// has its own short variant (the dialog appends "(recommended)").
    /// </summary>
    public static string GetModeLocalizationKey(RdpResolutionMode mode) => mode switch
    {
        RdpResolutionMode.Auto => "RdpResolutionModeAutoShort",
        RdpResolutionMode.FitWindow => "ServerDialogResolutionModeFitWindow",
        RdpResolutionMode.SmartSizing => "ServerDialogResolutionModeSmartSizing",
        RdpResolutionMode.Fixed => "ServerDialogResolutionModeFixed",
        RdpResolutionMode.Multimon => "ServerDialogResolutionModeMultimon",
        _ => "ServerDialogResolutionModeFitWindow",
    };

    /// <summary>
    /// The header layout used when the caller supplies no template. Separator and word order are
    /// not adaptable through these, which is why a caller that can reach the locale files is
    /// expected to pass its own; they exist so a caller that cannot still produces ASCII.
    /// </summary>
    internal const string DefaultHeaderTemplate = "{0}: {1}";

    /// <summary>The same, for the variant that carries the pixel dimensions.</summary>
    internal const string DefaultHeaderWithSizeTemplate = "{0}: {1} ({2}x{3})";

    /// <summary>
    /// Builds the resolution menu header. Template arguments: <c>{0}</c> = active-mode label,
    /// <c>{1}</c> = mode label, <c>{2}</c> = width, <c>{3}</c> = height.
    /// </summary>
    /// <remarks>
    /// The templates are parameters for the same reason <see cref="FormatTooltip"/>'s are: the
    /// string is user-facing, and a translator cannot reach a literal compiled into this method.
    /// The literal this replaced also carried a multiplication sign, which the documentation on
    /// this very method described as an ASCII <c>x</c>.
    /// </remarks>
    public static string FormatHeader(
        string activeModeLabel,
        string modeLabel,
        int? width,
        int? height,
        string? headerTemplate = null,
        string? headerWithSizeTemplate = null)
    {
        if (width.HasValue && height.HasValue && width.Value > 0 && height.Value > 0)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                headerWithSizeTemplate ?? DefaultHeaderWithSizeTemplate,
                activeModeLabel,
                modeLabel,
                width.Value,
                height.Value);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            headerTemplate ?? DefaultHeaderTemplate,
            activeModeLabel,
            modeLabel);
    }

    /// <summary>
    /// Picks the right localization template for the resolution tooltip
    /// depending on whether dimensions are available, then formats it.
    /// Template arguments: <c>{0}</c> = mode label, <c>{1}</c> = width,
    /// <c>{2}</c> = height.
    /// </summary>
    public static string FormatTooltip(
        string templateWithMode,
        string templateWithModeAndSize,
        string modeLabel,
        int? width,
        int? height)
    {
        if (width.HasValue && height.HasValue && width.Value > 0 && height.Value > 0)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                templateWithModeAndSize,
                modeLabel,
                width.Value,
                height.Value);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            templateWithMode,
            modeLabel);
    }
}
