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

using System.IO;
using System.Text.RegularExpressions;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every font size in the app names a typography or icon token from <c>CommonControls.xaml</c>,
/// so the type scale is decided in one place. A literal size drifts off the scale the day the
/// scale moves, and a glyph written at 10 next to one written at 12 reads as two designs.
/// </summary>
public sealed class FontSizeTokenGuardTests
{
    // An attribute that spells a number: FontSize="12", TextElement.FontSize="12.5".
    private static readonly Regex XamlAttributeLiteral = new(
        @"\bFontSize=""\s*\d",
        RegexOptions.Compiled);

    // A style setter that spells a number: <Setter Property="FontSize" Value="13"/>.
    private static readonly Regex XamlSetterLiteral = new(
        @"Property=""(\w+\.)?FontSize""\s+Value=""\s*\d",
        RegexOptions.Compiled);

    // A C# assignment of a number: FontSize = 14 in an initializer or a statement. Deliberately
    // broad (no word boundary before the name), so a field or constant named after a font size
    // is caught too and has to be declared below with its reason.
    private static readonly Regex CSharpAssignmentLiteral = new(
        @"FontSize\s*=\s*\d",
        RegexOptions.Compiled);

    /// <summary>
    /// The C# sites allowed to assign a number, by file and by the member that holds it. Neither
    /// is a size painted by a view of its own: each is a named value with a single owner.
    /// </summary>
    private static readonly IReadOnlyList<(string File, string Member, string Reason)> Allowances =
    [
        (
            Path.Combine("ViewModels", "SettingsViewModel.cs"),
            "_terminalFontSize",
            "the default of a user setting (the terminal font size), not a chrome size"),
        (
            Path.Combine("Views", "SftpViewMetrics.cs"),
            "MenuIconFontSize",
            "a named constant of the SFTP view metrics, consumed by name where the bookmark menu glyph is built"),
    ];

    [Fact]
    public void NoXamlSpellsAFontSizeOutsideTheTokens()
    {
        string appRoot = AppRoot();
        List<string> violations = [];

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (XamlAttributeLiteral.IsMatch(lines[i]) || XamlSetterLiteral.IsMatch(lines[i]))
                {
                    violations.Add($"  {Path.GetRelativePath(appRoot, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Name a FontSize* or IconSize* token from Themes/CommonControls.xaml"
            + " ({StaticResource FontSizeBody}) instead of a number:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void NoCSharpAssignsAFontSizeOutsideTheTokens()
    {
        string appRoot = AppRoot();
        List<string> violations = [];

        foreach ((string relative, int lineNumber, string line) in CSharpMatches(appRoot))
        {
            if (!Allowances.Any(allowance => IsAllowed(allowance, relative, line)))
            {
                violations.Add($"  {relative}:{lineNumber}: {line.Trim()}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Read the size from a token instead of assigning a number"
            + " ((double)Application.Current.FindResource(\"FontSizeBody\"), or"
            + " SetResourceReference(TextBlock.FontSizeProperty, \"FontSizeBody\")):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    /// <summary>Without this, an allowance outlives the site it was written for.</summary>
    [Fact]
    public void EveryAllowanceStillDescribesASite()
    {
        string appRoot = AppRoot();
        List<(string Relative, int LineNumber, string Line)> matches = CSharpMatches(appRoot).ToList();

        List<string> stale = Allowances
            .Where(allowance => !matches.Any(match => IsAllowed(allowance, match.Relative, match.Line)))
            .Select(allowance => $"  {allowance.File} {allowance.Member}")
            .ToList();

        Assert.True(
            stale.Count == 0,
            "These allowances no longer match any assignment (remove them):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale));
    }

    /// <summary>
    /// The scans above prove nothing if they never open the nested folders: a tool view sits
    /// three levels below the project, a dialog two.
    /// </summary>
    [Fact]
    public void TheScanReachesTheNestedViewsAndServices()
    {
        string appRoot = AppRoot();
        string toolViews = Path.Combine(appRoot, "Views", "Tools") + Path.DirectorySeparatorChar;
        string services = Path.Combine(appRoot, "Services") + Path.DirectorySeparatorChar;

        Assert.Contains(
            SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml"),
            file => file.StartsWith(toolViews, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            SourceFileEnumeration.EnumerateFiles(appRoot, "*.cs"),
            file => file.StartsWith(services, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The patterns catch every shape a literal size has taken in this repository, and leave the
    /// token reads alone. A pattern that stopped matching would turn both scans green.
    /// </summary>
    [Theory]
    [InlineData("FontSize=\"40\"", true)]
    [InlineData("TextElement.FontSize=\"12.5\"", true)]
    [InlineData("FontSize=\"{StaticResource FontSizeBody}\"", false)]
    public void TheXamlAttributePatternCatchesNumbersOnly(string line, bool expected) =>
        Assert.Equal(expected, XamlAttributeLiteral.IsMatch(line));

    [Theory]
    [InlineData("<Setter Property=\"FontSize\" Value=\"13\"/>", true)]
    [InlineData("<Setter Property=\"TextElement.FontSize\" Value=\"10\"/>", true)]
    [InlineData("<Setter Property=\"FontSize\" Value=\"{StaticResource FontSizeBody}\"/>", false)]
    public void TheXamlSetterPatternCatchesNumbersOnly(string line, bool expected) =>
        Assert.Equal(expected, XamlSetterLiteral.IsMatch(line));

    [Theory]
    [InlineData("    FontSize = 14,", true)]
    [InlineData("block.FontSize=12;", true)]
    [InlineData("private int _terminalFontSize = 14;", true)]
    [InlineData("FontSize = (double)Application.Current.FindResource(\"FontSizeBody\"),", false)]
    [InlineData("if (block.FontSize == 14)", false)]
    public void TheCSharpPatternCatchesNumbersOnly(string line, bool expected) =>
        Assert.Equal(expected, CSharpAssignmentLiteral.IsMatch(line));

    private static IEnumerable<(string Relative, int LineNumber, string Line)> CSharpMatches(string appRoot)
    {
        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.cs"))
        {
            string relative = Path.GetRelativePath(appRoot, file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (CSharpAssignmentLiteral.IsMatch(lines[i]))
                {
                    yield return (relative, i + 1, lines[i]);
                }
            }
        }
    }

    private static bool IsAllowed((string File, string Member, string Reason) allowance, string relative, string line) =>
        string.Equals(allowance.File, relative, StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(line, $@"\b{Regex.Escape(allowance.Member)}\s*=\s*\d");

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
