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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The Recipes pack in the "Tool packs" block of the Settings panel: its switch, its caution line,
/// its tool count, and its default state (off) for the marker and the reset.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private static readonly string[] FactoryDisabledPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Recipes)];

    [Fact]
    public void TheRecipesSwitchIsListedLastWithItsOwnWordsAndACautionLine()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        ToolPackSwitchViewModel recipes = viewModel.ToolPackSwitches[^1];

        Assert.Equal(ToolPack.Recipes, recipes.Pack);
        Assert.Equal("SettingsToolPackRecipesLabel", recipes.Label);
        Assert.Equal("SettingsToolPackRecipesDescription", recipes.Description);
        Assert.True(recipes.CarriesWarning);
        Assert.True(recipes.HasWarning);
        Assert.Equal("SettingsToolPackRecipesWarning", recipes.WarningText);
        Assert.Equal(0, recipes.ToolCount);
    }

    [Fact]
    public void TheAuditSwitchKeepsItsOwnCautionLineAndTheOthersHaveNone()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        Assert.Equal("SettingsToolPackAuditWarning", SwitchOf(viewModel, ToolPack.Audit).WarningText);
        Assert.False(SwitchOf(viewModel, ToolPack.Essentials).HasWarning);
        Assert.False(SwitchOf(viewModel, ToolPack.NetworkDiagnostics).HasWarning);
        Assert.False(SwitchOf(viewModel, ToolPack.Administration).HasWarning);
    }

    [Fact]
    public void TheRecipesCountFollowsTheRegistryAfterThePanelWasBuilt()
    {
        ToolRegistry registry = new();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, toolRegistry: registry);
        viewModel.LoadFromSettings(config.Settings);
        ToolPackSwitchViewModel recipes = SwitchOf(viewModel, ToolPack.Recipes);
        List<string?> changed = [];
        recipes.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        registry.RegisterRecipeTools(
            [RecipeTestFiles.Recipe("acme.one"), RecipeTestFiles.Recipe("acme.two")],
            _ => throw new InvalidOperationException("never built"));

        Assert.Equal(2, recipes.ToolCount);
        Assert.Contains(nameof(ToolPackSwitchViewModel.ToolCount), changed);

        registry.RegisterRecipeTools([], _ => throw new InvalidOperationException("never built"));

        Assert.Equal(0, recipes.ToolCount);
    }

    [Fact]
    public void ARecipeEventBeforeTheSwitchesExistChangesNothingAndDoesNotThrow()
    {
        ToolRegistry registry = new();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, toolRegistry: registry);

        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.one")], _ => throw new InvalidOperationException("never built"));

        Assert.Equal(1, SwitchOf(viewModel, ToolPack.Recipes).ToolCount);
    }

    [Fact]
    public void ADisposedPanelNoLongerFollowsTheRegistry()
    {
        ToolRegistry registry = new();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, toolRegistry: registry);
        ToolPackSwitchViewModel recipes = SwitchOf(viewModel, ToolPack.Recipes);

        viewModel.Dispose();
        registry.RegisterRecipeTools([RecipeTestFiles.Recipe("acme.one")], _ => throw new InvalidOperationException("never built"));

        Assert.Equal(0, recipes.ToolCount);
    }

    [Fact]
    public void TheRecipesPackIsOffInTheFactoryDefaults()
    {
        Assert.Equal(
            FactoryDisabledPacks.Order(StringComparer.Ordinal),
            SettingsViewModel.FactoryDefaults.DisabledToolPacks.Order(StringComparer.Ordinal));
        Assert.True(SettingsViewModel.FactoryDefaults.RecipesPackMigrated);
    }

    [Fact]
    public void TheDefaultMarkerTreatsRecipesOffAsTheDefaultState()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [.. FactoryDisabledPacks] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        Assert.False(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));

        SwitchOf(viewModel, ToolPack.Recipes).IsEnabled = true;

        Assert.True(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));
        Assert.True(viewModel.Defaults[nameof(SettingsViewModel.DisabledToolPacks)].IsModified);
    }

    [Fact]
    public void ARecipesPackTurnedOnIsMarkedAndTheResetTurnsItOffAgain()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);
        Assert.True(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));

        viewModel.ResetSettingToDefault(nameof(SettingsViewModel.DisabledToolPacks));

        Assert.Equal(
            FactoryDisabledPacks.Order(StringComparer.Ordinal),
            viewModel.DisabledToolPacks.Order(StringComparer.Ordinal));
        Assert.False(SwitchOf(viewModel, ToolPack.Recipes).IsEnabled);
        Assert.False(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));
    }

    [Fact]
    public async Task TheDefaultMarkerNamesRecipesAmongTheDefaultPacksThatAreOff()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: await LoadLocalizerForToolPacksAsync("en"));

        string text = viewModel.FormatDisabledToolPacksDefault([.. FactoryDisabledPacks]);

        Assert.Contains("Audit and reconnaissance", text, StringComparison.Ordinal);
        Assert.Contains("Recipes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAuditOnlyListIsNowAModificationBecauseRecipesIsOffByDefaultToo()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)], RecipesPackMigrated = true },
        };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        Assert.True(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));
        Assert.True(SwitchOf(viewModel, ToolPack.Recipes).IsEnabled);
    }

    [Fact]
    public void AForbiddenRecipesPackKeepsTheStoredChoiceOnAReset()
    {
        // The user had the pack on; the organisation then forbade it. A reset must not rewrite the
        // user's stored choice for it, the way it leaves every forbidden pack alone.
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config, AvailabilityForbidding(ToolPack.Recipes));
        ToolPackSwitchViewModel recipes = SwitchOf(viewModel, ToolPack.Recipes);
        Assert.True(recipes.IsForbiddenByPolicy);
        Assert.False(recipes.IsEditable);
        Assert.False(recipes.IsEnabled);

        viewModel.ResetSettingToDefault(nameof(SettingsViewModel.DisabledToolPacks));

        Assert.Equal([nameof(ToolPack.Audit)], viewModel.DisabledToolPacks);
        Assert.False(viewModel.IsModifiedFromDefault(nameof(SettingsViewModel.DisabledToolPacks)));
    }

    [Fact]
    public void AForbiddenRecipesPackIgnoresAnAttemptToTurnItOn()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [.. FactoryDisabledPacks] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config, AvailabilityForbidding(ToolPack.Recipes));

        SwitchOf(viewModel, ToolPack.Recipes).IsEnabled = true;

        Assert.Equal(FactoryDisabledPacks, viewModel.DisabledToolPacks);
    }
}
