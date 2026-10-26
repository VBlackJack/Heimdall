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
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Theming;

/// <summary>
/// The colours the theme bridge paints semantic and accent text, boundary lines and the primary
/// button's hover fill with, derived from the active theme so that each one stands out on the
/// grounds it is drawn on.
/// </summary>
/// <remarks>
/// <para>A ThemeForge palette gives one colour per meaning (error, warning, success, info,
/// accent) and that colour is tuned for fills, badges and borders. As text it can fall short:
/// on Drakul the error red is 2.91 to 1 on the card, the accent 3.79 to 1. The secondary text
/// colour is derived the same way: Dracula's is its comment colour, 3.03 to 1 on the window and
/// 1.94 on a card, and on most dark themes it fell to 3.75 to 4.4 on a hovered row.</para>
/// <para>The derivation keeps the hue and saturation and only moves the lightness, away from the
/// grounds (lighter on a dark theme, darker on a light one), one small step at a time, and stops
/// at the first value readable on every ground. A theme colour that already passes is returned
/// unchanged, byte for byte.</para>
/// <para>Text is drawn in list rows as well as on the window and on cards, so a text colour must
/// also read on a hovered row (the selection colour) and on a selected row (the accent at
/// <see cref="SelectedRowOpacity"/> over the window). Checked on the window and the card only, the
/// error text was 4.06 to 1 on a hovered row on Magellan and 3.63 on Cinder.</para>
/// <para>Each pair carries its own target: text asks 4.5 to 1 (WCAG 1.4.3), a boundary line that
/// identifies a region asks 3 to 1 (WCAG 1.4.11). On Drakul the border colour is the card colour
/// itself, 1.00 to 1 on a card.</para>
/// <para>A status dot is a graphic that carries state, drawn in list rows: the indicator colours
/// reach 3 to 1 on the same four grounds as text (WCAG 1.4.11). The raw Dracula error red was 2.91
/// to 1 on a hovered row. The neutral indicator, for a session with no status to show, starts from
/// the border colour, which is the card colour on several themes.</para>
/// <para>The primary button's label is the background colour, fixed. ThemeForge lightens the
/// accent for the hover fill on every theme, and on a light one that leaves no label colour, light
/// or dark, at 4.5 to 1 (Parchment with the green accent: 3.64 and 4.30). The hover fill is the
/// theme's hover shade where the label reads on it, the pressed shade where it does not.</para>
/// <para>ThemeForge applies an accent tint to the accent brushes only, never to
/// <c>AccentColor</c>. Every accent derived here starts from the accent the application paints,
/// read through <c>AccentBrush</c>, so accent text, the selected-row tint and the accent outline
/// follow the tint; derived from <c>AccentColor</c>, the accent text kept the theme's accent in 120
/// of the 136 tinted theme and tint pairs, and the red tint left accent borders at 2.91 to 1 on a
/// Drakul card.</para>
/// </remarks>
internal static class ThemeTextColours
{
    /// <summary>The WCAG 1.4.3 minimum for body text.</summary>
    internal const double MinimumContrast = 4.5;

    /// <summary>The WCAG 1.4.11 minimum for a boundary that identifies a component or a region.</summary>
    internal const double MinimumBoundaryContrast = 3.0;

    /// <summary>The theme key of the window background.</summary>
    internal const string BackgroundKey = "BackgroundColor";

    /// <summary>The theme key of the card surface, the bridge's <c>CardBrush</c>.</summary>
    internal const string CardKey = "SurfaceAltColor";

    /// <summary>The theme key of the hovered row, the bridge's <c>HighlightBrush</c>.</summary>
    internal const string SelectionKey = "SelectionColor";

    /// <summary>The theme key of the accent, which tints a selected row.</summary>
    internal const string AccentKey = "AccentColor";

    /// <summary>
    /// The opacity of the accent over the window on a selected row: the bridge's
    /// <c>TreeRowSelectedBrush</c>, which a test holds to this value.
    /// </summary>
    internal const double SelectedRowOpacity = 0.18;

