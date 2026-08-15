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
/// Adding and removing tool favourites.
/// </summary>
public sealed class FavoriteToolSetTests
{
    [Fact]
    public void AToolThatIsNotAFavourite_BecomesOne()
    {
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle(["NETIF"], "ROUTES");

        Assert.True(toggle.Added);
        Assert.Equal(["NETIF", "ROUTES"], toggle.Favorites);
        Assert.Equal("ROUTES", toggle.NormalizedId);
    }

    [Fact]
    public void AToolThatIsAlreadyAFavourite_StopsBeingOne()
    {
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle(["NETIF", "ROUTES"], "NETIF");

        Assert.False(toggle.Added);
        Assert.Equal(["ROUTES"], toggle.Favorites);
    }

    [Theory]
    [InlineData("ext:scoop:winbox")]
    [InlineData("EXT:SCOOP:WINBOX")]
    [InlineData("Ext:Scoop:WinBox")]
    public void MembershipIsCaseInsensitive_LikeTheRegistry(string stored)
    {
        // ToolRegistry indexes ids with StringComparer.OrdinalIgnoreCase, so a favourite stored in
        // one casing must be recognised when the toggle arrives in another - otherwise un-starring
        // a tool would silently star it a second time.
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle([stored], "EXT:SCOOP:winbox");

        Assert.False(toggle.Added);
        Assert.Empty(toggle.Favorites);
    }

    [Fact]
    public void TheStoredFormIsUpperCased()
    {
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle([], "ext:scoop:winbox");

        Assert.True(toggle.Added);
        Assert.Equal(["EXT:SCOOP:WINBOX"], toggle.Favorites);
        Assert.Equal("EXT:SCOOP:WINBOX", toggle.NormalizedId);
    }

    [Fact]
    public void RemovingTakesEverySpelling()
    {
        // A set that somehow holds two casings of one tool must come back holding neither, or the
        // second un-star would appear to do nothing.
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle(
            ["ROUTES", "ext:scoop:winbox", "EXT:SCOOP:WINBOX"],
            "EXT:SCOOP:WINBOX");

        Assert.False(toggle.Added);
        Assert.Equal(["ROUTES"], toggle.Favorites);
    }

    [Fact]
    public void TheOrderOfTheOtherFavouritesIsPreserved()
    {
        FavoriteToolToggle toggle = FavoriteToolSet.Toggle(["A", "B", "C"], "B");

        Assert.Equal(["A", "C"], toggle.Favorites);
    }

    [Fact]
    public void TheCallersListIsNeverMutated()
    {
        List<string> original = ["NETIF"];

        FavoriteToolSet.Toggle(original, "ROUTES");

        // This is what lets the shell persist before publishing: the live settings stay untouched
        // until the write has actually succeeded.
        Assert.Equal(["NETIF"], original);
    }

    [Fact]
    public void TheShellPersistsBeforeItTouchesAnythingInMemory()
    {
        // Ordering, checked at the source. MergeSettingAsync reloads from disk, writes, and only
        // then raises SettingsChanged, which is what refreshes _currentSettings. Editing the live
        // list first left every reader of FavoriteToolIds describing a toggle that had not reached
        // disk, and kept describing it when the write threw.
        string mainViewModel = File.ReadAllText(FindShellFile("MainViewModel.cs"));

        Assert.Contains(
            "FavoriteToolSet.Toggle(_currentSettings.FavoriteToolIds, toolId)",
            mainViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_currentSettings.FavoriteToolIds.Add(", mainViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("_currentSettings.FavoriteToolIds.RemoveAll(", mainViewModel, StringComparison.Ordinal);
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
