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
/// The Tunnels tab offers on each row what the status bar's tunnel panel already offered: a
/// right-click menu (close, copy the local port, close all) and a close button. The tab could
/// only close a tunnel from its toolbar, after selecting the row first.
/// </summary>
public sealed class TunnelsTabRowActionsTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheTabGridHasTheSameRowMenuAsThePanelGrid()
    {
        Assert.Equal(MenuCommands(PanelGrid()), MenuCommands(TabGrid()));
        Assert.Equal(
            [
                ("Tunnels.CloseCommand", "{Binding Tunnels.SelectedItem}"),
                ("Tunnels.CopyPortCommand", "{Binding Tunnels.SelectedItem}"),
                ("Tunnels.CloseAllCommand", null),
            ],
            MenuCommands(TabGrid()));
    }

    [Fact]
    public void EachTabRowHasANamedCloseButtonForThatRow()
    {
        XElement button = Assert.Single(
            TabGrid().Descendants(),
            e => e.Name.LocalName == "Button"
                && ((string?)e.Attribute("Command"))?.Contains("Tunnels.CloseCommand", StringComparison.Ordinal) == true);

        Assert.Equal("{Binding}", (string?)button.Attribute("CommandParameter"));
        Assert.Equal("{loc:Translate A11yCloseTunnel}", (string?)button.Attribute("AutomationProperties.Name"));
    }

    private static List<(string? Command, string? Parameter)> MenuCommands(XElement grid) =>
        grid.Elements()
            .Where(e => e.Name.LocalName == "DataGrid.ContextMenu")
            .Descendants()
            .Where(e => e.Name.LocalName == "MenuItem")
            .Select(item => (
                ((string?)item.Attribute("Command"))?.Replace("{Binding ", string.Empty, StringComparison.Ordinal).TrimEnd('}'),
                (string?)item.Attribute("CommandParameter")))
            .ToList();

    private static XElement TabGrid() => GridHolding("Mw_TunnelsColStarted");

    private static XElement PanelGrid() => GridHolding("Mw_TpColPort");

    private static XElement GridHolding(string columnName)
    {
        XDocument window = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement column = Assert.Single(
            window.Descendants(),
            e => (string?)e.Attribute(Xaml + "Name") == columnName);
        return column.Ancestors().First(e => e.Name.LocalName == "DataGrid");
    }
}
