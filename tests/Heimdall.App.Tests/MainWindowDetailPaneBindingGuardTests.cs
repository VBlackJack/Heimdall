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
/// The two detail panes of the sessions view are driven by the view model and by nothing else.
/// </summary>
/// <remarks>
/// A local value written to a bound dependency property replaces the binding. The tree handlers
/// used to write <c>SessionDetailPanel.Visibility</c> and the Connect button's label that way, so
/// the first click on the tree severed the binding the markup declared and every later selection
/// change that did not pass through a tree handler - a search with no match, the collapse of the
/// folder holding the selection, a UI Automation Select - left the pane as the last click had put
/// it. Measured on 2026-09-05 on a bound <c>Border</c>: one local assignment, and the binding
/// expression was gone. The guard reads the sources because a construction of the window in a
/// unit test is what poisons the shared dispatcher for every other WPF test in the process.
/// </remarks>
public sealed class MainWindowDetailPaneBindingGuardTests
{
    /// <summary>The bound properties that no code-behind may assign.</summary>
    private static readonly string[] ForbiddenAssignments =
    [
        "SessionDetailPanel.Visibility =",
        "ToolDetailPanel.Visibility =",
        "Mw_DetailConnectBtn.Content =",
        "Mw_ToolDetailName.Text =",
        "Mw_ToolDetailCategory.Text =",
        "Mw_ToolDetailDescription.Text =",
        "Mw_ToolDetailOpenBtn.Content ="
    ];

    [Fact]
    public void NoCodeBehindWritesADetailPaneProperty()
    {
        string appRoot = Path.Combine(FindRepoRoot(), "src", "Heimdall.App");
        List<string> violations = [];
        foreach (string file in Directory.EnumerateFiles(appRoot, "MainWindow*.cs", SearchOption.TopDirectoryOnly))
        {
            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string forbidden in ForbiddenAssignments)
                {
                    if (line.Contains(forbidden, StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetFileName(file)}:{index + 1}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("SessionDetailPanel", "{Binding ServerList.ShowSessionDetail, Converter={StaticResource BoolToVisibilityConverter}}")]
    [InlineData("ToolDetailPanel", "{Binding ServerList.ShowToolDetail, Converter={StaticResource BoolToVisibilityConverter}}")]
    public void EachDetailPaneBindsItsVisibilityToTheViewModel(string elementName, string expectedVisibility)
    {
        XDocument markup = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XName name = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name";
        XElement? element = markup.Descendants()
            .FirstOrDefault(candidate => (string?)candidate.Attribute(name) == elementName);

        Assert.True(element is not null, $"{elementName} is no longer a named element in MainWindow.xaml.");
        Assert.Equal("Border", element.Name.LocalName);
        Assert.Equal(expectedVisibility, (string?)element.Attribute("Visibility"));
    }

    [Theory]
    [InlineData("Mw_ToolDetailName", "{Binding ServerList.ToolDetailName}")]
    [InlineData("Mw_ToolDetailCategory", "{Binding ServerList.ToolDetailCategory}")]
    [InlineData("Mw_ToolDetailDescription", "{Binding ServerList.ToolDetailDescription}")]
    public void EachToolPaneTextBindsToTheViewModel(string elementName, string expectedText)
    {
        XDocument markup = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XName name = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name";
        XElement? element = markup.Descendants()
            .FirstOrDefault(candidate => (string?)candidate.Attribute(name) == elementName);

        Assert.True(element is not null, $"{elementName} is no longer a named element in MainWindow.xaml.");
        Assert.Equal(expectedText, (string?)element.Attribute("Text"));
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

        throw new InvalidOperationException("Heimdall.slnx was not found above the test directory.");
    }
}
