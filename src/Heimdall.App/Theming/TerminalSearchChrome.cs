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
using System.Text.RegularExpressions;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Theming;

/// <summary>
/// The colours of the terminal page's search bar, loading text and scrollbar, derived from the
/// active terminal colour scheme.
/// </summary>
/// <remarks>
/// <para>The bar used to be drawn in fixed Dracula greys whatever the scheme: its pink no-match
/// status was 3.77 to 1 on the grey bar, and its hovered button text 4.41. It now takes the scheme's
/// background and foreground, mixes its surfaces from them, and derives each colour drawn on those
/// surfaces with <see cref="ThemeTextColours.Derive"/>: text at 4.5 to 1 (WCAG 1.4.3), borders and
/// the focus outline at 3 to 1 (WCAG 1.4.11).</para>
/// <para>The loading text and the scrollbar were fixed Dracula colours too: on a light scheme the
/// loading text was near white on white. They reuse the bar's colours: the scrollbar track is the
/// scheme's background, its thumb the border (3 to 1 on that background), and the hovered thumb
/// and the loading text the bar's text (4.5 to 1).</para>
/// <para>A scheme that leaves a colour out gets xterm.js's own default for it, the colour the
/// terminal under the bar is drawn with; the caller hands those defaults in with the scheme, from
/// the terminal palettes they belong to.</para>
/// </remarks>
internal static class TerminalSearchChrome
{
    /// <summary>How much of the foreground tints the bar, the hovered button and the border.</summary>
    private const double BarTint = 0.14;
    private const double HoverTint = 0.26;
    private const double BorderTint = 0.45;

    /// <summary>
    /// The page placeholders and the colour each one receives for <paramref name="themeJson"/>, an
    /// xterm.js theme object literal.
    /// </summary>
    /// <param name="themeJson">The active scheme, as handed to the terminal.</param>
    /// <param name="defaultsJson">xterm.js's own colours, in the same shape, for what the scheme leaves out.</param>
    internal static IReadOnlyList<(string Placeholder, string Colour)> CssVariables(string themeJson, string defaultsJson)
    {
        Palette palette = Derive(themeJson, defaultsJson);
        return
        [
            ("/*{{CSS_SEARCH_BAR_BG}}*/", Hex(palette.Bar)),
            ("/*{{CSS_SEARCH_INPUT_BG}}*/", Hex(palette.Input)),
            ("/*{{CSS_SEARCH_HOVER_BG}}*/", Hex(palette.Hover)),
            ("/*{{CSS_SEARCH_TEXT}}*/", Hex(palette.Text)),
            ("/*{{CSS_SEARCH_BORDER}}*/", Hex(palette.Border)),
            ("/*{{CSS_SEARCH_FOCUS}}*/", Hex(palette.Focus)),
            ("/*{{CSS_SEARCH_NO_MATCH_TEXT}}*/", Hex(palette.NoMatchText)),
            ("/*{{CSS_SEARCH_NO_MATCH_BORDER}}*/", Hex(palette.NoMatchBorder)),
            ("/*{{CSS_LOADING_TEXT}}*/", Hex(palette.Text)),
            ("/*{{CSS_SCROLLBAR_TRACK}}*/", Hex(palette.Input)),
            ("/*{{CSS_SCROLLBAR_THUMB}}*/", Hex(palette.Border)),
            ("/*{{CSS_SCROLLBAR_THUMB_HOVER}}*/", Hex(palette.Text)),
        ];
    }

    /// <summary>The bar's colours for a scheme, surfaces and the colours drawn on them.</summary>
    /// <param name="themeJson">The active scheme.</param>
    /// <param name="defaultsJson">xterm.js's own colours, for what the scheme leaves out.</param>
    internal static Palette Derive(string themeJson, string defaultsJson)
    {
        ArgumentNullException.ThrowIfNull(themeJson);
        ArgumentNullException.ThrowIfNull(defaultsJson);

        Color background = ColourOf(themeJson, defaultsJson, "background");
        Color foreground = ColourOf(themeJson, defaultsJson, "foreground");
        Color cursor = ColourOf(themeJson, defaultsJson, "cursor");
        Color red = ColourOf(themeJson, defaultsJson, "red");
        bool lighten = ThemeLuminance.IsDark(background);

        Color bar = Mix(foreground, BarTint, background);
        Color hover = Mix(foreground, HoverTint, background);
        Color[] surfaces = [bar, background, hover];
        Color[] outlined = [bar, background];

        return new Palette(
            Bar: bar,
            Input: background,
            Hover: hover,
            Text: ThemeTextColours.Derive(foreground, surfaces, lighten),
            Border: ThemeTextColours.Derive(Mix(foreground, BorderTint, background), outlined, lighten, ThemeTextColours.MinimumBoundaryContrast),
            Focus: ThemeTextColours.Derive(cursor, outlined, lighten, ThemeTextColours.MinimumBoundaryContrast),
            NoMatchText: ThemeTextColours.Derive(red, [bar], lighten),
            NoMatchBorder: ThemeTextColours.Derive(red, outlined, lighten, ThemeTextColours.MinimumBoundaryContrast));
    }

    /// <summary>A colour of the scheme as an opaque colour, the default's when the scheme has none in #RRGGBB.</summary>
    private static Color ColourOf(string themeJson, string defaultsJson, string key)
    {
        string hex = HexOf(themeJson, key) ?? HexOf(defaultsJson, key)
            ?? throw new ArgumentException($"The terminal defaults give no colour for '{key}'.", nameof(defaultsJson));
        return new Color
        {
            A = byte.MaxValue,
            R = byte.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            G = byte.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            B = byte.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        };
    }

    private static string? HexOf(string themeJson, string key)
    {
        Match match = Regex.Match(themeJson, $@"\b{key}:\s*'#([0-9A-Fa-f]{{6}})'", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary><paramref name="top"/> at <paramref name="amount"/> over an opaque colour.</summary>
    private static Color Mix(Color top, double amount, Color under)
    {
        byte Channel(byte t, byte u) => (byte)Math.Round((t * amount) + (u * (1 - amount)));
        return new Color { A = byte.MaxValue, R = Channel(top.R, under.R), G = Channel(top.G, under.G), B = Channel(top.B, under.B) };
    }

    private static string Hex(Color colour) =>
        string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");

    /// <summary>The search bar's colours for one scheme.</summary>
    internal readonly record struct Palette(
        Color Bar, Color Input, Color Hover, Color Text, Color Border, Color Focus, Color NoMatchText, Color NoMatchBorder);
}
