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
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.Theming;
using Heimdall.App.Views;
using Color = System.Windows.Media.Color;

namespace Heimdall.App.Tests;

/// <summary>
/// The terminal page's search bar takes its colours from the active terminal scheme and reads on
/// them, and the terminal raises every ANSI colour to 4.5 to 1. The bar was fixed Dracula greys:
/// its no-match status #FF8080 on #44475A was 3.77 to 1 and its hovered button text 4.41.
/// </summary>
public sealed class TerminalSearchChromeTests
{
    public static TheoryData<string> Schemes { get; } = new(EmbeddedSshView.ColorSchemes.Keys);

    [Theory]
    [MemberData(nameof(Schemes))]
    public void TheSearchBarIsReadableInEveryScheme(string scheme)
    {
        TerminalSearchChrome.Palette bar = Derive(scheme);
        List<string> unreadable = [];

        Check(unreadable, scheme, "text on the bar", bar.Text, bar.Bar, ThemeTextColours.MinimumContrast);
        Check(unreadable, scheme, "text in the input", bar.Text, bar.Input, ThemeTextColours.MinimumContrast);
        Check(unreadable, scheme, "text on a hovered button", bar.Text, bar.Hover, ThemeTextColours.MinimumContrast);
        Check(unreadable, scheme, "no-match status on the bar", bar.NoMatchText, bar.Bar, ThemeTextColours.MinimumContrast);
        foreach ((string name, Color outline) in new[] { ("border", bar.Border), ("focus outline", bar.Focus), ("no-match border", bar.NoMatchBorder) })
        {
            Check(unreadable, scheme, name + " on the bar", outline, bar.Bar, ThemeTextColours.MinimumBoundaryContrast);
            Check(unreadable, scheme, name + " on the input", outline, bar.Input, ThemeTextColours.MinimumBoundaryContrast);
        }

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>
    /// The bar follows the scheme: its input is the scheme's background, and a scheme that sets no
    /// colours (Default) gets xterm.js's own black ground and white text, as the terminal does.
    /// </summary>
    [Fact]
    public void TheSearchBarFollowsTheScheme()
    {
        TerminalSearchChrome.Palette solarized = Derive("Solarized Dark");
        TerminalSearchChrome.Palette nord = Derive("Nord");
        TerminalSearchChrome.Palette none = Derive("Default");

        Assert.Equal(Color.FromRgb(0x00, 0x2B, 0x36), solarized.Input);
        Assert.Equal(Color.FromRgb(0x2E, 0x34, 0x40), nord.Input);
        Assert.NotEqual(solarized.Bar, nord.Bar);
        Assert.Equal(Color.FromRgb(0x00, 0x00, 0x00), none.Input);
        Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), none.Text);
    }

    /// <summary>
    /// A scheme colour that already reads is kept as it is, and one that does not is moved: Dracula's
    /// foreground stays #F8F8F2, Solarized Dark's #839496 (4.75 to 1 on its own background) is
    /// lightened to read on the bar.
    /// </summary>
    [Fact]
    public void ASchemeColourMovesOnlyWhereItFails()
    {
        Assert.Equal(Color.FromRgb(0xF8, 0xF8, 0xF2), Derive("Dracula").Text);
        Assert.NotEqual(Color.FromRgb(0x83, 0x94, 0x96), Derive("Solarized Dark").Text);
    }

    /// <summary>
    /// The page paints the bar from the derived variables only, gets every one of them filled, and
    /// asks xterm.js for the minimum contrast.
    /// </summary>
    [Fact]
    public void ThePageTakesTheBarsColoursAndTheMinimumContrastFromTheHost()
    {
        string page = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "terminal.html"));
        Match searchRules = Regex.Match(page, @"/\* Search bar overlay \*/(.*?)\.xterm,", RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(searchRules.Success, "the search bar rules were not found");
        Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b|rgba?\(", searchRules.Groups[1].Value);
        foreach ((string placeholder, _) in TerminalSearchChrome.CssVariables("{}", EmbeddedSshView.XtermDefaultColours))
        {
            Assert.Contains(placeholder, page, StringComparison.Ordinal);
        }

        Assert.Matches(@"minimumContrastRatio:\s*/\*\{\{TERMINAL_MIN_CONTRAST\}\}\*/", page);
    }

    /// <summary>
    /// The loading text and the scrollbar take the scheme's colours too. They were fixed Dracula
    /// colours: near white loading text and Dracula greys on every scheme, Parchment included.
    /// </summary>
    [Fact]
    public void TheLoadingTextAndTheScrollbarTakeTheSchemesColours()
    {
        string page = File.ReadAllText(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Assets", "terminal.html"));
        Match loading = Regex.Match(page, @"#loading\s*\{(?<rules>[^}]*)\}", RegexOptions.CultureInvariant);
        MatchCollection scrollbar = Regex.Matches(page, @"::-webkit-scrollbar[\w:-]*\s*\{(?<rules>[^}]*)\}", RegexOptions.CultureInvariant);

        Assert.True(loading.Success, "the loading rule was not found");
        Assert.True(scrollbar.Count >= 4, $"expected the scrollbar, track, thumb and hovered thumb rules, found {scrollbar.Count}");
        Assert.Contains("var(--loading-text)", loading.Groups["rules"].Value, StringComparison.Ordinal);
        foreach (string rules in new[] { loading.Groups["rules"].Value }.Concat(scrollbar.Select(rule => rule.Groups["rules"].Value)))
        {
            Assert.DoesNotMatch(@"#[0-9A-Fa-f]{3,8}\b|rgba?\(", rules);
        }

        foreach (string variable in new[] { "--scrollbar-track", "--scrollbar-thumb", "--scrollbar-thumb-hover" })
        {
            Assert.Contains($"var({variable})", page, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The scrollbar thumb stands out from its track at 3 to 1 (WCAG 1.4.11), and the loading text
    /// and the hovered thumb read at 4.5 to 1 on the scheme's background, in every scheme.
    /// </summary>
    [Theory]
    [MemberData(nameof(Schemes))]
    public void TheScrollbarAndTheLoadingTextReadInEveryScheme(string scheme)
    {
        Dictionary<string, Color> page = TerminalSearchChrome.CssVariables(EmbeddedSshView.ColorSchemes[scheme], EmbeddedSshView.XtermDefaultColours)
            .ToDictionary(variable => variable.Placeholder, variable => ParseHex(variable.Colour), StringComparer.Ordinal);
        Color track = page["/*{{CSS_SCROLLBAR_TRACK}}*/"];
        List<string> unreadable = [];

        Assert.Equal(Derive(scheme).Input, track);
        Check(unreadable, scheme, "scrollbar thumb on its track", page["/*{{CSS_SCROLLBAR_THUMB}}*/"], track, ThemeTextColours.MinimumBoundaryContrast);
        Check(unreadable, scheme, "hovered scrollbar thumb on its track", page["/*{{CSS_SCROLLBAR_THUMB_HOVER}}*/"], track, ThemeTextColours.MinimumContrast);
        Check(unreadable, scheme, "loading text on the background", page["/*{{CSS_LOADING_TEXT}}*/"], track, ThemeTextColours.MinimumContrast);

        Assert.True(unreadable.Count == 0, string.Join(Environment.NewLine, unreadable));
    }

    /// <summary>The colours reach the page as #RRGGBB, the only shape the placeholders may take.</summary>
    [Fact]
    public void TheVariablesAreSixDigitHexColours()
    {
        foreach (string scheme in EmbeddedSshView.ColorSchemes.Keys)
        {
            foreach ((_, string colour) in TerminalSearchChrome.CssVariables(EmbeddedSshView.ColorSchemes[scheme], EmbeddedSshView.XtermDefaultColours))
            {
                Assert.Matches("^#[0-9A-F]{6}$", colour);
            }
        }
    }

    private static Color ParseHex(string hex) => new()
    {
        A = byte.MaxValue,
        R = byte.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        G = byte.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        B = byte.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    };

    private static TerminalSearchChrome.Palette Derive(string scheme) =>
        TerminalSearchChrome.Derive(EmbeddedSshView.ColorSchemes[scheme], EmbeddedSshView.XtermDefaultColours);

    private static void Check(List<string> unreadable, string scheme, string what, Color foreground, Color ground, double minimum)
    {
        double ratio = ThemeTextColours.ContrastRatio(foreground, ground);
        if (ratio < minimum)
        {
            unreadable.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1} {2} on {3} is {4:F2} to 1", scheme, what, foreground, ground, ratio));
        }
    }
}
