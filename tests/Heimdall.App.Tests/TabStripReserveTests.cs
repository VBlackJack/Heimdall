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
/// The session tab strip leaves room at its right end for the quick connect button, always, and
/// for the list button while the tabs overflow.
/// </summary>
public sealed class TabStripReserveTests
{
    private const double QuickAdd = 32;
    private const double List = 32;

    [Fact]
    public void WithoutOverflowOnlyTheQuickConnectButtonIsReserved()
    {
        MainWindow.TabStripReserve reserve = MainWindow.ComputeTabStripReserve(
            extentWidth: 400, viewportWidth: 500, currentRightMargin: QuickAdd, QuickAdd, List);

        Assert.False(reserve.Overflowing);
        Assert.Equal(QuickAdd, reserve.RightMargin);
    }

    [Fact]
    public void AnOverflowReservesTheListButtonBesideIt()
    {
        MainWindow.TabStripReserve reserve = MainWindow.ComputeTabStripReserve(
            extentWidth: 600, viewportWidth: 500, currentRightMargin: QuickAdd, QuickAdd, List);

        Assert.True(reserve.Overflowing);
        Assert.Equal(QuickAdd + List + MainWindow.TabStripButtonGap, reserve.RightMargin);
    }

    /// <summary>
    /// Taking the room for the list button narrows the strip by that much. Judged on the narrowed
    /// strip alone, tabs that fit before would now overflow and the button would come and go on
    /// every layout pass.
    /// </summary>
    [Fact]
    public void TakingTheRoomDoesNotUndoTheDecision()
    {
        double reserved = QuickAdd + List + MainWindow.TabStripButtonGap;
        double narrowed = 500 - (List + MainWindow.TabStripButtonGap);

        MainWindow.TabStripReserve stillOver = MainWindow.ComputeTabStripReserve(
            extentWidth: 501, viewportWidth: narrowed, currentRightMargin: reserved, QuickAdd, List);
        MainWindow.TabStripReserve backUnder = MainWindow.ComputeTabStripReserve(
            extentWidth: 490, viewportWidth: narrowed, currentRightMargin: reserved, QuickAdd, List);

        Assert.True(stillOver.Overflowing);
        Assert.False(backUnder.Overflowing);
        Assert.Equal(QuickAdd, backUnder.RightMargin);
    }

    [Fact]
    public void AQuickConnectButtonNotYetMeasuredReservesNothing()
    {
        MainWindow.TabStripReserve reserve = MainWindow.ComputeTabStripReserve(
            extentWidth: 400, viewportWidth: 500, currentRightMargin: 0, double.NaN, List);

        Assert.Equal(0, reserve.RightMargin);
    }

    /// <summary>The two buttons sit side by side at the strip's right end, list first.</summary>
    [Fact]
    public void TheListAndQuickConnectButtonsShareOneRightAlignedRow()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument window = XDocument.Load(Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XElement list = Assert.Single(window.Descendants(), e => (string?)e.Attribute(x + "Name") == "Mw_SessionTabListButton");
        XElement quick = Assert.Single(window.Descendants(), e => (string?)e.Attribute(x + "Name") == "Mw_SessionTabQuickAddButton");

        Assert.Same(list.Parent, quick.Parent);
        Assert.Equal("StackPanel", list.Parent!.Name.LocalName);
        Assert.Equal("Right", (string?)list.Parent.Attribute("HorizontalAlignment"));
        Assert.True(list.IsBefore(quick));
        Assert.Equal("OnQuickConnectButtonClick", (string?)quick.Attribute("Click"));
    }
}