    /// <summary>The resource key the primary button's hover fill is written to.</summary>
    internal const string AccentHoverFillKey = "AccentHoverFillColor";

    /// <summary>
    /// The resource key the accent the application paints is written to: the accent tint's
    /// colour when one is chosen, the theme's accent otherwise.
    /// </summary>
    internal const string ActiveAccentKey = "ActiveAccentColor";

    /// <summary>The resource key the accent outline (focus rings, selection and focus borders) is written to.</summary>
    internal const string AccentBorderKey = "AccentBorderColor";

    /// <summary>How far the lightness moves per step, on the 0 to 1 HSL scale.</summary>
    private const double LightnessStep = 0.01;

    /// <summary>Enough steps to walk the whole lightness scale from either end.</summary>
    private const int MaximumSteps = 100;

    private const double LinearThreshold = 0.03928;
    private const double LinearDivisor = 12.92;
    private const double GammaOffset = 0.055;
    private const double GammaDivisor = 1.055;
    private const double GammaExponent = 2.4;
    private const double LuminanceFlare = 0.05;
    private const double RedWeight = 0.2126;
    private const double GreenWeight = 0.7152;
    private const double BlueWeight = 0.0722;
    private const double ChannelMaximum = 255.0;

    /// <summary>
    /// Each theme colour, the resource key of the colour derived from it, the contrast the derived
    /// colour must reach, and whether it is drawn in rows (text) or only around regions (the strong
    /// border, on the window and on a card).
    /// </summary>
    internal static IReadOnlyList<(string SourceKey, string TextKey, double Target, bool InRows)> Pairs { get; } =
    [
        ("ErrorColor", "ErrorTextColor", MinimumContrast, true),
        ("WarningColor", "WarningTextColor", MinimumContrast, true),
        ("SuccessColor", "SuccessTextColor", MinimumContrast, true),
        ("InfoColor", "InfoTextColor", MinimumContrast, true),
        ("AccentColor", "AccentTextColor", MinimumContrast, true),
        ("TextSecondaryColor", "SecondaryTextColor", MinimumContrast, true),
        ("BorderColor", "BorderStrongColor", MinimumBoundaryContrast, false),
        ("TextSecondaryColor", "InputBorderColor", MinimumBoundaryContrast, false),
        ("BlueColor", "JwtHeaderTextColor", MinimumContrast, false),
        ("GreenColor", "JwtPayloadTextColor", MinimumContrast, false),
        ("OrangeColor", "JwtSignatureTextColor", MinimumContrast, false),
        ("ErrorColor", "ErrorIndicatorColor", MinimumBoundaryContrast, true),
        ("WarningColor", "WarningIndicatorColor", MinimumBoundaryContrast, true),
        ("SuccessColor", "SuccessIndicatorColor", MinimumBoundaryContrast, true),
        ("InfoColor", "InfoIndicatorColor", MinimumBoundaryContrast, true),
        ("BorderColor", "NeutralIndicatorColor", MinimumBoundaryContrast, true),
    ];

    /// <summary>
    /// Writes every derived text colour into <paramref name="target"/>, reading the theme
    /// colours through <paramref name="find"/>. A pair whose colours the theme does not define is
    /// left as it was.
    /// </summary>
    /// <param name="target">The dictionary the bridge's dynamic references resolve against.</param>
    /// <param name="find">Looks a resource up in the active theme.</param>
    internal static void Apply(ResourceDictionary target, Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(find);

        if (find(BackgroundKey) is not Color background || find(CardKey) is not Color card)
        {
            return;
        }

        bool lighten = ThemeLuminance.IsDark(background);
        Color? activeAccent = ActiveAccent(find);
        Color[] regions = [background, card];
        Color[] rows = RowGrounds(background, card, find);
        foreach ((string sourceKey, string textKey, double contrast, bool inRows) in Pairs)
        {
            Color? source = sourceKey == AccentKey ? activeAccent : find(sourceKey) as Color?;
            if (source is Color colour)
            {
                target[textKey] = Derive(colour, inRows ? rows : regions, lighten, contrast);
            }
        }

        if (activeAccent is Color accent)
        {
            target[ActiveAccentKey] = accent;
            target[AccentBorderKey] = Derive(accent, regions, lighten, MinimumBoundaryContrast);
        }

        if (AccentHoverFill(background, find) is Color hoverFill)
        {
            target[AccentHoverFillKey] = hoverFill;
        }
    }

