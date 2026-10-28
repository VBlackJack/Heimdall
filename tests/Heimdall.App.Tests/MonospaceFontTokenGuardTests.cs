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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Monospaced text names the shared font token, never a font.
/// </summary>
/// <remarks>
/// 253 elements named <c>Consolas</c> directly while 29 used the token, so changing the monospace
/// face (or its fallbacks on a machine without Consolas) meant editing 56 files. The token is the
/// one place that names the family.
/// </remarks>
public sealed class MonospaceFontTokenGuardTests
{
    private const string MonospaceFamilyName = "Consolas";
    private const string TokenKey = "FontFamilyMonospace";
    private const string TokenFile = "CommonControls.xaml";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NoXamlNamesTheMonospaceFontDirectly()
    {
        string appRoot = AppRoot();
        List<string> files = SourceFileEnumeration.EnumerateFiles(appRoot, "*.xaml").ToList();
        List<string> offenders = [];

        foreach (string file in files)
        {
            if (string.Equals(Path.GetFileName(file), TokenFile, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string occurrence in LiteralFamilyUses(XDocument.Load(file)))
            {
                offenders.Add($"{Path.GetRelativePath(appRoot, file)}: {occurrence}");
            }
        }

        // The scan has to reach the tool views, where nearly all the literals were.
        Assert.Contains(files, f => f.Contains(Path.Combine("Views", "Tools"), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheTokenStillLeadsWithTheMonospaceFamily()
    {
        XDocument themes = XDocument.Load(Path.Combine(AppRoot(), "Themes", TokenFile));
        XElement token = themes.Descendants()
            .Single(e => (string?)e.Attribute(Xaml + "Key") == TokenKey);

        Assert.StartsWith(MonospaceFamilyName, token.Value, StringComparison.Ordinal);
    }

    /// <summary>Positive control: the scan finds the family wherever markup writes it as a value.</summary>
    [Fact]
    public void TheScan_FindsALiteralFamily()
    {
        XDocument inline = XDocument.Parse(
            "<Grid><TextBlock FontFamily=\"Consolas\"/><TextBlock FontFamily=\"{StaticResource FontFamilyMonospace}\"/>"
            + "<Setter Property=\"FontFamily\" Value=\"Consolas, Courier New\"/></Grid>");

        Assert.Equal(2, LiteralFamilyUses(inline).Count());
    }

    private static IEnumerable<string> LiteralFamilyUses(XDocument markup)
        => markup.Descendants()
            .SelectMany(e => e.Attributes().Select(a => (Element: e, Attribute: a)))
            .Where(pair => pair.Attribute.Value.Contains(MonospaceFamilyName, StringComparison.Ordinal))
            .Select(pair => $"<{pair.Element.Name.LocalName} {pair.Attribute.Name.LocalName}=\"{pair.Attribute.Value}\">");

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
