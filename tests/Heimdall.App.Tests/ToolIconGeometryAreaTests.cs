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
using System.Windows.Media;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The tool card on the Tools tab and the tools sidebar draw a tool icon with a Path that sets
/// Fill only. A geometry made of line segments alone has no area, so the icon drew nothing: the
/// SNMP Walker card and its sidebar entry showed an empty square.
/// </summary>
public sealed class ToolIconGeometryAreaTests
{
    private const string ToolKeyPrefix = "Geo.Tool.";

    /// <summary>Tool geometries found in IconGeometries.xaml on 2026-10-21: 51. A floor for the walk.</summary>
    private const int MinimumToolGeometryCount = 45;

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryToolGeometry_HasAFilledArea()
    {
        string path = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "IconGeometries.xaml");
        List<XElement> geometries = XDocument.Load(path)
            .Descendants()
            .Where(e => e.Name.LocalName == "Geometry"
                && ((string?)e.Attribute(Xaml + "Key"))?.StartsWith(ToolKeyPrefix, StringComparison.Ordinal) == true)
            .ToList();

        Assert.True(
            geometries.Count >= MinimumToolGeometryCount,
            $"the walk found {geometries.Count} tool geometries, under the floor of {MinimumToolGeometryCount}");

        List<string> empty = geometries
            .Where(e => Geometry.Parse(e.Value.Trim()).GetArea() <= 0)
            .Select(e => (string)e.Attribute(Xaml + "Key")!)
            .ToList();

        Assert.True(
            empty.Count == 0,
            "These tool geometries have no filled area and draw nothing in a Path that sets Fill only: "
            + string.Join(", ", empty));
    }
}
