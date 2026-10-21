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
/// At launch, focus left on the session tree's container moves to a row or to the filter box.
/// </summary>
/// <remarks>
/// The container took focus before its rows existed, and the focus frame then outlined the whole
/// sidebar until a row was clicked. The frame itself stays: a keyboard user tabbing into an empty
/// tree needs it.
/// </remarks>
public sealed class StartupTreeFocusTests
{
    private const string RestoreMember = "private void RestoreStartupSelection(MainViewModel viewModel)";

    private const string RestoreStatement = "RestoreTreeSelectionRow(viewModel.ServerList.SelectedServer);";

    private const string HandoffStatement = "HandStartupFocusFromTreeContainer(viewModel.ServerList.SelectedServer);";

    [Theory]
    [InlineData(true, true, true, "SelectedRow")]
    [InlineData(true, true, false, "SelectedRow")]
    [InlineData(true, false, true, "FilterBox")]
    [InlineData(true, false, false, "Keep")]
    [InlineData(false, true, true, "Keep")]
    [InlineData(false, false, true, "Keep")]
    public void FocusMovesOnlyOffTheTreeContainer(
        bool onContainer,
        bool selectedRow,
        bool filterBox,
        string expected)
    {
        Assert.Equal(
            Enum.Parse<MainWindow.StartupFocusHandoff>(expected),
            MainWindow.ResolveStartupFocusHandoff(onContainer, selectedRow, filterBox));
    }

    [Fact]
    public void TheStartupHandsFocusOnAfterRestoringTheSelection()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(
                Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.TreeInteractions.cs"))),
            RestoreMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, RestoreStatement),
            "restoring the tree selection row is not a step of the startup restore");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, HandoffStatement),
            "handing focus off the tree container is not a step of the startup restore");
        Assert.True(
            logic.IndexOf(RestoreStatement, StringComparison.Ordinal)
                < logic.IndexOf(HandoffStatement, StringComparison.Ordinal),
            "the startup hands focus on before the selection row is restored");
    }

    [Fact]
    public void TheTreeKeepsItsFocusFrame()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument window = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement tree = Assert.Single(window.Descendants(), e => (string?)e.Attribute(xaml + "Name") == "SessionTreeView");

        Assert.Null(tree.Attribute("FocusVisualStyle"));
    }
}
