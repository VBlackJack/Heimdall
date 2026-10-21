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
/// Pins the layout of the runbook run header: the "Running: ..." status keeps the width the
/// Stop button leaves.
/// </summary>
/// <remarks>
/// The header was a dock panel with the Stop button and the execution mode both docked right,
/// so the status got what the two of them left, about 65 px in the side panel. The fix is in the
/// markup alone, so this file reads the markup. It does not render the view.
/// </remarks>
public sealed class CommandLibraryRunHeaderMarkupTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string HeaderName = "RunbookRunHeader";

    /// <summary>
    /// One star column for the status and the mode under it, one Auto column for Stop: a third
    /// column in the row would take its width from the status again.
    /// </summary>
    [Fact]
    public void TheHeader_GivesTheStatusTheOnlyStarColumn()
    {
        List<string?> widths = Header()
            .Element(Presentation + "Grid.ColumnDefinitions")!
            .Elements(Presentation + "ColumnDefinition")
            .Select(column => (string?)column.Attribute("Width"))
            .ToList();

        Assert.Equal(["*", "Auto"], widths);
    }

    [Fact]
    public void TheStatus_SitsInTheStarColumnAndTrims()
    {
        XElement status = Assert.Single(
            Header().Elements(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding Title}");

        Assert.Equal("0", (string?)status.Attribute("Grid.Column"));
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
    }

    [Fact]
    public void TheModeLine_SitsUnderTheStatusInTheStarColumn()
    {
        XElement mode = Assert.Single(
            Header().Elements(Presentation + "TextBlock"),
            text => (string?)text.Attribute("Text") == "{Binding ExecutionModeText}");

        Assert.Equal("0", (string?)mode.Attribute("Grid.Column"));
        Assert.Equal("1", (string?)mode.Attribute("Grid.Row"));
    }

    [Fact]
    public void TheStopButton_SitsInTheAutoColumn()
    {
        XElement stop = Assert.Single(
            Header().Elements(Presentation + "Button"),
            button => (string?)button.Attribute("Command") == "{Binding StopCommand}");

        Assert.Equal("1", (string?)stop.Attribute("Grid.Column"));
    }

    /// <summary>
    /// Positive control: a renamed header would make the tests above fail for the wrong reason,
    /// and a header turned back into a dock panel must fail here under its own name.
    /// </summary>
    [Fact]
    public void TheHeader_IsAGridInTheRunPanel()
    {
        XElement header = Header();

        Assert.Equal("Grid", header.Name.LocalName);
        Assert.Equal("RunbookRunPanel", (string?)header.Parent!.Attribute(Xaml + "Name"));
    }

    private static XElement Header()
    {
        string path = Path.Combine(
            ViewSource.RepoRoot(),
            "src", "Heimdall.App", "Views", "Tools", "CommandLibraryView.xaml");

        Assert.True(File.Exists(path), $"View not found: {path}");

        List<XElement> matches = XDocument.Load(path)
            .Descendants()
            .Where(element => string.Equals(
                (string?)element.Attribute(Xaml + "Name"), HeaderName, StringComparison.Ordinal))
            .ToList();

        return Assert.Single(matches);
    }
}
