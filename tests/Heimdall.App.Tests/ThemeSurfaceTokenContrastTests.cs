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
using System.Reflection;
using System.Windows;
using System.Xml;
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
        "TextSecondaryBrush",
        "TextTertiaryBrush",
    ];

    public static TheoryData<string> Themes { get; } = new(ThemeNames.All);

    /// <summary>
    /// The third tier of text is never fainter than the second. #608 drew it in the comment
    /// colour at 82 percent, 2.49 to 1 on Dracula. Both tiers are now the derived secondary text
    /// colour, which the semantic text test holds at 4.5 on every ground.
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

    /// <summary>
    /// An input's outline reads at 3 to 1 on the window and on a card, where most inputs and the
    /// secondary buttons sit. Measured on the window only, the raw Dracula colour passed at 3.03
    /// while it was 1.94 to 1 on every Settings card.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void AnInputBorderIsVisible(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color border = ColorOf(palette, "InputBorderBrush");
        List<string> faint = [];

        foreach (string ground in new[] { "BackgroundBrush", "CardBrush" })
        {
            Color under = ColorOf(palette, ground);
            if (Contrast(Over(border, under), under) < MinimumBoundaryContrast)
            {
                faint.Add(Report(theme, "input border on " + ground, border, under));
            }
        }

        Assert.True(faint.Count == 0, string.Join(Environment.NewLine, faint));
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
    /// Semantic and accent text reads at 4.5 to 1 on the window background, on a card, on a
    /// hovered row and on a selected row. The bridge used to paint ErrorTextBrush with the theme's
    /// ErrorColor itself: #FF5555 on the Drakul card #44475A is 2.91 to 1, and the accent #BD93F9
    /// there is 3.79 to 1. Derived for the window and the card only, the error text was 4.06 to 1
    /// on a hovered row on Magellan and 3.63 on Cinder.
    /// </summary>
    /// <remarks>
    /// The two row grounds are the bridge's own brushes, HighlightBrush and TreeRowSelectedBrush,
    /// drawn over the window as the session tree draws them, read from the bridge's XAML.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Themes))]
    public void SemanticAndAccentTextIsReadableOnTheBackgroundOnACardAndOnRows(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color background = ColorOf(palette, "BackgroundBrush");
        (string Name, Color Ground)[] grounds =
        [
            ("BackgroundBrush", background),
            ("CardBrush", ColorOf(palette, "CardBrush")),
            ("HighlightBrush over BackgroundBrush", Over(ColorOf(palette, "HighlightBrush"), background)),
            ("TreeRowSelectedBrush over BackgroundBrush", Over(ColorOf(palette, "TreeRowSelectedBrush"), background)),
        ];
        List<string> unreadable = [];

        foreach (string text in SemanticTextBrushes)
        {
            if (!palette.Contains(text))
            {
                unreadable.Add($"{theme}: the bridge resolves no {text}");
                continue;
            }

            Color foreground = ColorOf(palette, text);
            foreach ((string name, Color under) in grounds)
            {
                if (Contrast(Over(foreground, under), under) < MinimumTextContrast)
                {
                    unreadable.Add(Report(theme, text + " on " + name, foreground, under));
                }
            }
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// A status dot reads at 3 to 1 on the window, a card, a hovered row and a selected row, for
    /// every status it can show. The raw Dracula error red was 2.91 to 1 on a hovered row, and the
    /// neutral ring of a local session was the decorative border colour, the card colour itself on
    /// several themes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void StatusIndicatorsAreVisibleOnTheBackgroundOnACardAndOnRows(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color background = ColorOf(palette, "BackgroundBrush");
        (string Name, Color Ground)[] grounds =
        [
            ("BackgroundBrush", background),
            ("CardBrush", ColorOf(palette, "CardBrush")),
            ("HighlightBrush over BackgroundBrush", Over(ColorOf(palette, "HighlightBrush"), background)),
            ("TreeRowSelectedBrush over BackgroundBrush", Over(ColorOf(palette, "TreeRowSelectedBrush"), background)),
        ];
        List<string> faint = [];

        foreach (string indicator in IndicatorBrushes)
        {
            Assert.True(palette.Contains(indicator), $"{theme}: the bridge resolves no {indicator}");
            Color dot = ColorOf(palette, indicator);
            foreach ((string name, Color under) in grounds)
            {
                if (Contrast(Over(dot, under), under) < MinimumBoundaryContrast)
                {
                    faint.Add(Report(theme, indicator + " on " + name, dot, under));
                }
            }
        }

        Assert.True(faint.Count == 0, string.Join(Environment.NewLine, faint));
    }

    private static readonly string[] IndicatorBrushes =
        ["SuccessIndicatorBrush", "WarningIndicatorBrush", "ErrorIndicatorBrush", "InfoIndicatorBrush", "NeutralIndicatorBrush"];

    /// <summary>
    /// The JWT parser's part colours (titles, outlines and decoded text) read at 4.5 to 1 on the
    /// window and on a card. On the raw blue, green and orange the header was 4.00 to 1 on a Drakul
    /// card, the payload 4.20 on a Folio card and the signature 4.43 on a Tarn card.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheJwtPartColoursAreReadable(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        List<string> unreadable = [];

        foreach (string brush in new[] { "JwtHeaderBrush", "JwtPayloadBrush", "JwtSignatureBrush" })
        {
            Color text = ColorOf(palette, brush);
            foreach (string ground in new[] { "BackgroundBrush", "CardBrush" })
            {
                Color under = ColorOf(palette, ground);
                if (Contrast(Over(text, under), under) < MinimumTextContrast)
                {
                    unreadable.Add(Report(theme, brush + " on " + ground, text, under));
                }
            }
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The RDP toolbar's health dot paints a derived indicator in every state, the neutral one at
    /// rest. At rest it was TextDisabledBrush, 1.85 to 1 on the toolbar on 15 themes, and its
    /// states were the raw semantic brushes.
    /// </summary>
    [Fact]
    public void TheRdpHealthDotPaintsTheDerivedIndicators()
    {
        MethodInfo resolve = typeof(Heimdall.App.Views.EmbeddedRdpView).GetMethod(
            "ResolveHealthDotBrushKey", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("EmbeddedRdpView.ResolveHealthDotBrushKey was not found");

        foreach (Heimdall.App.Views.EmbeddedRdp.RdpHealthDotState state in Enum.GetValues<Heimdall.App.Views.EmbeddedRdp.RdpHealthDotState>())
        {
            Assert.Contains((string)resolve.Invoke(null, [state])!, IndicatorBrushes);
        }

        XDocument view = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach ((string name, string property) in new[] { ("HealthDotColor", "Background"), ("HealthDotGlyph", "Foreground") })
        {
            XElement element = view.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
            Assert.Equal("{DynamicResource NeutralIndicatorBrush}", (string?)element.Attribute(property));
        }
    }

    /// <summary>The raw semantic brushes, which the theme draws as it is with no contrast floor.</summary>
    private static readonly string[] RawSemanticBrushes =
        ["{DynamicResource ErrorBrush}", "{DynamicResource WarningBrush}", "{DynamicResource SuccessBrush}", "{DynamicResource InfoBrush}"];

    /// <summary>How many status outlines and glyphs the views draw today; the scan must reach them.</summary>
    private const int MinimumStatusOutlinesAndGlyphs = 38;

    /// <summary>
    /// A status outline or glyph (BorderBrush, Fill or Stroke, attributes and setters) uses a
    /// derived *IndicatorBrush, never the raw semantic brush: the raw Dracula red was 2.91 to 1 on
    /// a card around the host key warning, an input in error and the danger buttons. The one
    /// exception is an outline drawn on its own fill, the danger button's: its label identifies it.
    /// </summary>
    [Fact]
    public void StatusOutlinesAndGlyphsUseTheDerivedIndicators()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        int indicators = 0;
        List<string> raw = [];
        foreach (string path in SourceFileEnumeration.EnumerateFiles(root, "*.xaml"))
        {
            foreach (XElement element in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants())
            {
                IEnumerable<(string Property, string? Value)> uses = element.Name.LocalName == "Setter"
                    && (string?)element.Attribute("Property") is "BorderBrush" or "Fill" or "Stroke"
                        ? [((string)element.Attribute("Property")!, (string?)element.Attribute("Value"))]
                        : new[] { "BorderBrush", "Fill", "Stroke" }.Select(name => (name, (string?)element.Attribute(name)));
                foreach ((string property, string? value) in uses.Where(use => use.Value is not null))
                {
                    if (value!.Contains("IndicatorBrush}", StringComparison.Ordinal))
                    {
                        indicators++;
                    }
                    else if (RawSemanticBrushes.Contains(value) && !IsOutlineOnItsOwnFill(element, property, value))
                    {
                        raw.Add($"{Path.GetRelativePath(root, path)}:{((IXmlLineInfo)element).LineNumber} {property} {value}");
                    }
                }
            }
        }

        Assert.True(raw.Count == 0, string.Join(Environment.NewLine, raw));
        Assert.True(indicators >= MinimumStatusOutlinesAndGlyphs, $"the scan found only {indicators} indicator outlines and glyphs");
    }

    /// <summary>A BorderBrush the same element or the same setter group also paints as the Background.</summary>
    private static bool IsOutlineOnItsOwnFill(XElement element, string property, string value) =>
        property == "BorderBrush"
        && (element.Name.LocalName == "Setter"
            ? element.Parent?.Elements().Any(setter => setter.Name.LocalName == "Setter"
                && (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == value) == true
            : (string?)element.Attribute("Background") == value);

    /// <summary>
    /// The selected-row ground the derivation measures is the one the bridge paints: the accent at
    /// the opacity of <c>TreeRowSelectedBrush</c>. Changing one without the other would derive text
    /// colours for a row the tree no longer draws.
    /// </summary>
    [Fact]
    public void TheDerivationsSelectedRowIsTheBridgesSelectedRowBrush()
    {
        (string _, string colourKey, double opacity) = Assert.Single(BridgeBrushes(), brush => brush.Key == "TreeRowSelectedBrush");

        Assert.Equal(ThemeTextColours.ActiveAccentKey, colourKey);
        Assert.Equal(ThemeTextColours.SelectedRowOpacity, opacity);
    }

    /// <summary>
    /// The bridge paints every accent surface with the accent the application paints, so a tint
    /// reaches the selected row, the local session badge and the local protocol mark as it reaches
    /// the buttons. ThemeForge tints the accent brushes only; on <c>AccentColor</c> these kept the
    /// theme's accent under any tint.
    /// </summary>
    [Fact]
    public void TheBridgesAccentSurfacesFollowTheActiveAccent()
    {
        Dictionary<string, string> colourKeys = BridgeBrushes().ToDictionary(brush => brush.Key, brush => brush.ColourKey);

        foreach (string brush in new[] { "TreeRowSelectedBrush", "LocalBadgeBrush", "ProtocolLocalBrush" })
        {
            Assert.Equal(ThemeTextColours.ActiveAccentKey, colourKeys[brush]);
        }

        Assert.Equal(ThemeTextColours.AccentBorderKey, colourKeys["AccentBorderBrush"]);
        Assert.DoesNotContain(colourKeys, pair => pair.Value == ThemeTextColours.AccentKey && pair.Key != "AccentBrush");
    }

    /// <summary>
    /// With every accent tint, the accent text reads at 4.5 to 1 on the window, a card, a hovered
    /// row and the tinted selected row, and the accent outline at 3 to 1 on the window and a card.
    /// A tint puts its source colour in AccentBrush (ThemeForge 2.1.0, <c>CreateAccentTintDictionary</c>),
    /// which is what the resolver below hands the derivation. With the red tint the raw accent was
    /// 2.91 to 1 on a Drakul card.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheAccentTextAndOutlineAreReadableWithEveryTint(string theme)
    {
        ResourceDictionary colours = ThemeColours(theme);
        Color background = (Color)colours[ThemeTextColours.BackgroundKey];
        Color card = (Color)colours[ThemeTextColours.CardKey];
        Color hovered = (Color)colours[ThemeTextColours.SelectionKey];
        List<string> unreadable = [];

        foreach (string tint in TintSources)
        {
            Color tinted = (Color)colours[tint];
            ResourceDictionary derived = new();
            ThemeTextColours.Apply(derived, Tinted(colours, tinted));
            Color text = (Color)derived["AccentTextColor"];
            Color outline = (Color)derived[ThemeTextColours.AccentBorderKey];
            Color selected = Over(Color.FromArgb((byte)Math.Round(255 * ThemeTextColours.SelectedRowOpacity), tinted.R, tinted.G, tinted.B), background);

            Assert.Equal(tinted, (Color)derived[ThemeTextColours.ActiveAccentKey]);
            foreach ((string name, Color ground) in new[] { ("window", background), ("card", card), ("hovered row", hovered), ("selected row", selected) })
            {
                if (Contrast(text, ground) < MinimumTextContrast)
                {
                    unreadable.Add(Report(theme, $"{tint} accent text on the {name}", text, ground));
                }
            }

            foreach ((string name, Color ground) in new[] { ("window", background), ("card", card) })
            {
                if (Contrast(outline, ground) < MinimumBoundaryContrast)
                {
                    unreadable.Add(Report(theme, $"{tint} accent outline on the {name}", outline, ground));
                }
            }
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The accent text follows the tint rather than the theme's accent: under a tint it is the
    /// tint's colour, or a step of the tint's own hue, in most theme and tint pairs (120 of 136 on
    /// 2026-10-26; the others are tints whose derived text lands on the theme accent's), and the
    /// outline is the tint itself, unchanged, wherever that already reaches 3 to 1.
    /// </summary>
    [Fact]
    public void TheAccentTextFollowsTheTint()
    {
        int followed = 0;
        foreach (string theme in ThemeNames.All)
        {
            ResourceDictionary colours = ThemeColours(theme);
            ResourceDictionary untinted = new();
            ThemeTextColours.Apply(untinted, key => colours[key]);
            foreach (string tint in TintSources)
            {
                Color tinted = (Color)colours[tint];
                ResourceDictionary derived = new();
                ThemeTextColours.Apply(derived, Tinted(colours, tinted));
                Color text = (Color)derived["AccentTextColor"];
                if (text != (Color)untinted["AccentTextColor"])
                {
                    followed++;
                }

                Color outline = (Color)derived[ThemeTextColours.AccentBorderKey];
                bool tintIsAnOutline = Contrast(tinted, (Color)colours[ThemeTextColours.BackgroundKey]) >= MinimumBoundaryContrast
                    && Contrast(tinted, (Color)colours[ThemeTextColours.CardKey]) >= MinimumBoundaryContrast;
                Assert.True(tintIsAnOutline == (outline == tinted), $"{theme} {tint}: tint {tinted}, outline {outline}");
            }
        }

        Assert.True(followed >= 110, $"the accent text followed the tint in only {followed} of {ThemeNames.All.Count * TintSources.Length} pairs");
    }

    /// <summary>A style's base BorderBrush setter when the same style fills the control with the accent.</summary>
    private static bool IsOutlineOnItsOwnAccentFill(XElement element) =>
        element.Name.LocalName == "Setter"
        && element.Attribute("TargetName") is null
        && element.Parent is { Name.LocalName: "Style" } style
        && style.Elements().Any(setter => setter.Name.LocalName == "Setter"
            && (string?)setter.Attribute("Property") == "Background"
            && (string?)setter.Attribute("Value") == "{DynamicResource AccentBrush}");

    /// <summary>The theme colour keys the eight accent tints take their colour from.</summary>
    private static readonly string[] TintSources =
        ["BlueColor", "CyanColor", "GreenColor", "OrangeColor", "PinkColor", "PurpleColor", "RedColor", "YellowColor"];

    /// <summary>The theme's resources as the application resolves them under an accent tint.</summary>
    private static Func<string, object?> Tinted(ResourceDictionary colours, Color tint) =>
        key => key == "AccentBrush" ? new System.Windows.Media.SolidColorBrush(tint) : colours[key];

    /// <summary>
    /// Accent outlines (BorderBrush and Stroke, attributes and setters) use the derived
    /// AccentBorderBrush, never the raw accent, which a tint can leave under 3 to 1. The one
    /// exception is an outline drawn on its own accent fill, the primary button's: it identifies
    /// nothing the fill does not, and the button hierarchy test holds it to the fill.
    /// </summary>
    [Fact]
    public void AccentOutlinesUseTheDerivedBorder()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        int outlines = 0;
        List<string> raw = [];
        foreach (string path in SourceFileEnumeration.EnumerateFiles(root, "*.xaml"))
        {
            foreach (XElement element in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants())
            {
                IEnumerable<string?> values = element.Name.LocalName == "Setter"
                    && (string?)element.Attribute("Property") is "BorderBrush" or "Stroke"
                        ? [(string?)element.Attribute("Value")]
                        : [(string?)element.Attribute("BorderBrush"), (string?)element.Attribute("Stroke")];
                foreach (string? value in values.Where(value => value is not null))
                {
                    outlines++;
                    if (value == "{DynamicResource AccentBrush}" && !IsOutlineOnItsOwnAccentFill(element))
                    {
                        raw.Add($"{Path.GetFileName(path)}:{((System.Xml.IXmlLineInfo)element).LineNumber}");
                    }
                }
            }
        }

        Assert.True(outlines >= 500, $"the walk found {outlines} outlines");
        Assert.True(raw.Count == 0, "accent outlines on the raw AccentBrush; use AccentBorderBrush: " + string.Join(", ", raw));
    }

    /// <summary>
    /// A primary button's label reads on its hover fill. ThemeForge lightens the accent for hover
    /// on every theme; on Parchment and Folio that left the label at 4.35 and 4.30 to 1, and under
    /// 4 with the green and cyan accents, where no label colour reaches 4.5. The fill is the hover
    /// shade where the label reads on it and the pressed shade where it does not.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void APrimaryButtonLabelIsReadableOnItsHoverFill(string theme)
    {
        ResourceDictionary palette = Palette(theme);
        Color label = ColorOf(palette, "TextOnAccentBrush");
        Color fill = ColorOf(palette, "AccentHoverFillBrush");

        Assert.True(Contrast(label, fill) >= MinimumTextContrast, Report(theme, "label on the hover fill", label, fill));
    }

    /// <summary>
    /// The hover fill keeps ThemeForge's hover shade wherever the label reads on it (every dark
    /// theme), and moves to the pressed shade only where it does not, the light themes.
    /// </summary>
    [Fact]
    public void TheHoverFillKeepsTheHoverShadeWhereverTheLabelReadsOnIt()
    {
        List<string> kept = [];
        List<string> moved = [];
        foreach (string theme in ThemeNames.All)
        {
            ResourceDictionary colours = ThemeColours(theme);
            Color fill = Assert.IsType<Color>(ColorOf(Palette(theme), "AccentHoverFillBrush"));
            Color hover = (Color)colours["AccentHoverColor"];
            bool labelReadsOnHover = Contrast((Color)colours[ThemeTextColours.BackgroundKey], hover) >= MinimumTextContrast;

            Assert.True(labelReadsOnHover == (fill == hover), $"{theme}: hover {hover}, fill {fill}");
            (fill == hover ? kept : moved).Add(theme);
        }

        Assert.Contains(ThemeNames.Magellan, kept);
        Assert.Equal([ThemeNames.Parchment, ThemeNames.Folio], moved.OrderByDescending(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The choice reads the shades the application resolves, an accent tint's own: the first one
    /// the label reads on, in the order hover, pressed, rest, and the most readable when none does.
    /// </summary>
    [Fact]
    public void TheHoverFillTakesTheFirstShadeTheLabelReadsOn()
    {
        Color white = Color.FromRgb(0xFF, 0xFF, 0xFF);
        Color pale = Color.FromRgb(0xC0, 0xC0, 0xC0);
        Color mid = Color.FromRgb(0x80, 0x80, 0x80);
        Color dark = Color.FromRgb(0x30, 0x30, 0x30);

        Assert.Equal(dark, ThemeTextColours.AccentHoverFill(white, Shades(dark, pale, pale)));
        Assert.Equal(dark, ThemeTextColours.AccentHoverFill(white, Shades(pale, dark, pale)));
        Assert.Equal(dark, ThemeTextColours.AccentHoverFill(white, Shades(pale, pale, dark)));
        Assert.Equal(mid, ThemeTextColours.AccentHoverFill(white, Shades(pale, mid, pale)));
        Assert.Null(ThemeTextColours.AccentHoverFill(white, _ => null));

        static Func<string, object?> Shades(Color hover, Color pressed, Color rest) => key => key switch
        {
            "AccentHoverBrush" => new System.Windows.Media.SolidColorBrush(hover),
            "AccentPressedBrush" => new System.Windows.Media.SolidColorBrush(pressed),
            "AccentBrush" => new System.Windows.Media.SolidColorBrush(rest),
            _ => null,
        };
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
        Color[] rows = ThemeTextColours.RowGrounds(background, card, key => colours[key]);
        Assert.Equal(4, rows.Length);
        ResourceDictionary derived = new();
        ThemeTextColours.Apply(derived, key => colours[key]);

        foreach ((string sourceKey, string textKey, double target, bool inRows) in ThemeTextColours.Pairs)
        {
            Color source = Assert.IsType<Color>(colours[sourceKey]);
            Color text = Assert.IsType<Color>(derived[textKey]);
            Color[] grounds = inRows ? rows : [background, card];
            bool passes = grounds.All(ground => Contrast(source, ground) >= target);

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
            foreach ((string sourceKey, string textKey, _, _) in ThemeTextColours.Pairs)
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
        Color parchmentBackground = (Color)parchment[ThemeTextColours.BackgroundKey];
        Assert.Equal(
            ThemeTextColours.Derive(
                (Color)parchment["ErrorColor"],
                ThemeTextColours.RowGrounds(parchmentBackground, (Color)parchment[ThemeTextColours.CardKey], key => parchment[key]),
                lighten: false),
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
