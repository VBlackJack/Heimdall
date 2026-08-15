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
using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// The recently used tools list: ordering, deduplication and the cap.
/// </summary>
/// <remarks>
/// None of this had any coverage while it lived inside the shell view model, which is where the
/// case-sensitivity defect survived.
/// </remarks>
public sealed class RecentToolListTests
{
    [Fact]
    public void ATrackedTool_IsTheMostRecent()
    {
        RecentToolList recent = new();

        recent.Track("NETIF");

        Assert.Equal(["NETIF"], recent.Ids);
    }

    [Fact]
    public void TheNewestTool_ComesFirst()
    {
        RecentToolList recent = new();

        recent.Track("NETIF");
        recent.Track("ROUTES");

        // The command palette renders this list in order, so the order IS the feature.
        Assert.Equal(["ROUTES", "NETIF"], recent.Ids);
    }

    [Fact]
    public void ReTrackingATool_MovesItToTheFrontWithoutGrowingTheList()
    {
        RecentToolList recent = new();

        recent.Track("NETIF");
        recent.Track("ROUTES");
        recent.Track("NETIF");

        Assert.Equal(["NETIF", "ROUTES"], recent.Ids);
    }

    [Fact]
    public void TwoSpellingsOfOneToolAreOneEntry_AndTheLatestSpellingIsKept()
    {
        RecentToolList recent = new();

        recent.Track("EXT:SCOOP:winbox");
        recent.Track("EXT:SCOOP:WINBOX");

        // ToolRegistry indexes ids with StringComparer.OrdinalIgnoreCase, so these are one tool
        // everywhere else in the shell. Deduplicating ordinally instead let a single external tool
        // take two of the five slots and appear twice in the palette's recent section - which is
        // reachable in practice because external ids are built as EXT:{PROVIDER}:{tool.Id} with the
        // trailing segment left exactly as the provider spelled it, while only four of the seven
        // call sites upper-case before calling.
        Assert.Equal(["EXT:SCOOP:WINBOX"], recent.Ids);
    }

    [Fact]
    public void ADifferentlyCasedReTrack_StillMovesTheToolToTheFront()
    {
        RecentToolList recent = new();

        recent.Track("EXT:SCOOP:winbox");
        recent.Track("ROUTES");
        recent.Track("EXT:SCOOP:WINBOX");

        Assert.Equal(["EXT:SCOOP:WINBOX", "ROUTES"], recent.Ids);
    }

    [Fact]
    public void PastTheCap_TheOldestToolIsDropped()
    {
        RecentToolList recent = new();

        foreach (string id in new[] { "A", "B", "C", "D", "E", "F" })
        {
            recent.Track(id);
        }

        // Six tracked, five kept, and it is the oldest that goes - dropping the newest would make
        // the list stop reflecting use the moment it filled up.
        Assert.Equal(RecentToolList.MaxEntries, recent.Ids.Count);
        Assert.Equal(["F", "E", "D", "C", "B"], recent.Ids);
        Assert.DoesNotContain("A", recent.Ids);
    }

    [Fact]
    public void ReTrackingAtCapacity_EvictsNothing()
    {
        RecentToolList recent = new();

        foreach (string id in new[] { "A", "B", "C", "D", "E" })
        {
            recent.Track(id);
        }

        recent.Track("A");

        // A full list re-recording something it already holds must not lose an unrelated tool: the
        // deduplication happens before the insertion, so the count never exceeds the cap.
        Assert.Equal(["A", "E", "D", "C", "B"], recent.Ids);
    }

    [Fact]
    public void TheShellDelegatesInsteadOfKeepingItsOwnList()
    {
        // Wiring, checked at the source: the extracted list is only worth anything while the shell
        // actually uses it, and a reintroduced private list would keep every test above green.
        string mainViewModel = File.ReadAllText(FindShellFile("MainViewModel.cs"));

        Assert.Contains("_recentTools.Track(toolId)", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_recentToolIds", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxRecentTools", mainViewModel, StringComparison.Ordinal);
    }

    private static string FindShellFile(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                string[] matches = Directory.GetFiles(
                    Path.Combine(directory, "src", "Heimdall.App"),
                    fileName,
                    SearchOption.AllDirectories);
                return Assert.Single(matches);
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            "Cannot find repository root containing Heimdall.slnx from test binary directory: "
            + AppContext.BaseDirectory);
    }
}
