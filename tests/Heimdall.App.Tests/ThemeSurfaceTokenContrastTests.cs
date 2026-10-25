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
    private const double MinimumTextContrast = 4.5;
    private const double MinimumBoundaryContrast = 3.0;

    private static readonly string[] SemanticTextBrushes =
    [
        "ErrorTextBrush",
        "WarningTextBrush",
        "SuccessTextBrush",
        "InfoTextBrush",
        "AccentTextBrush",
    ];

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

    /// <summary>
    /// Semantic and accent text reads at 4.5 to 1 on the window background and on a card. The
    /// bridge used to paint ErrorTextBrush with the theme's ErrorColor itself: #FF5555 on the
    /// Drakul card #44475A is 2.91 to 1, and the accent #BD93F9 there is 3.79 to 1.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void SemanticAndAccentTextIsReadableOnTheBackgroundAndOnACard(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        List<string> unreadable = [];

        foreach (string text in SemanticTextBrushes)
        {
            if (!palette.Contains(text))
            {
                unreadable.Add($"{theme}: the bridge resolves no {text}");
                continue;
            }

            Color foreground = ColorOf(palette, text);
            foreach (string ground in new[] { "BackgroundBrush", "CardBrush" })
            {
                Color under = ColorOf(palette, ground);
                if (Contrast(Over(foreground, under), under) < MinimumTextContrast)
                {
                    unreadable.Add(Report(theme, text + " on " + ground, foreground, under));
                }
            }
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>Text drawn on an accent fill, a primary button's label, stays readable.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TextOnAccentIsReadableOnTheAccent(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color accent = ColorOf(palette, "AccentBrush");
        Color text = ColorOf(palette, "TextOnAccentBrush");

        Assert.True(Contrast(Over(text, accent), accent) >= MinimumTextContrast, Report(theme, "text on accent", text, accent));
    }

    /// <summary>
    /// A line that carries a boundary (the sidebar divider, a grid header separator, a section
    /// card) reaches 3 to 1 on the window background and on a card, WCAG 1.4.11. The theme's own
    /// border does not: on Drakul BorderColor is the card colour #44475A, 1.00 to 1 on a card and
    /// 1.56 on the background. Where the theme's border already passes, the strong border is that
    /// same colour.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void AStrongBorderIsVisibleOnTheBackgroundAndOnACard(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Assert.True(palette.Contains("BorderStrongBrush"), $"{theme}: the bridge resolves no BorderStrongBrush");

        Color border = ColorOf(palette, "BorderBrush");
        Color strong = ColorOf(palette, "BorderStrongBrush");
        List<string> faint = [];
        bool borderPasses = true;
        foreach (string ground in new[] { "BackgroundBrush", "CardBrush" })
        {
            Color under = ColorOf(palette, ground);
            borderPasses &= Contrast(Over(border, under), under) >= MinimumBoundaryContrast;
            if (Contrast(Over(strong, under), under) < MinimumBoundaryContrast)
            {
                faint.Add(Report(theme, "BorderStrongBrush on " + ground, strong, under));
            }
        }

        Assert.True(faint.Count == 0, string.Join(Environment.NewLine, faint));
        if (borderPasses)
        {
            Assert.True(border == strong, $"{theme}: BorderColor {border} already passes and was changed to {strong}");
        }
    }

    /// <summary>
    /// A theme colour that already reaches its pair's target on both grounds (4.5 to 1 for text,
    /// 3 to 1 for the strong border) is used as it is, byte for byte; one that does not is moved,
    /// and only then.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void ADerivedTextColourIsTheThemeColourWheneverThatAlreadyPasses(string theme)
    {
        ResourceDictionary colours = ThemeColours(theme);
        Color background = Assert.IsType<Color>(colours[ThemeTextColours.BackgroundKey]);
        Color card = Assert.IsType<Color>(colours[ThemeTextColours.CardKey]);
        ResourceDictionary derived = new();
        ThemeTextColours.Apply(derived, key => colours[key]);

        foreach ((string sourceKey, string textKey, double target) in ThemeTextColours.Pairs)
        {
            Color source = Assert.IsType<Color>(colours[sourceKey]);
            Color text = Assert.IsType<Color>(derived[textKey]);
            bool passes = Contrast(source, background) >= target && Contrast(source, card) >= target;

            if (passes)
            {
                Assert.True(source == text, $"{theme}: {sourceKey} {source} already passes and was changed to {text}");
            }
            else
            {
                Assert.True(source != text, $"{theme}: {sourceKey} {source} does not pass and was kept");
            }
        }
    }

    /// <summary>
    /// The derivation is not vacuous: across the shipped themes it keeps some colours and moves
    /// others. On Drakul the error red #FF5555 becomes #FF9C9C, the first step of one lightness
    /// percent that passes: 4.57 to 1 on the card and 7.11 on the background, where the step
    /// below it, #FF9999, is under 4.5 on the card.
    /// </summary>
    [Fact]
    public void TheDerivationKeepsSomeThemeColoursAndMovesOthers()
    {
        int kept = 0;
        int moved = 0;
        foreach (string theme in ThemeNames.All)
        {
            ResourceDictionary colours = ThemeColours(theme);
            ResourceDictionary derived = new();
            ThemeTextColours.Apply(derived, key => colours[key]);
            foreach ((string sourceKey, string textKey, _) in ThemeTextColours.Pairs)
            {
                if ((Color)colours[sourceKey] == (Color)derived[textKey])
                {
                    kept++;
                }
                else
                {
                    moved++;
                }
            }
        }

        Assert.True(kept > 0, "no theme colour was kept");
        Assert.True(moved > 0, "no theme colour was moved");
        Assert.Equal(Color.FromRgb(0xFF, 0x9C, 0x9C), ColorOf(Palette(ThemeNames.Drakul), "ErrorTextBrush"));
    }

    /// <summary>
    /// A theme switch recomputes the derived colours from the new theme rather than keeping the
    /// previous theme's: the same dictionary, applied after Drakul then after Parchment, ends
    /// with Parchment's values.
    /// </summary>
    [Fact]
    public void ASwitchOfThemeRecomputesTheDerivedTextColours()
    {
        ResourceDictionary application = new();
        application.MergedDictionaries.Add(ThemeColours(ThemeNames.Drakul));
        ThemeTextColours.Apply(application, key => application[key]);
        Color drakulError = Assert.IsType<Color>(application["ErrorTextColor"]);

        application.MergedDictionaries.Clear();
        ResourceDictionary parchment = ThemeColours(ThemeNames.Parchment);
        application.MergedDictionaries.Add(parchment);
        ThemeTextColours.Apply(application, key => application[key]);
        Color parchmentError = Assert.IsType<Color>(application["ErrorTextColor"]);

        Assert.NotEqual(drakulError, parchmentError);
        Assert.Equal(
            ThemeTextColours.Derive(
                (Color)parchment["ErrorColor"],
                (Color)parchment[ThemeTextColours.BackgroundKey],
                (Color)parchment[ThemeTextColours.CardKey]),
            parchmentError);
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
    /// rebuilt from the colour key and opacity the bridge gives it, after the derived text
    /// colours are written beside the theme's own, as the theme service writes them.
    /// </summary>
    internal static ResourceDictionary Palette(string theme)
    {
        ResourceDictionary colours = ThemeColours(theme);
        ThemeTextColours.Apply(colours, key => colours[key]);
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

    /// <summary>One theme's own colours, as ThemeForge ships them.</summary>
    internal static ResourceDictionary ThemeColours(string theme) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri(string.Format(CultureInfo.InvariantCulture, ThemeUriFormat, theme), UriKind.Relative));

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

    internal static Color ColorOf(ResourceDictionary palette, string key) => Assert.IsType<Color>(palette[key]);

    /// <summary>A translucent colour as drawn over an opaque one.</summary>
    internal static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round((t * a) + (u * (1 - a)));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    internal static double Contrast(Color a, Color b)
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
