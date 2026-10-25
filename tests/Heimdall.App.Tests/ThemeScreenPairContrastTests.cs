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
using ThemeForge.Theme;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Tests;

/// <summary>
/// Pairs drawn by particular screens, read from their markup and measured on each of the 17
/// ThemeForge themes: the brush a screen names, on the ground it sits on. Each case was under its
/// WCAG minimum on Magellan, the default theme, before the contrast audit of 2026-10-25.
/// </summary>
/// <remarks>
/// The brush is read from the XAML, so putting the former brush back fails here under the screen's
/// name; the ground is the one the markup around it paints, stated beside each case.
/// </remarks>
public sealed class ThemeScreenPairContrastTests
{
    private const double MinimumTextContrast = 4.5;
    private const double MinimumGraphicContrast = 3.0;

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The colours an accent tint can put in AccentBrush: the theme's own, or a tint source.</summary>
    private static readonly string[] AccentFillColours =
        ["AccentColor", "BlueColor", "CyanColor", "GreenColor", "OrangeColor", "PinkColor", "PurpleColor", "RedColor", "YellowColor"];

    public static TheoryData<string> Themes { get; } = new(ThemeNames.All);

    /// <summary>
    /// The " | " separators of the tool status lines. They were painted with the decorative
    /// BorderBrush, the card colour on most themes: 1.27 to 1 on Magellan.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void ToolStatusLineSeparatorsAreReadable(string theme)
    {
        List<XElement> separators = Directory
            .EnumerateFiles(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Tools"), "*.xaml")
            .SelectMany(file => Load(file).Descendants(Presentation + "TextBlock"))
            .Where(block => (string?)block.Attribute("Text") == "  |  ")
            .ToList();

        Assert.True(separators.Count >= 9, $"only {separators.Count} separators found");
        List<string> unreadable = [];
        foreach (XElement separator in separators)
        {
            string brush = Key(separator.Attribute("Foreground"));
            AssertOrCollect(unreadable, theme, "status separator", brush, ["SurfaceBrush"], MinimumTextContrast);
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The dismiss glyph of the ARP alert banner: the banner text's colour on the warning fill, and
    /// primary text on the surface the button paints when hovered or pressed. It was primary text on
    /// the warning fill, 2.31 to 1 on Magellan.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheArpAlertDismissGlyphIsReadableInEveryState(string theme)
    {
        XElement button = Named("Views/Tools/ArpMonitorView.xaml", "BtnDismissAlert");
        XElement style = Assert.Single(button.Descendants(Presentation + "Style"));
        List<string> unreadable = [];

        AssertOrCollect(unreadable, theme, "dismiss glyph at rest", SetterKey(style, "Foreground"), ["WarningBrush"], MinimumGraphicContrast);
        AssertOrCollect(unreadable, theme, "dismiss glyph hovered", TriggerSetterKey(style, "IsMouseOver", "Foreground"), ["HighlightBrush"], MinimumGraphicContrast);
        AssertOrCollect(unreadable, theme, "dismiss glyph pressed", TriggerSetterKey(style, "IsPressed", "Foreground"), ["SurfaceBrush"], MinimumGraphicContrast);

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>The external tool provider badge: it was primary text on the pink fill, 2.39 to 1.</summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheExternalToolProviderBadgeIsReadable(string theme)
    {
        XElement badge = Named("Views/Tools/ExternalToolWrapperView.xaml", "TxtProviderBadge");
        List<string> unreadable = [];

        AssertOrCollect(
            unreadable, theme, "provider badge", Key(badge.Attribute("Foreground")),
            ["SurfaceBrush", Key(badge.Attribute("Background"))], MinimumTextContrast);

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// A pressed primary button keeps its label readable with every accent. The pressed shade as a
    /// fill held the label at 3.51 to 1 on Magellan and 3.29 on Drakul with the red accent; no
    /// label colour reaches 4.5 on it in every theme, so the fill stays the accent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void APressedPrimaryButtonLabelIsReadableWithEveryAccent(string theme)
    {
        XElement style = KeyedStyle("Themes/CommonControls.xaml", "PrimaryButtonStyle");
        string label = SetterKey(style, "Foreground");
        string pressedFill = TriggerSetterKey(style, "IsPressed", "Background", "ButtonBorder");
        ResourceDictionary palette = ThemeSurfaceTokenContrastTests.Palette(theme);
        ResourceDictionary colours = ThemeSurfaceTokenContrastTests.ThemeColours(theme);
        Color text = ThemeSurfaceTokenContrastTests.ColorOf(palette, label);

        IEnumerable<Color> fills = pressedFill == "AccentBrush"
            ? AccentFillColours.Select(key => (Color)colours[key])
            : [ThemeSurfaceTokenContrastTests.ColorOf(palette, pressedFill)];
        List<string> unreadable = fills
            .Where(fill => ThemeSurfaceTokenContrastTests.Contrast(text, fill) < MinimumTextContrast)
            .Select(fill => Report(theme, $"{label} on pressed {pressedFill}", text, fill))
            .ToList();

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// A hovered primary button keeps its label readable. On ThemeForge's hover shade the label was
    /// 4.35 to 1 on Parchment and 4.30 on Folio; the style paints the derived hover fill instead.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void AHoveredPrimaryButtonLabelIsReadable(string theme)
    {
        XElement style = KeyedStyle("Themes/CommonControls.xaml", "PrimaryButtonStyle");
        string label = SetterKey(style, "Foreground");
        string hoverFill = TriggerSetterKey(style, "IsMouseOver", "Background", "ButtonBorder");
        ResourceDictionary palette = ThemeSurfaceTokenContrastTests.Palette(theme);
        Color text = ThemeSurfaceTokenContrastTests.ColorOf(palette, label);
        Color fill = ThemeSurfaceTokenContrastTests.ColorOf(palette, hoverFill);

        Assert.True(
            ThemeSurfaceTokenContrastTests.Contrast(text, fill) >= MinimumTextContrast,
            Report(theme, $"{label} on hovered {hoverFill}", text, fill));
    }

    /// <summary>
    /// The onboarding step dots: an inactive dot is an indicator, not a disabled control, and must
    /// stand at 3 to 1 on the onboarding card. In the disabled colour it was 2.4 on Parchment.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void InactiveOnboardingDotsAreVisible(string theme)
    {
        XElement dots = Assert.Single(
            Load(Source("MainWindow.xaml")).Descendants(Presentation + "ItemsControl"),
            items => ((string?)items.Attribute("ItemsSource"))?.Contains("StepIndicatorStates", StringComparison.Ordinal) == true);
        XElement style = Assert.Single(dots.Descendants(Presentation + "Style"));
        List<string> unreadable = [];

        AssertOrCollect(unreadable, theme, "inactive step dot", SetterKey(style, "Fill"), ["SurfaceBrush"], MinimumGraphicContrast);

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The broadcast pill while broadcasting: its label on the red tint over the status bar, and
    /// the red outline on the status bar. The label was the derived error text, 3.17 to 1 on
    /// Magellan.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheActiveBroadcastPillIsReadable(string theme)
    {
        XElement button = Named("MainWindow.xaml", "Mw_BroadcastToggleBtn");
        XElement trigger = Assert.Single(button.Descendants(Presentation + "DataTrigger"));
        string fill = SetterKey(trigger, "Background");
        List<string> unreadable = [];

        AssertOrCollect(unreadable, theme, "broadcast label", SetterKey(trigger, "Foreground"), ["SurfaceBrush", fill], MinimumTextContrast);
        AssertOrCollect(unreadable, theme, "broadcast outline", SetterKey(trigger, "BorderBrush"), ["SurfaceBrush"], MinimumGraphicContrast);

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The skipped-mappings chip of the RDP import dialog, on the dialog's section card: its label
    /// and its outline. Its label was the background colour on a disabled-grey fill, 3.61 to 1.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheRdpImportSkippedChipIsReadable(string theme)
    {
        XElement chip = Assert.Single(
            Load(Source("Views/Dialogs/RdpImportDialog.xaml")).Descendants(Presentation + "Border"),
            border => ((string?)border.Attribute("Visibility"))?.Contains("HasSkippedMappings", StringComparison.Ordinal) == true);
        XElement label = Assert.Single(chip.Elements(Presentation + "TextBlock"));
        List<string> ground = ["CardBrush"];
        if (chip.Attribute("Background") is XAttribute fill)
        {
            ground.Add(Key(fill));
        }

        List<string> unreadable = [];
        AssertOrCollect(unreadable, theme, "skipped chip label", Key(label.Attribute("Foreground")), ground, MinimumTextContrast);
        if (chip.Attribute("BorderBrush") is XAttribute outline)
        {
            AssertOrCollect(unreadable, theme, "skipped chip outline", Key(outline), ["CardBrush"], MinimumGraphicContrast);
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// Text in the Notes tool is not dimmed with Opacity on top of its tier brush. At 0.62 the list
    /// footer's secondary text was 3.22 to 1 on Magellan; the tier brushes carry the emphasis.
    /// </summary>
    [Fact]
    public void NotesToolTextIsNotDimmedByOpacity()
    {
        XDocument view = Load(Source("Views/Tools/NotesToolView.xaml"));
        List<string> dimmed = [];
        foreach (XElement block in view.Descendants(Presentation + "TextBlock"))
        {
            if (IsDimmed(Opacity(block.Attribute("Opacity")?.Value)))
            {
                dimmed.Add(Describe(block));
            }
        }

        foreach (XElement style in view.Descendants(Presentation + "Style")
                     .Where(style => ((string?)style.Attribute("TargetType"))?.EndsWith("TextBlock", StringComparison.Ordinal) == true))
        {
            foreach (XElement setter in style.Elements(Presentation + "Setter"))
            {
                if ((string?)setter.Attribute("Property") == "Opacity" && IsDimmed(Opacity(setter.Attribute("Value")?.Value)))
                {
                    dimmed.Add(Describe(style));
                }
            }
        }

        Assert.True(dimmed.Count == 0, "dimmed text: " + string.Join(", ", dimmed));
    }

    /// <summary>
    /// The one exemption above: Dracula's secondary text is a palette gap, 3.03 to 1 on its window
    /// and 1.94 on a card, gated nowhere (see <c>TertiaryTextIsNoFainterThanSecondaryText</c>).
    /// Once it reads at 4.5, this fails, and the exemption goes.
    /// </summary>
    [Fact]
    public void DraculasSecondaryTextIsStillThePaletteGapTheExemptionNames()
    {
        ResourceDictionary palette = ThemeSurfaceTokenContrastTests.Palette(ThemeNames.Dracula);
        Color card = ThemeSurfaceTokenContrastTests.ColorOf(palette, "CardBrush");
        Color secondary = ThemeSurfaceTokenContrastTests.ColorOf(palette, "TextSecondaryBrush");

        Assert.True(ThemeSurfaceTokenContrastTests.Contrast(secondary, card) < MinimumTextContrast);
    }

    private static void AssertOrCollect(
        List<string> unreadable, string theme, string what, string brush, IReadOnlyList<string> ground, double minimum)
    {
        if (IsDraculaSecondaryTier(theme, brush) && minimum > MinimumGraphicContrast)
        {
            return;
        }

        ResourceDictionary palette = ThemeSurfaceTokenContrastTests.Palette(theme);
        Color under = ThemeSurfaceTokenContrastTests.ColorOf(palette, "BackgroundBrush");
        foreach (string layer in ground)
        {
            under = ThemeSurfaceTokenContrastTests.Over(ThemeSurfaceTokenContrastTests.ColorOf(palette, layer), under);
        }

        Color top = ThemeSurfaceTokenContrastTests.Over(ThemeSurfaceTokenContrastTests.ColorOf(palette, brush), under);
        if (ThemeSurfaceTokenContrastTests.Contrast(top, under) < minimum)
        {
            unreadable.Add(Report(theme, $"{what}: {brush} on {string.Join(" > ", ground)}", top, under));
        }
    }

    private static bool IsDraculaSecondaryTier(string theme, string brush) =>
        theme == ThemeNames.Dracula && brush is "TextSecondaryBrush" or "TextTertiaryBrush";

    private static string Report(string theme, string what, Color foreground, Color background) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1}, {2} on {3} is {4:F2} to 1",
            theme,
            what,
            foreground,
            background,
            ThemeSurfaceTokenContrastTests.Contrast(foreground, background));

    private static string Source(string relative) =>
        Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relative.Replace('/', Path.DirectorySeparatorChar));

    private static XDocument Load(string path) => XDocument.Load(path, LoadOptions.SetLineInfo);

    private static XElement Named(string relative, string name) =>
        Assert.Single(Load(Source(relative)).Descendants(), element => (string?)element.Attribute(Xaml + "Name") == name);

    private static XElement KeyedStyle(string relative, string key) =>
        Assert.Single(Load(Source(relative)).Descendants(Presentation + "Style"), style => (string?)style.Attribute(Xaml + "Key") == key);

    /// <summary>The resource key of a <c>{DynamicResource Key}</c> or <c>{StaticResource Key}</c> value.</summary>
    private static string Key(XAttribute? attribute)
    {
        Assert.NotNull(attribute);
        string value = attribute.Value.Trim();
        string[] parts = value.Trim('{', '}').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length == 2 && parts[0] is "DynamicResource" or "StaticResource", $"not a resource reference: {value}");
        return parts[1];
    }

    /// <summary>The value of a direct setter of the style or trigger, as a resource key.</summary>
    private static string SetterKey(XElement owner, string property) =>
        Key(Assert.Single(
            owner.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == property && setter.Attribute("TargetName") is null)
            .Attribute("Value"));

    /// <summary>The value a property trigger of the style (or of its template) sets.</summary>
    private static string TriggerSetterKey(XElement style, string trigger, string property, string? targetName = null)
    {
        XElement match = Assert.Single(
            style.Descendants(Presentation + "Trigger"),
            candidate => (string?)candidate.Attribute("Property") == trigger && (string?)candidate.Attribute("Value") == "True");
        return Key(Assert.Single(
            match.Elements(Presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == property && (string?)setter.Attribute("TargetName") == targetName)
            .Attribute("Value"));
    }

    /// <summary>Partly transparent: an element at 0 is hidden on purpose, not dimmed.</summary>
    private static bool IsDimmed(double opacity) => opacity is > 0.0 and < 1.0;

    private static double Opacity(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity) ? opacity : 1.0;

    private static string Describe(XElement element) =>
        $"{element.Name.LocalName} {(string?)element.Attribute(Xaml + "Name") ?? (string?)element.Attribute(Xaml + "Key")} at line {((System.Xml.IXmlLineInfo)element).LineNumber}";
}
