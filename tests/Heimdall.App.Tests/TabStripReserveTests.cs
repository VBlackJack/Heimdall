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
/// The session tab strip leaves room at its right end for the list button while the tabs
/// overflow, and none otherwise.
/// </summary>
public sealed class TabStripReserveTests
{
    private const double List = 32;

    /// <summary>
    /// Quick connect is clicked from the toolbar button and the empty session area; the tab strip no
    /// longer carries a duplicate of it.
    /// </summary>
    private const int QuickConnectClickSites = 2;

    [Fact]
    public void WithoutOverflowNothingIsReserved()
    {
        MainWindow.TabStripReserve reserve = MainWindow.ComputeTabStripReserve(
            extentWidth: 400, viewportWidth: 500, currentRightMargin: 0, List);

        Assert.False(reserve.Overflowing);
        Assert.Equal(0, reserve.RightMargin);
    }

    [Fact]
    public void AnOverflowReservesTheListButton()
    {
        MainWindow.TabStripReserve reserve = MainWindow.ComputeTabStripReserve(
            extentWidth: 600, viewportWidth: 500, currentRightMargin: 0, List);

        Assert.True(reserve.Overflowing);
        Assert.Equal(List + MainWindow.TabStripButtonGap, reserve.RightMargin);
    }

    /// <summary>
    /// Taking the room for the list button narrows the strip by that much. Judged on the narrowed
    /// strip alone, tabs that fit before would now overflow and the button would come and go on
    /// every layout pass.
    /// </summary>
    [Fact]
    public void TakingTheRoomDoesNotUndoTheDecision()
    {
        double reserved = List + MainWindow.TabStripButtonGap;
        double narrowed = 500 - reserved;

        MainWindow.TabStripReserve stillOver = MainWindow.ComputeTabStripReserve(
            extentWidth: 501, viewportWidth: narrowed, currentRightMargin: reserved, List);
        MainWindow.TabStripReserve backUnder = MainWindow.ComputeTabStripReserve(
            extentWidth: 490, viewportWidth: narrowed, currentRightMargin: reserved, List);

        Assert.True(stillOver.Overflowing);
        Assert.Equal(reserved, stillOver.RightMargin);
        Assert.False(backUnder.Overflowing);
        Assert.Equal(0, backUnder.RightMargin);
    }

    /// <summary>
    /// The list button alone sits at the strip's right end; the quick add button that duplicated
    /// quick connect is gone, and quick connect keeps only its toolbar and empty area buttons.
    /// </summary>
    [Fact]
    public void OnlyTheListButtonSitsAtTheStripRightEnd()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument window = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement list = Assert.Single(window.Descendants(), e => (string?)e.Attribute(x + "Name") == "Mw_SessionTabListButton");

        Assert.Equal("StackPanel", list.Parent!.Name.LocalName);
        Assert.Equal("Right", (string?)list.Parent.Attribute("HorizontalAlignment"));
        Assert.DoesNotContain(window.Descendants(), e => (string?)e.Attribute(x + "Name") == "Mw_SessionTabQuickAddButton");
        Assert.Equal(
            QuickConnectClickSites,
            window.Descendants().Count(e => (string?)e.Attribute("Click") == "OnQuickConnectButtonClick"));
    }
}
