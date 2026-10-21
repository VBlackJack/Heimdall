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
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Theming;

/// <summary>
/// The colours the theme bridge paints semantic and accent text with, derived from the active
/// theme so that each one is readable on the window background and on a card.
/// </summary>
/// <remarks>
/// <para>A ThemeForge palette gives one colour per meaning (error, warning, success, info,
/// accent) and that colour is tuned for fills, badges and borders. As text it can fall short:
/// on Drakul the error red is 2.91 to 1 on the card, the accent 3.79 to 1.</para>
/// <para>The derivation keeps the hue and saturation and only moves the lightness, away from the
/// grounds (lighter on a dark theme, darker on a light one), one small step at a time, and stops
/// at the first value readable on both grounds. A theme colour that already passes is returned
/// unchanged, byte for byte.</para>
/// </remarks>
internal static class ThemeTextColours
{
    /// <summary>The WCAG 1.4.3 minimum for body text.</summary>
    internal const double MinimumContrast = 4.5;

    /// <summary>The theme key of the window background.</summary>
    internal const string BackgroundKey = "BackgroundColor";

    /// <summary>The theme key of the card surface, the bridge's <c>CardBrush</c>.</summary>
    internal const string CardKey = "SurfaceAltColor";

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
    /// Each theme colour and the resource key of the text colour derived from it.
    /// </summary>
    internal static IReadOnlyList<(string SourceKey, string TextKey)> Pairs { get; } =
    [
        ("ErrorColor", "ErrorTextColor"),
        ("WarningColor", "WarningTextColor"),
        ("SuccessColor", "SuccessTextColor"),
        ("InfoColor", "InfoTextColor"),
        ("AccentColor", "AccentTextColor"),
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

        foreach ((string sourceKey, string textKey) in Pairs)
        {
            if (find(sourceKey) is Color source)
            {
                target[textKey] = Derive(source, background, card);
            }
        }
    }

    /// <summary>
    /// The colour closest to <paramref name="colour"/>, same hue and saturation, that reaches
    /// <see cref="MinimumContrast"/> against both grounds; the colour itself when it already does.
    /// </summary>
    /// <param name="colour">The theme's own colour for the meaning.</param>
    /// <param name="background">The window background.</param>
    /// <param name="card">The card surface.</param>
    internal static Color Derive(Color colour, Color background, Color card)
    {
        if (IsReadable(colour, background, card))
        {
            return colour;
        }

        bool lighten = ThemeLuminance.IsDark(background);
        (double hue, double saturation, double lightness) = ToHsl(colour);
        Color candidate = colour;
        for (int step = 1; step <= MaximumSteps; step++)
        {
            double moved = lighten
                ? Math.Min(1.0, lightness + (step * LightnessStep))
                : Math.Max(0.0, lightness - (step * LightnessStep));
            candidate = FromHsl(hue, saturation, moved, colour.A);
            if (IsReadable(candidate, background, card))
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

    private static bool IsReadable(Color colour, Color background, Color card) =>
        ContrastRatio(colour, background) >= MinimumContrast
        && ContrastRatio(colour, card) >= MinimumContrast;

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