    /// <summary>
    /// The grounds text is drawn on: the window, a card, a hovered row and a selected row. A theme
    /// without a selection or an accent colour gives the grounds it has.
    /// </summary>
    /// <param name="background">The window background.</param>
    /// <param name="card">The card surface.</param>
    /// <param name="find">Looks a resource up in the active theme.</param>
    internal static Color[] RowGrounds(Color background, Color card, Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);

        List<Color> grounds = [background, card];
        if (find(SelectionKey) is Color selection)
        {
            grounds.Add(selection);
        }

        if (ActiveAccent(find) is Color accent)
        {
            grounds.Add(Over(accent, SelectedRowOpacity, background));
        }

        return [.. grounds];
    }

    /// <summary>
    /// The accent the application paints: <c>AccentBrush</c> as the application resolves it, which
    /// an accent tint replaces, else the theme's <c>AccentColor</c>.
    /// </summary>
    /// <param name="find">Looks a resource up in the application.</param>
    internal static Color? ActiveAccent(Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        return ColourOf(find("AccentBrush")) ?? ColourOf(find(AccentKey));
    }

    /// <summary>
    /// The primary button's hover fill: the first of the hover, pressed and rest accent shades the
    /// label reads on at <see cref="MinimumContrast"/>, the most readable of them when none does.
    /// The shades are read as the application resolves them, so an accent tint's own shades are
    /// the ones measured.
    /// </summary>
    /// <param name="label">The label colour, the bridge's <c>TextOnAccentBrush</c>.</param>
    /// <param name="find">Looks a resource up in the application.</param>
    internal static Color? AccentHoverFill(Color label, Func<string, object?> find)
    {
        ArgumentNullException.ThrowIfNull(find);

        Color?[] shades =
        [
            ColourOf(find("AccentHoverBrush")) ?? ColourOf(find("AccentHoverColor")),
            ColourOf(find("AccentPressedBrush")) ?? ColourOf(find("AccentPressedColor")),
            ColourOf(find("AccentBrush")) ?? ColourOf(find(AccentKey)),
        ];
        Color[] known = [.. shades.Where(shade => shade.HasValue).Select(shade => shade!.Value)];
        if (known.Length == 0)
        {
            return null;
        }

        foreach (Color shade in known)
        {
            if (ContrastRatio(label, shade) >= MinimumContrast)
            {
                return shade;
            }
        }

        return known.MaxBy(shade => ContrastRatio(label, shade));
    }

    /// <summary>
    /// The colour closest to <paramref name="colour"/>, same hue and saturation, that reaches
    /// <paramref name="target"/> against every ground; the colour itself when it already does.
    /// </summary>
    /// <param name="colour">The theme's own colour for the meaning.</param>
    /// <param name="grounds">The opaque colours it is drawn on.</param>
    /// <param name="lighten">Whether to move lighter (a dark theme) or darker (a light one).</param>
    /// <param name="target">The contrast to reach; <see cref="MinimumContrast"/> for text.</param>
    internal static Color Derive(Color colour, IReadOnlyList<Color> grounds, bool lighten, double target = MinimumContrast)
    {
        ArgumentNullException.ThrowIfNull(grounds);

        if (IsReadable(colour, grounds, target))
        {
            return colour;
        }

        (double hue, double saturation, double lightness) = ToHsl(colour);
        Color candidate = colour;
        for (int step = 1; step <= MaximumSteps; step++)
        {
            double moved = lighten
                ? Math.Min(1.0, lightness + (step * LightnessStep))
                : Math.Max(0.0, lightness - (step * LightnessStep));
            candidate = FromHsl(hue, saturation, moved, colour.A);
            if (IsReadable(candidate, grounds, target))
            {
                return candidate;
            }
        }

        return candidate;
    }

    /// <summary>The WCAG 2 contrast ratio between two opaque colours, from 1 to 21.</summary>
    /// <param name="first">One colour.</param>
    /// <param name="second">The other.</param>
    internal static double ContrastRatio(Color first, Color second)
    {
        double a = RelativeLuminance(first);
        double b = RelativeLuminance(second);
        return (Math.Max(a, b) + LuminanceFlare) / (Math.Min(a, b) + LuminanceFlare);
    }

    private static bool IsReadable(Color colour, IReadOnlyList<Color> grounds, double target) =>
        grounds.All(ground => ContrastRatio(colour, ground) >= target);

    /// <summary>A brush's or a colour resource's colour; null for anything else.</summary>
    private static Color? ColourOf(object? resource) => resource switch
    {
        Color colour => colour,
        SolidColorBrush brush => brush.Color,
        _ => null,
    };

    /// <summary>
    /// <paramref name="top"/> at <paramref name="opacity"/> as drawn over an opaque colour, the
    /// opacity taken as the alpha byte it rounds to.
    /// </summary>
    private static Color Over(Color top, double opacity, Color under)
    {
        double alpha = Math.Round(opacity * ChannelMaximum) / ChannelMaximum;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * alpha) + (u * (1 - alpha)));
        return new Color { A = under.A, R = Mix(top.R, under.R), G = Mix(top.G, under.G), B = Mix(top.B, under.B) };
    }

    private static double RelativeLuminance(Color colour) =>
        (RedWeight * Linear(colour.R)) + (GreenWeight * Linear(colour.G)) + (BlueWeight * Linear(colour.B));

    private static double Linear(byte channel)
    {
        double c = channel / ChannelMaximum;
        return c <= LinearThreshold ? c / LinearDivisor : Math.Pow((c + GammaOffset) / GammaDivisor, GammaExponent);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color colour)
    {
        double r = colour.R / ChannelMaximum;
        double g = colour.G / ChannelMaximum;
        double b = colour.B / ChannelMaximum;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2.0;
        double delta = max - min;
        if (delta == 0)
        {
            return (0, 0, lightness);
        }

        double saturation = lightness > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
        double hue;
        if (max == r)
        {
            hue = ((g - b) / delta) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            hue = ((b - r) / delta) + 2;
        }
        else
        {
            hue = ((r - g) / delta) + 4;
        }

        return (hue / 6.0, saturation, lightness);
    }

    private static Color FromHsl(double hue, double saturation, double lightness, byte alpha)
    {
        if (saturation == 0)
        {
            byte grey = ToByte(lightness);
            return new Color { A = alpha, R = grey, G = grey, B = grey };
        }

        double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - (lightness * saturation);
        double p = (2 * lightness) - q;
        return new Color
        {
            A = alpha,
            R = ToByte(HueToChannel(p, q, hue + (1.0 / 3.0))),
            G = ToByte(HueToChannel(p, q, hue)),
            B = ToByte(HueToChannel(p, q, hue - (1.0 / 3.0))),
        };
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1.0 / 6.0)
        {
            return p + ((q - p) * 6 * t);
        }

        if (t < 1.0 / 2.0)
        {
            return q;
        }

        if (t < 2.0 / 3.0)
        {
            return p + ((q - p) * ((2.0 / 3.0) - t) * 6);
        }

        return p;
    }

    private static byte ToByte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0.0, 1.0) * ChannelMaximum);
}
