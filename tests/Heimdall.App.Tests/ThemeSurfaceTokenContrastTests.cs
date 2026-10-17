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
using System.IO;
using System.Windows;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Theming;
using ThemeForge.Theme;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Tests;

/// <summary>
/// What the theme bridge's surface and text tokens give on each of the 17 ThemeForge themes:
/// tertiary text no fainter than secondary text, the input border visible (WCAG 1.4.11, 3 to 1),
/// primary text readable on a hovered row (1.4.3, 4.5 to 1).
/// </summary>
/// <remarks>
/// The bridge is read from its XAML (colour key and opacity) and the colours from each theme,
/// and a translucent brush is blended over what it is drawn on, as WPF draws it.
/// </remarks>
public sealed class ThemeSurfaceTokenContrastTests
{
    private const string ThemeUriFormat = "/ThemeForge.Theme;component/Themes/{0}.xaml";

    public static TheoryData<string> Themes { get; } = new(ThemeNames.All);

    /// <summary>
    /// The third tier of text is never fainter than the second. #608 drew it in the comment
    /// colour at 82 percent, 2.49 to 1 on Dracula. The second tier itself, ThemeForge's
    /// TextSecondaryColor, is 3.03 to 1 on Dracula, under the 4.5 WCAG asks for: that gap is in
    /// the palette, not here, and this test does not claim 4.5.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TertiaryTextIsNoFainterThanSecondaryText(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color secondary = ColorOf(palette, "TextSecondaryBrush");
        Color tertiary = ColorOf(palette, "TextTertiaryBrush");

        foreach (string ground in new[] { "BackgroundBrush", "SurfaceBrush" })
        {
            Color under = ColorOf(palette, ground);
            Assert.True(
                Contrast(Over(tertiary, under), under) >= Contrast(Over(secondary, under), under) - 0.01,
                Report(theme, "tertiary on " + ground, tertiary, under) + "; secondary is " + Report(theme, "secondary", secondary, under));
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void AnInputBorderIsVisible(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color background = ColorOf(palette, "BackgroundBrush");
        Color border = ColorOf(palette, "InputBorderBrush");

        Assert.True(Contrast(Over(border, background), background) >= 3.0, Report(theme, "input border", border, background));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void PrimaryTextIsReadableOnAHoveredRow(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color background = ColorOf(palette, "BackgroundBrush");
        Color highlight = Over(ColorOf(palette, "HighlightBrush"), background);
        Color text = ColorOf(palette, "TextPrimaryBrush");

        Assert.True(Contrast(Over(text, highlight), highlight) >= 4.5, Report(theme, "primary on hover", text, highlight));
    }

    [Fact]
    public void TheTitleBarGetsItsColoursInTheOrderDwmReadsThem()
    {
        // COLORREF is 0x00BBGGRR: red in the low byte.
        Assert.Equal(0x00332211, WindowThemeHelper.ToColorRef(Color.FromRgb(0x11, 0x22, 0x33)));
    }

    private static string Report(string theme, string what, Color foreground, Color background) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} {2} on {3} is {4:F2} to 1",
            theme,
            what,
            foreground,
            background,
            Contrast(Over(foreground, background), background));

    /// <summary>
    /// One theme's colours, under the bridge's brushes: each brush named by the bridge is
    /// rebuilt from the colour key and opacity the bridge gives it.
    /// </summary>
    private static ResourceDictionary Palette(string theme)
    {
        var colours = (ResourceDictionary)Application.LoadComponent(
            new Uri(string.Format(CultureInfo.InvariantCulture, ThemeUriFormat, theme), UriKind.Relative));
        ResourceDictionary resolved = new();
        foreach ((string key, string colourKey, double opacity) in BridgeBrushes())
        {
            if (colours[colourKey] is Color colour)
            {
                resolved[key] = Color.FromArgb((byte)Math.Round(colour.A * opacity), colour.R, colour.G, colour.B);
            }
        }

        return resolved;
    }

    private static IEnumerable<(string Key, string ColourKey, double Opacity)> BridgeBrushes()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument bridge = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "HeimdallThemeBridge.xaml"));

        foreach (XElement brush in bridge.Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            string? key = (string?)brush.Attribute(x + "Key");
            string? colour = (string?)brush.Attribute("Color");
            if (key is null || colour is null || !colour.StartsWith("{DynamicResource ", StringComparison.Ordinal))
            {
                continue;
            }

            double opacity = double.TryParse((string?)brush.Attribute("Opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 1.0;
            yield return (key, colour["{DynamicResource ".Length..^1].Trim(), opacity);
        }
    }

    private static Color ColorOf(ResourceDictionary palette, string key) => Assert.IsType<Color>(palette[key]);

    /// <summary>A translucent colour as drawn over an opaque one.</summary>
    private static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * a) + (u * (1 - a)));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color) =>
        (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
