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

namespace Heimdall.App.Tests;

/// <summary>
/// What the trusted RDP certificate grid says out loud when a row takes focus.
/// </summary>
/// <remarks>
/// <para>
/// A thumbprint is <c>"SHA256:"</c> followed by thirty-two colon-separated hex pairs: 102
/// characters, every one of them spoken. Bound whole into the row name, it sits between the
/// server name and the trusted-since date, so the field a user needs in order to choose which
/// approval to revoke arrives last, behind a minute of hex per row - while the grid shows the
/// sighted user twenty characters and hides the rest behind a tooltip.
/// </para>
/// <para>
/// The rule is not "leave the thumbprint out". Two certificates of the same server approved on
/// the same day are told apart by nothing else, so the elided form has to be there; it is the
/// unelided one that must not be. That is a decision about markup, and markup is where it is
/// checked: MainWindow.xaml is not copied to the output directory, so there is no live object
/// to interrogate short of building a Window, which seals application styles onto the shared
/// dispatcher and takes unrelated tests down with it.
/// </para>
/// </remarks>
public sealed class TrustedRdpCertificateRowNameGuardTests
{
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string RowNameKey = "A11yTrustedRdpCertificateRow";

    [Fact]
    public void TheRowName_AnnouncesTheElidedThumbprint_NotTheFullOne()
    {
        XElement rowName = FindRowNameMultiBinding();

        List<string> paths = rowName.Elements(Xaml + "Binding")
            .Select(b => (string?)b.Attribute("Path") ?? string.Empty)
            .ToList();

        Assert.Contains("ThumbprintDisplay", paths);
        Assert.DoesNotContain("Thumbprint", paths);
    }

    /// <summary>
    /// The control. Without it, renaming the key or the converter would empty the sweep and the
    /// assertions above would pass over a grid that had stopped naming its rows at all.
    /// </summary>
    [Fact]
    public void TheSweep_FindsTheRowNameItChecks()
    {
        XElement rowName = FindRowNameMultiBinding();

        // One format binding plus the values it fills.
        Assert.Equal(4, rowName.Elements(Xaml + "Binding").Count());
    }

    private static XElement FindRowNameMultiBinding()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");
        List<XElement> found = XDocument.Load(path)
            .Descendants(Xaml + "MultiBinding")
            .Where(m => m.ToString().Contains(
                "[" + RowNameKey + "]", StringComparison.Ordinal))
            .ToList();

        return Assert.Single(found);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
