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

using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The MobaXterm variables card names the variables the shared sources use and no row defines,
/// and adds one in a click: the case of a login typed in the Name column instead of the Value one.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    // Producer: SettingsViewModel.FindMissingMobaXtermVariables, a pure function over the source
    // states and the row names.
    [Fact]
    public void MissingVariables_AreTheNamesLoadedSourcesUse_ThatNoRowNames_InAnyCase()
    {
        SharedSessionSourceState team = LoadedUsing("teamsrc1", "Team", "DAS", "NOPE");
        SharedSessionSourceState lab = LoadedUsing("labsrc1", "Lab", "das", "OPS");
        SharedSessionSourceState failed = StubSharedSessionProvider.Failed("failsrc1", "Failed", "gone") with { Variables = ["LOST"] };

        IReadOnlyList<(string Name, IReadOnlyList<string> Sources)> missing = SettingsViewModel.FindMissingMobaXtermVariables(
            [team, lab, failed],
            [" nope ", "jdoe", ""]);

        Assert.Equal(new[] { "DAS", "OPS" }, missing.Select(found => found.Name));
        Assert.Equal(new[] { "Team", "Lab" }, missing[0].Sources);
        Assert.Equal(new[] { "Lab" }, missing[1].Sources);
        Assert.Empty(SettingsViewModel.FindMissingMobaXtermVariables([team], ["das", "NOPE"]));
        Assert.Empty(SettingsViewModel.FindMissingMobaXtermVariables([], []));
    }

    [Fact]
    public async Task MissingVariables_ALoginTypedAsTheName_ShowsTheHint_AndOneClickAddsTheRow()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.MobaXtermUserVariables.Clear();
        config.Settings.MobaXtermUserVariables["jdoe"] = "%USERNAME%";
        var provider = new StubSharedSessionProvider([], [], [LoadedUsing(SavedSourceId, "Test004", "DAS")]);
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sharedSessions: provider);
        viewModel.LoadFromSettings(config.Settings);

        MissingMobaXtermVariable das = Assert.Single(viewModel.MissingMobaXtermVariables);
        Assert.True(viewModel.HasMissingMobaXtermVariables);
        Assert.Equal("Used by shared sources but not defined: DAS (Test004)", viewModel.MissingMobaXtermVariablesText);
        Assert.Equal("Add DAS", das.AddLabel);
        Assert.Contains("Test004", das.AddAccessibleName, StringComparison.Ordinal);
        Assert.False(viewModel.IsDirty);

        viewModel.AddMissingMobaXtermVariableCommand.Execute(das);

        MobaXtermVariableItemViewModel added = viewModel.MobaXtermUserVariables[^1];
        Assert.Equal(("DAS", ""), (added.Name, added.Value));
        Assert.True(added.IsValueFocusRequested);
        Assert.Equal(localizer["SettingsMobaXtermVariableValueRequired"], added.ValueError);
        Assert.Empty(viewModel.MissingMobaXtermVariables);
        Assert.Equal(string.Empty, viewModel.MissingMobaXtermVariablesText);
        Assert.Equal("DAS added: enter its value, then save.", viewModel.MobaXtermVariablesStatus);
        Assert.True(viewModel.IsDirty);

        // Nothing is written until the user saves.
        Assert.False(config.Settings.MobaXtermUserVariables.ContainsKey("DAS"));
    }

    [Fact]
    public async Task MissingVariables_FollowTheRows_AndAnAddTwiceIsANoOp()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.MobaXtermUserVariables.Clear();
        var provider = new StubSharedSessionProvider([], [], [LoadedUsing(SavedSourceId, "Team", "DAS")]);
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sharedSessions: provider);
        viewModel.LoadFromSettings(config.Settings);
        MissingMobaXtermVariable das = Assert.Single(viewModel.MissingMobaXtermVariables);

        viewModel.AddMobaXtermVariableCommand.Execute(null);
        viewModel.MobaXtermUserVariables[^1].Name = " das ";

        Assert.Empty(viewModel.MissingMobaXtermVariables);
        int rows = viewModel.MobaXtermUserVariables.Count;
        viewModel.AddMissingMobaXtermVariableCommand.Execute(das);
        Assert.Equal(rows, viewModel.MobaXtermUserVariables.Count);

        viewModel.RemoveMobaXtermVariableCommand.Execute(viewModel.MobaXtermUserVariables[^1]);

        Assert.Equal("DAS", Assert.Single(viewModel.MissingMobaXtermVariables).Name);
    }

    private static SharedSessionSourceState LoadedUsing(string sourceId, string name, params string[] variables) =>
        StubSharedSessionProvider.Loaded(sourceId, name, 1) with { Variables = variables };
}
