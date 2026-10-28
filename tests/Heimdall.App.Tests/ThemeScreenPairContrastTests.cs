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
    /// A pressed primary button shows its pressed outline. Pressing focuses the button, and in a
    /// template the later trigger wins: with IsFocused after IsPressed, the focus border covered the
    /// outline on every press (seen on screen on 2026-10-26).
    /// </summary>
    [Fact]
    public void APressedPrimaryButtonsOutlineWinsOverItsFocusBorder()
    {
        XElement style = KeyedStyle("Themes/CommonControls.xaml", "PrimaryButtonStyle");
        List<XElement> triggers = [.. style.Descendants(Presentation + "Trigger")];
        int OrderOf(string property) => triggers.FindIndex(trigger =>
            (string?)trigger.Attribute("Property") == property
            && (string?)trigger.Attribute("Value") == "True"
            && trigger.Elements(Presentation + "Setter").Any(setter => (string?)setter.Attribute("Property") == "BorderBrush"));

        int focused = OrderOf("IsFocused");
        int pressed = OrderOf("IsPressed");

        Assert.True(focused >= 0 && pressed >= 0, "the focus or press trigger no longer sets the border");
        Assert.True(pressed > focused, $"IsPressed (trigger {pressed}) must come after IsFocused (trigger {focused})");
        Assert.Equal("AccentPressedBrush", TriggerSetterKey(style, "IsPressed", "BorderBrush", "ButtonBorder"));
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

        // A 1 px outline at a fractional position is drawn as two blended rows, 2.2 to 1 on screen
        // where the colour itself reaches 3: the outline must land on whole device pixels.
        Assert.Equal("True", (string?)chip.Attribute("SnapsToDevicePixels"));

        List<string> unreadable = [];
        AssertOrCollect(unreadable, theme, "skipped chip label", Key(label.Attribute("Foreground")), ground, MinimumTextContrast);
        if (chip.Attribute("BorderBrush") is XAttribute outline)
        {
            AssertOrCollect(unreadable, theme, "skipped chip outline", Key(outline), ["CardBrush"], MinimumGraphicContrast);
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// Every chip of the RDP import dialog: its label on the fill it is drawn on (the section card
    /// for an outlined chip), and no text brush used as a fill. The chips drew the background colour
    /// on the raw error, warning and secondary-text fills, measured per theme in this test.
    /// </summary>
    [Theory]
    [MemberData(nameof(Themes))]
    public void TheRdpImportChipLabelsAreReadableOnTheirFills(string theme)
    {
        List<XElement> chips = [.. ChipsOf("Views/Dialogs/RdpImportDialog.xaml")];
        Assert.True(chips.Count >= 6, $"only {chips.Count} chips found");

        List<string> unreadable = [];
        foreach (XElement chip in chips)
        {
            XElement label = Assert.Single(chip.Elements(Presentation + "TextBlock"));
            List<string> ground = ["CardBrush"];
            if (chip.Attribute("Background") is XAttribute fill)
            {
                ground.Add(Key(fill));
            }

            AssertOrCollect(unreadable, theme, $"chip {(string?)label.Attribute("Text")}", Key(label.Attribute("Foreground")), ground, MinimumTextContrast);
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// A text brush (TextSecondaryBrush, ErrorTextBrush and the others) is for text: used as a fill it
    /// is the colour derived to read on a card, so a label on it has nothing to be paired with. The
    /// unknown-keys chip was filled with TextSecondaryBrush. The chips of the RDP import dialog keep
    /// to the fill tokens.
    /// </summary>
    [Fact]
    public void NoRdpImportChipIsFilledWithATextBrush()
    {
        List<string> misused = [];
        foreach (XElement chip in ChipsOf("Views/Dialogs/RdpImportDialog.xaml"))
        {
            if (chip.Attribute("Background") is XAttribute fill && Key(fill).Contains("Text", StringComparison.Ordinal))
            {
                misused.Add(Describe(chip));
            }
        }

        Assert.True(misused.Count == 0, "chips filled with a text brush: " + string.Join(", ", misused));
    }

    /// <summary>The chips of a view: a rounded border that holds one label.</summary>
    private static IEnumerable<XElement> ChipsOf(string relative) =>
        Load(Source(relative)).Descendants(Presentation + "Border")
            .Where(border => (string?)border.Attribute("CornerRadius") == "10"
                && border.Elements(Presentation + "TextBlock").Count() == 1);

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
    /// Secondary and tertiary text anywhere is not dimmed with Opacity on top of its brush: the
    /// derived colour is sized to 4.5 to 1 at full opacity. At 0.85 the command palette's snippet
    /// headers and the password generator's crack-time line were 4.48 to 1 on Parchment.
    /// </summary>
    [Fact]
    public void SecondaryAndTertiaryTextIsNotDimmedByOpacity()
    {
        int tierTexts = 0;
        List<string> dimmed = [];
        foreach (string path in SourceFileEnumeration.EnumerateFiles(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App"), "*.xaml"))
        {
            foreach (XElement block in Load(path).Descendants(Presentation + "TextBlock"))
            {
                if ((string?)block.Attribute("Foreground") is "{DynamicResource TextSecondaryBrush}" or "{DynamicResource TextTertiaryBrush}")
                {
                    tierTexts++;
                    if (IsDimmed(Opacity(block.Attribute("Opacity")?.Value)))
                    {
                        dimmed.Add(Path.GetFileName(path) + ":" + Describe(block));
                    }
                }
            }
        }

        Assert.True(dimmed.Count == 0, "dimmed text: " + string.Join(", ", dimmed));
        Assert.True(tierTexts >= MinimumTierTexts, $"the scan found only {tierTexts} secondary and tertiary texts");
    }

    /// <summary>How many secondary and tertiary TextBlocks the views draw; the scan must reach them.</summary>
    private const int MinimumTierTexts = 200;

    private static void AssertOrCollect(
        List<string> unreadable, string theme, string what, string brush, IReadOnlyList<string> ground, double minimum)
    {
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
