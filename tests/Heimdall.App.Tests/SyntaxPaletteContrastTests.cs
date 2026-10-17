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
using System.Windows;
using Heimdall.App.Themes;
using Heimdall.App.Theming;
using ThemeForge.Theme;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Tests;

/// <summary>
/// Syntax colours readable on every shipped theme: 4.5 to 1 against the theme's background for
/// text (WCAG 1.4.3), 3 to 1 for the horizontal rule, which is a line and not text (1.4.11).
/// </summary>
/// <remarks>
/// The editor used to paint the Dracula colours on every theme, so a light theme showed yellow
/// strings and pale cyan types on near-white; and the Dracula comment colour, #6272A4, is about
/// 3 to 1 on the Dracula background itself.
/// </remarks>
public sealed class SyntaxPaletteContrastTests
{
    private const double TextMinimum = 4.5;
    private const double NonTextMinimum = 3.0;
    private const string NonTextToken = "HRule";
    private const string ThemeUriFormat = "/ThemeForge.Theme;component/Themes/{0}.xaml";

    public static TheoryData<string> Themes { get; } = new(ThemeNames.All);

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryTokenIsReadableOnTheThemeThatPicksIt(string theme)
    {
        Color background = Background(theme);
        IReadOnlyDictionary<string, string> rules = ThemeLuminance.IsDark(background)
            ? DraculaSyntaxPalette.Rules
            : DraculaSyntaxPalette.LightRules;

        List<string> unreadable = rules
            .Where(rule => Contrast(Parse(rule.Value), background) < Minimum(rule.Key))
            .Select(rule => $"{rule.Key} {rule.Value} at {Contrast(Parse(rule.Value), background):F2}")
            .ToList();

        Assert.True(unreadable.Count == 0, $"{theme} ({background}):" + Environment.NewLine + string.Join(Environment.NewLine, unreadable));
    }

    [Fact]
    public void BothPalettesNameTheSameTokens()
    {
        Assert.Equal(
            DraculaSyntaxPalette.Rules.Keys.Order(StringComparer.OrdinalIgnoreCase),
            DraculaSyntaxPalette.LightRules.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheShippedThemesIncludeBothFamilies()
    {
        // Without a light theme the light palette would be pinned against nothing.
        List<bool> dark = ThemeNames.All.Select(theme => ThemeLuminance.IsDark(Background(theme))).ToList();

        Assert.Contains(true, dark);
        Assert.Contains(false, dark);
    }

    private static double Minimum(string token) =>
        string.Equals(token, NonTextToken, StringComparison.OrdinalIgnoreCase) ? NonTextMinimum : TextMinimum;

    private static Color Background(string theme)
    {
        // LoadComponent runs the Application type initializer, which registers the pack://
        // scheme: a ResourceDictionary given the URI directly loads only after some other WPF
        // code has done so, which made this class pass in a full run and fail alone.
        var dictionary = (ResourceDictionary)Application.LoadComponent(
            new Uri(string.Format(CultureInfo.InvariantCulture, ThemeUriFormat, theme), UriKind.Relative));
        return Assert.IsType<Color>(dictionary["BackgroundColor"]);
    }

    private static Color Parse(string hex) =>
        (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));

    private static double Channel(byte value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
