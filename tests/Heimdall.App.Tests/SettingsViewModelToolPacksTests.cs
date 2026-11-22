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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

/// <summary>
/// The "Tool packs" block of the Settings panel: its switches over the pending list of disabled
/// packs, the policy lock, the tool counts, Restore defaults and the unsaved-changes state.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private static readonly ToolPack[] OptionalPacks =
        [ToolPack.Essentials, ToolPack.NetworkDiagnostics, ToolPack.Administration, ToolPack.Audit];

    private static ToolAvailability AvailabilityForbidding(params ToolPack[] forbidden)
        => new(
            new ToolRegistry(),
            new ToolPackPolicy(name => forbidden.Any(pack => ToolPackPolicy.ValueNameFor(pack) == name)
                ? ToolPackPolicy.ForbiddenValue
                : null),
            () => new AppSettings(),
            _ => { });

    private static SettingsViewModel LoadedToolPackPanel(
        FakeConfigManager config,
        IToolAvailability? availability = null)
    {
        SettingsViewModel viewModel = CreateViewModel(config, toolAvailability: availability);
        viewModel.LoadFromSettings(config.Settings);
        return viewModel;
    }

    private static ToolPackSwitchViewModel SwitchOf(SettingsViewModel viewModel, ToolPack pack)
        => Assert.Single(viewModel.ToolPackSwitches, toolPackSwitch => toolPackSwitch.Pack == pack);

    [Fact]
    public void TheToolPackSwitches_AreTheOptionalPacksOnly_AndCoreHasNone()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        Assert.Equal(OptionalPacks, viewModel.ToolPackSwitches.Select(toolPackSwitch => toolPackSwitch.Pack));
        Assert.DoesNotContain(viewModel.ToolPackSwitches, toolPackSwitch => toolPackSwitch.Pack == ToolPack.Core);
    }

    [Fact]
    public void TheSwitches_ReflectTheDisabledList()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit), nameof(ToolPack.Administration)] },
        };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        Assert.True(SwitchOf(viewModel, ToolPack.Essentials).IsEnabled);
        Assert.True(SwitchOf(viewModel, ToolPack.NetworkDiagnostics).IsEnabled);
        Assert.False(SwitchOf(viewModel, ToolPack.Administration).IsEnabled);
        Assert.False(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);
    }

    [Fact]
    public async Task TurningASwitchOn_RemovesItsNameOnSave()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [nameof(ToolPack.Audit)] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        SwitchOf(viewModel, ToolPack.Audit).IsEnabled = true;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(Assert.IsType<AppSettings>(config.SavedSettings).DisabledToolPacks);
    }

    [Fact]
    public async Task TurningASwitchOff_AddsItsNameOnSave()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        SwitchOf(viewModel, ToolPack.NetworkDiagnostics).IsEnabled = false;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            [nameof(ToolPack.NetworkDiagnostics)],
            Assert.IsType<AppSettings>(config.SavedSettings).DisabledToolPacks);
    }

    [Fact]
    public async Task ANameTheBuildDoesNotKnow_IsPreservedByEveryToggle()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { DisabledToolPacks = ["FuturePack", nameof(ToolPack.Audit)] },
        };
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        SwitchOf(viewModel, ToolPack.Audit).IsEnabled = true;
        SwitchOf(viewModel, ToolPack.Administration).IsEnabled = false;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            ["FuturePack", nameof(ToolPack.Administration)],
            Assert.IsType<AppSettings>(config.SavedSettings).DisabledToolPacks);
    }

    [Fact]
    public async Task CoreNeverEntersTheList_WhateverTheSwitchesDo()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        foreach (ToolPackSwitchViewModel toolPackSwitch in viewModel.ToolPackSwitches)
        {
            toolPackSwitch.IsEnabled = false;
        }

        await viewModel.SaveCommand.ExecuteAsync(null);

        string[] saved = [.. Assert.IsType<AppSettings>(config.SavedSettings).DisabledToolPacks];
        Assert.Equal(OptionalPacks.Select(pack => pack.ToString()), saved);
        Assert.DoesNotContain(nameof(ToolPack.Core), saved);
    }

    [Fact]
    public void TurningAPackOffTwice_DoesNotListItTwice()
    {
        string[] once = SettingsViewModel.WithPackState([], ToolPack.Audit, enabled: false);
        string[] twice = SettingsViewModel.WithPackState(once, ToolPack.Audit, enabled: false);

        Assert.Equal([nameof(ToolPack.Audit)], twice);
    }

    [Fact]
    public void TurningAPackOn_RemovesEveryCaseVariantOfItsName()
    {
        string[] result = SettingsViewModel.WithPackState(["audit", "Keep", "AUDIT"], ToolPack.Audit, enabled: true);

        Assert.Equal(["Keep"], result);
    }

    [Fact]
    public async Task AForbiddenPack_ReadsOffAndLocked_AndCannotBeTurnedOn()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config, AvailabilityForbidding(ToolPack.Audit));
        ToolPackSwitchViewModel audit = SwitchOf(viewModel, ToolPack.Audit);

        Assert.False(audit.IsEnabled);
        Assert.False(audit.IsEditable);
        Assert.True(audit.IsForbiddenByPolicy);
        Assert.True(audit.HasPolicyText);

        audit.IsEnabled = true;
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(audit.IsEnabled);
        Assert.False(viewModel.IsDirty);
        Assert.Empty(viewModel.DisabledToolPacks);
        Assert.True(SwitchOf(viewModel, ToolPack.Essentials).IsEditable);
        Assert.False(SwitchOf(viewModel, ToolPack.Essentials).HasPolicyText);
    }

    [Fact]
    public void AForbiddenPack_KeepsTheUsersOwnChoiceInTheList()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [] } };
        SettingsViewModel viewModel = LoadedToolPackPanel(config, AvailabilityForbidding(ToolPack.Audit));

        SwitchOf(viewModel, ToolPack.Essentials).IsEnabled = false;

        Assert.Equal([nameof(ToolPack.Essentials)], viewModel.DisabledToolPacks);
        Assert.False(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);
    }

    [Fact]
    public async Task TheToolCounts_MatchTheRegistry_AndTheirTextSaysSo()
    {
        ToolRegistry registry = new();
        SettingsViewModel viewModel = CreateViewModel(
            new FakeConfigManager(),
            localizer: await LoadLocalizerForToolPacksAsync("en"),
            toolRegistry: registry);

        foreach (ToolPackSwitchViewModel toolPackSwitch in viewModel.ToolPackSwitches)
        {
            int expected = registry.All.Count(descriptor => descriptor.Pack == toolPackSwitch.Pack);
            Assert.True(expected > 0, toolPackSwitch.Pack + " holds no tool");
            Assert.Equal(expected, toolPackSwitch.ToolCount);
            Assert.Equal(
                expected.ToString(System.Globalization.CultureInfo.InvariantCulture) + " tools",
                toolPackSwitch.CountText);
        }

        int corePermanent = registry.All.Count(descriptor => descriptor.Pack == ToolPack.Core);
        Assert.Equal(
            registry.All.Count(descriptor => descriptor.Pack is not null),
            viewModel.ToolPackSwitches.Sum(toolPackSwitch => toolPackSwitch.ToolCount) + corePermanent);
    }

    [Fact]
    public async Task OnlyTheAuditSwitch_CarriesTheWarning_AndTheSwitchesAreWordedFromTheLocale()
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync("en");
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer);

        foreach (ToolPackSwitchViewModel toolPackSwitch in viewModel.ToolPackSwitches)
        {
            Assert.Equal(toolPackSwitch.Pack == ToolPack.Audit, toolPackSwitch.HasWarning);
            Assert.Equal(localizer[SettingsViewModel.ToolPackLabelKey(toolPackSwitch.Pack)], toolPackSwitch.Label);
            Assert.Equal(localizer[SettingsViewModel.ToolPackDescriptionKey(toolPackSwitch.Pack)], toolPackSwitch.Description);
        }

        Assert.Equal("Audit and reconnaissance", SwitchOf(viewModel, ToolPack.Audit).Label);
    }

    [Fact]
    public void AToggle_MakesThePanelDirty_AndTogglingBackClearsIt()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);
        viewModel.IsDirty = false;

        SwitchOf(viewModel, ToolPack.Essentials).IsEnabled = false;
        Assert.True(viewModel.IsDirty);

        SwitchOf(viewModel, ToolPack.Essentials).IsEnabled = true;
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void TheDefaultMarker_ComparesTheListWithTheFactoryList()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFileForToolPacks()));
        Assert.False(viewModel.Defaults[nameof(SettingsViewModel.DisabledToolPacks)].IsModified);

        SwitchOf(viewModel, ToolPack.Audit).IsEnabled = true;
        Assert.True(viewModel.Defaults[nameof(SettingsViewModel.DisabledToolPacks)].IsModified);

        viewModel.ResetSettingToDefault(nameof(SettingsViewModel.DisabledToolPacks));
        Assert.Equal([nameof(ToolPack.Audit)], viewModel.DisabledToolPacks);
        Assert.False(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);
    }

    [Fact]
    public async Task RestoreDefaults_GivesTheFactoryList_WhichTurnsAuditOff()
    {
        FakeConfigManager config = new() { Settings = new AppSettings { DisabledToolPacks = [] } };
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);
        Assert.True(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        AppSettings factory = SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFileForToolPacks());
        Assert.Equal(factory.DisabledToolPacks, viewModel.DisabledToolPacks);
        Assert.Contains(nameof(ToolPack.Audit), viewModel.DisabledToolPacks);
        Assert.False(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);
        Assert.True(SwitchOf(viewModel, ToolPack.Essentials).IsEnabled);
    }

    [Fact]
    public void AnImportedList_ReachesTheSwitches()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = LoadedToolPackPanel(config);

        viewModel.LoadFromSettings(new AppSettings { DisabledToolPacks = [nameof(ToolPack.Administration)] });

        Assert.False(SwitchOf(viewModel, ToolPack.Administration).IsEnabled);
        Assert.True(SwitchOf(viewModel, ToolPack.Audit).IsEnabled);
    }

    private static string ReadFactoryDefaultsFileForToolPacks()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        Assert.True(File.Exists(path), $"settings.default.json not found at {path}");
        return File.ReadAllText(path);
    }

    private static async Task<LocalizationManager> LoadLocalizerForToolPacksAsync(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return localizer;
    }
}
