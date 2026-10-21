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
/// The Tunnels and Scheduled tabs show their empty state as the same card the Sessions view
/// shows: a bordered container, an icon, a title and an action.
/// </summary>
/// <remarks>
/// Tunnels used to show a lone line of text in the middle and its gateway link at the very bottom
/// of the tab; Scheduled showed a line and a button with no card and no icon.
/// </remarks>
public sealed class TabEmptyStateConsistencyTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Theory]
    [InlineData("Mw_TunnelsEmpty", "Tunnels.HasNoTunnels", "Tunnels.NewTunnelCommand")]
    [InlineData("Mw_ScheduledEmpty", "Scheduled.HasNoTasks", "Scheduled.AddTaskCommand")]
    public void TheEmptyStateIsACardWithAnIconATitleAndAnAction(string titleName, string visibilityPath, string command)
    {
        XElement title = ViewSource.NamedElement(Window(), titleName);

        XElement card = Assert.Single(
            title.Ancestors(Presentation + "Border"),
            e => (string?)e.Attribute("Style") == "{StaticResource EmptyStateStyle}");

        Assert.Contains(visibilityPath, (string?)card.Attribute("Visibility") ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("1", (string?)card.Attribute("BorderThickness"));
        Assert.NotNull(card.Attribute("Background"));

        Assert.Contains(
            card.Descendants(Presentation + "TextBlock"),
            e => (string?)e.Attribute("FontFamily") == "{StaticResource FontFamilyIcons}"
                && e.Parent?.Name == Presentation + "Border");

        Assert.Contains(
            card.Descendants(Presentation + "Button"),
            e => ((string?)e.Attribute("Command"))?.Contains(command, StringComparison.Ordinal) == true
                && (string?)e.Attribute("Style") == "{DynamicResource PrimaryButtonStyle}");
    }

    [Fact]
    public void TheGatewayLinkIsTheTunnelCardHint()
    {
        XDocument window = Window();
        XElement link = ViewSource.NamedElement(window, "Mw_TunnelsManageGatewaysLink");
        XElement title = ViewSource.NamedElement(window, "Mw_TunnelsEmpty");

        Assert.Same(title.Parent, link.Parent);
        Assert.Equal("OnNavigateToGatewaySettings", (string?)link.Attribute("Click"));
        Assert.Null(link.Attribute("DockPanel.Dock"));
    }

    private static XDocument Window() =>
        XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
}
