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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The two rows of the placement bar read the same scale, so they are held to being the same bar.
/// </summary>
/// <remarks>
/// <para>The digits row and the specials row each show a position from 0 to 100 percent of the
/// same password. A cursor at 50 percent on one has to sit directly above a cursor at 50 percent
/// on the other, which means the two tracks must start at the same place and be the same
/// length.</para>
/// <para>They did not. The label widening of PR #422 matched a width belonging to a
/// <c>ColumnDefinition</c> rather than to the label it was aiming at, and a column definition
/// carrying only a <c>MinWidth</c> is a star column: the specials label took half the row and its
/// track began in the middle. Nothing failed, because nothing compared the two rows to each
/// other.</para>
/// <para>Two separate grids cannot agree on an Auto column by themselves. A shared size scope is
/// what makes them agree, in any language and without anyone choosing a number of pixels, and it
/// fails silently when the scope is missing: the groups are simply ignored and each grid sizes
/// itself again. Both halves are asserted here for that reason.</para>
/// </remarks>
public sealed class PlacementBarGeometryGuardTests
{
    private const string SharedGroupName = "PlacementLabel";
    private static readonly string[] PlacementRows = ["PlacementDigitsRow", "PlacementSpecialsRow"];

    [Fact]
    public void BothPlacementRowsSizeTheirLabelColumnTogether()
    {
        XDocument view = LoadView();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        List<XElement> rows = PlacementRows
            .Select(name => FindByName(view, name))
            .ToList();

        // A row that vanished would otherwise make every assertion below pass on an empty list.
        Assert.Equal(PlacementRows.Length, rows.Count);

        foreach ((string name, XElement row) in PlacementRows.Zip(rows))
        {
            List<XElement> columns = row
                .Elements()
                .Where(child => child.Name.LocalName == "Grid.ColumnDefinitions")
                .SelectMany(definitions => definitions.Elements())
                .ToList();

            Assert.True(columns.Count >= 2, $"{name} declares {columns.Count} column(s)");

            XElement label = columns[0];
            Assert.Equal(
                SharedGroupName,
                (string?)label.Attribute("SharedSizeGroup"));

            // Auto is what the shared group measures; a star column would take half the row and
            // a fixed one would need a number picked per language.
            Assert.Equal("Auto", (string?)label.Attribute("Width"));
            Assert.Null(label.Attribute("MinWidth"));
        }
    }

    /// <summary>
    /// A shared size group with no scope above it is ignored without a word, which looks exactly
    /// like the defect it is meant to prevent.
    /// </summary>
    [Fact]
    public void TheSharedSizeGroupHasAScopeToBeSharedIn()
    {
        XDocument view = LoadView();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement panel = FindByName(view, "PanelPlacementBar");

        // An attached property is serialised under its owner's name, so the attribute is
        // Grid.IsSharedSizeScope and not IsSharedSizeScope.
        Assert.Equal("True", (string?)panel.Attribute("Grid.IsSharedSizeScope"));

        List<XElement> rows = PlacementRows.Select(name => FindByName(view, name)).ToList();
        Assert.All(rows, row => Assert.True(
            row.Ancestors().Contains(panel),
            "a placement row sits outside the scope that sizes it"));
    }

    private static XElement FindByName(XDocument view, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement? found = view.Descendants()
            .FirstOrDefault(element => (string?)element.Attribute(x + "Name") == name);

        Assert.True(found is not null, $"the view no longer declares {name}");
        return found!;
    }

    private static XDocument LoadView()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(),
            "src", "Heimdall.App", "Views", "Tools", "PasswordGeneratorView.xaml");

        Assert.True(File.Exists(path), $"View not found: {path}");
        return XDocument.Load(path);
    }
}
