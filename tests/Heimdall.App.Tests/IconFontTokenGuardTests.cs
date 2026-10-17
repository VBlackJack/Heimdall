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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every icon glyph names the <c>FontFamilyIcons</c> token, so the icon font is chosen in one
/// place. A glyph that spelled the font itself kept MDL2 on Windows 11 while its neighbours moved
/// to Fluent, and two icons side by side were drawn by two different fonts.
/// </summary>
public sealed class IconFontTokenGuardTests
{
    private const string TokenKey = "FontFamilyIcons";

    // A font name written as a value: in an attribute or a C# string ("Segoe ...) or as the
    // text of a FontFamily element (>Segoe ...). A comment naming the font is neither.
    private static readonly Regex IconFontLiteral = new(
        @"["">]Segoe (MDL2 Assets|Fluent Icons)",
        RegexOptions.Compiled);

    [Fact]
    public void NoSourceSpellsTheIconFontOutsideItsToken()
    {
        string srcRoot = Path.Combine(ViewSource.RepoRoot(), "src");
        List<string> violations = [];

        foreach (string file in ScannedFiles(srcRoot))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IconFontLiteral.IsMatch(lines[i]) && !IsTokenDefinition(lines[i]))
                {
                    violations.Add($"  {Path.GetRelativePath(srcRoot, file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Name the {TokenKey} token instead of the font ({{StaticResource {TokenKey}}} in XAML,"
            + $" SetResourceReference(..., \"{TokenKey}\") in code):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// The scan above proves nothing if it never opens the nested folders where most glyphs
    /// live: a tool view is three levels below <c>src</c>.
    /// </summary>
    [Fact]
    public void TheScanReachesTheNestedToolViews()
    {
        string srcRoot = Path.Combine(ViewSource.RepoRoot(), "src");
        string toolViews = Path.Combine(srcRoot, "Heimdall.App", "Views", "Tools") + Path.DirectorySeparatorChar;

        Assert.Contains(
            ScannedFiles(srcRoot),
            file => file.StartsWith(toolViews, StringComparison.OrdinalIgnoreCase)
                && file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fluent first, MDL2 behind it: Windows 10 has no Fluent font, and without the fallback
    /// every glyph there would render as an empty box.
    /// </summary>
    [Fact]
    public void TheTokenPrefersFluentAndFallsBackToMdl2()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument controls = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

        XElement token = Assert.Single(
            controls.Descendants(),
            e => e.Name.LocalName == "FontFamily" && (string?)e.Attribute(x + "Key") == TokenKey);

        string[] families = token.Value.Split(',', StringSplitOptions.TrimEntries);
        Assert.Equal(["Segoe Fluent Icons", "Segoe MDL2 Assets"], families);
    }

    private static bool IsTokenDefinition(string line) =>
        line.Contains($"x:Key=\"{TokenKey}\"", StringComparison.Ordinal);

    private static IEnumerable<string> ScannedFiles(string srcRoot) =>
        SourceFileEnumeration.EnumerateFiles(srcRoot, "*.xaml")
            .Concat(SourceFileEnumeration.EnumerateFiles(srcRoot, "*.cs"));
}
