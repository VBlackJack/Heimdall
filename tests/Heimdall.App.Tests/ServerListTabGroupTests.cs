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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The folder group a session tab takes is read from the session tree: same folder spelling,
/// same name, same resolved colour as the tree shows.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task ResolveTabGroup_NestedFolder_ReturnsTreeSpellingNameAndInheritedColour()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        AppSettings settings = fixture.ExpandGroups("Prod");
        settings.GroupDefaults["Prod"] = new GroupDefaultsDto { Color = "#FF0000" };
        fixture.LoadServers(
            settings,
            CreateServer("a", "A", "Prod/Web"),
            CreateServer("b", "B", " prod\\web/ "));

        SessionTabGroupInfo? first = fixture.ViewModel.ResolveTabGroup("a");
        SessionTabGroupInfo? second = fixture.ViewModel.ResolveTabGroup("b");

        Assert.Equal(new SessionTabGroupInfo("Prod/Web", "Web", "#FF0000"), first);

        // Spelled differently, filed under the same tree folder: one group, not two.
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ResolveTabGroup_ProfileOutsideAnyFolderOrUnknown_ReturnsNull()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(fixture.ExpandGroups(), CreateServer("a", "A", ""));

        Assert.Null(fixture.ViewModel.ResolveTabGroup("a"));
        Assert.Null(fixture.ViewModel.ResolveTabGroup("missing"));
    }

    [Fact]
    public async Task SetFolderColorAsync_RaisesFolderLayoutChanged_WithTheNewColourAlreadyResolvable()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(fixture.ExpandGroups("ops"), CreateServer("a", "A", "ops"));
        string? seen = null;
        fixture.ViewModel.FolderLayoutChanged += () => seen ??= fixture.ViewModel.ResolveTabGroup("a")?.Color;

        await fixture.ViewModel.SetFolderColorAsync("ops", "#00FF00");

        Assert.Equal("#00FF00", seen);
    }

    [Fact]
    public async Task RealResolver_NeverYieldsTheToolsKey()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        string[] groups = ["/tools", "\\tools", " /tools ", "tools", "Tools", "//Tools//"];
        fixture.LoadServers(
            fixture.ExpandGroups(),
            groups.Select((group, index) => CreateServer("tool-folder-" + index, "T" + index, group)).ToArray());

        for (int index = 0; index < groups.Length; index++)
        {
            SessionTabGroupInfo? info = fixture.ViewModel.ResolveTabGroup("tool-folder-" + index);

            Assert.NotNull(info);
            Assert.False(
                string.Equals(info.Key, ConnectionViewModel.ToolsTabGroupKey, StringComparison.OrdinalIgnoreCase),
                $"group '{groups[index]}' resolved to the tools key");
            Assert.False(ConnectionViewModel.IsToolsTabGroup(info.Key));
        }
    }
}
