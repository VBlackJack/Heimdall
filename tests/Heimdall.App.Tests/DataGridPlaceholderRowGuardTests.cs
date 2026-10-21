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
/// An editable grid never draws WPF's new-item placeholder row.
/// </summary>
/// <remarks>
/// A <c>DataGrid</c> that is not read-only adds an empty row under its headers when
/// <c>CanUserAddRows</c> is left at its default. The Scheduled tab showed it as a lone checkbox on
/// an otherwise empty tab. Every row in this application is created by a dialog, never inline.
/// </remarks>
public sealed class DataGridPlaceholderRowGuardTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>
    /// The application carries over twenty grids; a sweep that reads fewer walked the wrong place.
    /// </summary>
    private const int MinimumGridsSeen = 20;

    [Fact]
    public void EveryEditableGridRefusesTheNewItemRow()
    {
        int seen = 0;
        int editable = 0;
        List<string> offenders = [];

        foreach (string path in MarkupFiles())
        {
            foreach (XElement grid in XDocument.Load(path, LoadOptions.SetLineInfo).Descendants(Presentation + "DataGrid"))
            {
                seen++;
                if ((string?)grid.Attribute("IsReadOnly") == "True")
                {
                    continue;
                }

                editable++;
                if ((string?)grid.Attribute("CanUserAddRows") != "False")
                {
                    offenders.Add($"{Path.GetFileName(path)}: line {((System.Xml.IXmlLineInfo)grid).LineNumber}");
                }
            }
        }

        Assert.True(seen >= MinimumGridsSeen, $"only {seen} grids were read, so this guard measured almost nothing");
        Assert.True(editable > 0, "no editable grid was read, so the rule was never exercised");
        Assert.True(offenders.Count == 0, "editable grids that draw the new-item row: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheScheduledGridRefusesInlineDeletionToo()
    {
        XElement grid = ScheduledGrid();

        Assert.Equal("False", (string?)grid.Attribute("CanUserAddRows"));
        Assert.Equal("False", (string?)grid.Attribute("CanUserDeleteRows"));
    }

    private static XElement ScheduledGrid()
    {
        XDocument window = XDocument.Load(
            Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"),
            LoadOptions.SetLineInfo);
        return Assert.Single(
            window.Descendants(Presentation + "DataGrid"),
            e => (string?)e.Attribute("ItemsSource") == "{Binding Scheduled.Tasks}");
    }

    private static IEnumerable<string> MarkupFiles()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");

        return Directory
            .EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
