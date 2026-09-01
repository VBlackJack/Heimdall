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

namespace Heimdall.App.Tests;

/// <summary>
/// The view-model half of the multi-selection drag: which sessions a drag carries, and what the
/// drop moves once it has them.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task ShouldDeferSingleSelection_OnlyForARowInsideAMultiSelection()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        // A selection of one is already exactly what a plain press would leave behind, so there is
        // nothing to defer and the press keeps its existing timing.
        Assert.False(fixture.ViewModel.ShouldDeferSingleSelection(fixture.ServerById("alpha")));

        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        Assert.True(fixture.ViewModel.ShouldDeferSingleSelection(fixture.ServerById("alpha")));
        Assert.True(fixture.ViewModel.ShouldDeferSingleSelection(fixture.ServerById("beta")));
        Assert.False(fixture.ViewModel.ShouldDeferSingleSelection(fixture.ServerById("gamma")));
        Assert.False(fixture.ViewModel.ShouldDeferSingleSelection(null));
    }

    [Fact]
    public async Task ResolveDragSelection_FromInsideTheSelection_CarriesAllOfIt()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("gamma"));

        var carried = fixture.ViewModel.ResolveDragSelection(fixture.ServerById("beta"));

        Assert.Equal(
            ["alpha", "beta", "gamma"],
            carried.Select(server => server.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ResolveDragSelection_FromOutsideTheSelection_CarriesThePressedSessionAlone()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"),
            CreateServer("gamma", "Gamma", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));

        var carried = fixture.ViewModel.ResolveDragSelection(fixture.ServerById("gamma"));

        Assert.Equal(["gamma"], carried.Select(server => server.Id).ToArray());
    }

    /// <summary>
    /// The count the status message reports is the number of rows that actually changed folder,
    /// not the size of the drag: three of the eight may already live in the target.
    /// </summary>
    [Fact]
    public async Task MoveServersToGroupAsync_MovesEverySessionAndCountsOnlyTheOnesThatChanged()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("beta", "Beta", "ops/source"),
            CreateServer("gamma", "Gamma", "ops/target"));

        int moved = await fixture.ViewModel.MoveServersToGroupAsync(
            [
                fixture.ServerById("alpha"),
                fixture.ServerById("beta"),
                fixture.ServerById("gamma")
            ],
            "ops/target");

        Assert.Equal(2, moved);
        Assert.Equal("ops/target", fixture.ServerById("alpha").Group);
        Assert.Equal("ops/target", fixture.ServerById("beta").Group);

        var persisted = (await fixture.ConfigManager.LoadServersAsync())
            .OrderBy(server => server.Id, StringComparer.Ordinal)
            .Select(server => server.Group ?? string.Empty)
            .ToArray();
        Assert.Equal(["ops/target", "ops/target", "ops/target"], persisted);
    }

    [Fact]
    public async Task MoveServersToGroupAsync_ReportsNothingWhenEverySessionIsAlreadyInTheTarget()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/target"),
            CreateServer("beta", "Beta", "ops/target"));

        int moved = await fixture.ViewModel.MoveServersToGroupAsync(
            [fixture.ServerById("alpha"), fixture.ServerById("beta")],
            "ops/target");

        Assert.Equal(0, moved);
    }

    /// <summary>
    /// The drop gate and the move have to answer the same question, or a highlighted folder accepts
    /// a drag that then moves nothing.
    /// </summary>
    [Fact]
    public async Task DropGateAndMoveAgreeOnAMixedSelection()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "ops/source", "ops/target"),
            CreateServer("alpha", "Alpha", "ops/source"),
            CreateServer("gamma", "Gamma", "ops/target"));

        var mixed = new[] { fixture.ServerById("alpha"), fixture.ServerById("gamma") };
        var settled = new[] { fixture.ServerById("gamma") };

        Assert.True(fixture.ViewModel.IsBulkMoveTargetEnabled(mixed, "ops/target"));
        Assert.False(fixture.ViewModel.IsBulkMoveTargetEnabled(settled, "ops/target"));
        Assert.Equal(0, await fixture.ViewModel.MoveServersToGroupAsync(settled, "ops/target"));
        Assert.Equal(1, await fixture.ViewModel.MoveServersToGroupAsync(mixed, "ops/target"));
    }
}
