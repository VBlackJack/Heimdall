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
using System.Reflection;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The "modified from default" markers and the per-setting reset of the Settings panel.
/// </summary>
/// <remarks>
/// A partial of <see cref="SettingsViewModelTests"/> so the panel is built over the same fakes the
/// rest of the settings tests use, with the same counters of what reached the disk.
/// </remarks>
public sealed partial class SettingsViewModelTests
{
    /// <summary>
    /// The language is the one marked setting the sweep cannot modify by assignment: a pick that
    /// does not load is put back at once, so it would read as never modified. Its default and its
    /// marker are asserted on their own below.
    /// </summary>
    private static readonly HashSet<string> SweptElsewhere = new(StringComparer.Ordinal)
    {
        nameof(SettingsViewModel.DefaultLocale),
    };

    /// <summary>A panel loaded from the factory file shows no marker at all.</summary>
    /// <remarks>
    /// The strongest statement of "the markers compare against the factory defaults": every one of
    /// them, loaded through the same path as a startup, agrees that nothing is modified. A marker
    /// that compared a null path with an empty box, or an enum with its name, would stand here.
    /// </remarks>
    [Fact]
    public void APanelLoadedFromTheFactoryDefaultsShowsNoMarker()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));

        List<string> marked = viewModel.Defaults.All
            .Where(state => state.IsModified)
            .Select(state => state.Setting)
            .ToList();

        Assert.True(viewModel.Defaults.Settings.Count >= 90, $"only {viewModel.Defaults.Settings.Count} settings carry a marker");
        Assert.True(marked.Count == 0, "marked on a factory panel: " + string.Join(", ", marked));
    }

    /// <summary>A changed value is marked, and changing it back unmarks it.</summary>
    [Fact]
    public void AChangedValueIsMarkedAndTheDefaultValueIsNot()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));
        SettingDefaultState nla = viewModel.Defaults[nameof(SettingsViewModel.RdpDefaultNla)];
        Assert.False(nla.IsModified);

        viewModel.RdpDefaultNla = false;
        Assert.True(nla.IsModified);
        Assert.True(nla.ResetCommand.CanExecute(null));

        viewModel.RdpDefaultNla = true;
        Assert.False(nla.IsModified);
        Assert.False(nla.ResetCommand.CanExecute(null));
    }

    /// <summary>
    /// A number typed into its box is marked once the box commits it, through the number the box
    /// edits rather than the text.
    /// </summary>
    [Fact]
    public void ATypedNumberIsMarked()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));

        SettingDefaultState fontSize = viewModel.Defaults[nameof(SettingsViewModel.TerminalFontSize)];
        Assert.False(fontSize.IsModified);

        viewModel.TerminalFontSizeText = "20";

        Assert.True(fontSize.IsModified);
    }

    /// <summary>
    /// The per-setting reset puts the default back as a pending edit: the value and its box both
    /// show the default, the panel is dirty, and nothing reached the disk.
    /// </summary>
    [Fact]
    public void ResettingOneSettingMakesItTheDefaultAsAPendingEditWithoutWriting()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        AppSettings saved = SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile());
        int factorySize = saved.TerminalFontSize;
        saved.TerminalFontSize = factorySize + 6;
        saved.FileShareEnableTftp = true;
        viewModel.LoadFromSettings(saved);
        Assert.False(viewModel.IsDirty);

        viewModel.Defaults[nameof(SettingsViewModel.TerminalFontSize)].ResetCommand.Execute(null);

        Assert.Equal(factorySize, viewModel.TerminalFontSize);
        Assert.Equal(factorySize.ToString(System.Globalization.CultureInfo.InvariantCulture), viewModel.TerminalFontSizeText);
        Assert.False(viewModel.Defaults[nameof(SettingsViewModel.TerminalFontSize)].IsModified);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.FileShareEnableTftp, "a per-setting reset touched another setting");
        Assert.Equal(0, config.MergeSettingCallCount);
        Assert.Equal(0, config.SaveSettingsCallCount);
        Assert.Empty(config.PersistenceCalls);
    }

    /// <summary>
    /// Every setting with a marker has a default and a reset path that reaches it.
    /// </summary>
    /// <remarks>
    /// The sweep modifies each marked setting, checks the marker sees it, resets it through its own
    /// command and checks the value is the factory default again. A setting whose default could not
    /// be read, whose type the comparison did not know, or whose reset wrote somewhere else, fails
    /// here under its own name.
    /// </remarks>
    [Fact]
    public void EveryMarkedSettingHasADefaultAndAResetPathThatReachesIt()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));

        List<string> swept = viewModel.Defaults.Settings.Where(name => !SweptElsewhere.Contains(name)).ToList();
        List<string> failures = MarkerResetFailures(viewModel, swept);

        Assert.True(swept.Count >= 90, $"only {swept.Count} marked settings were swept");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        string locale = nameof(SettingsViewModel.DefaultLocale);
        Assert.True(viewModel.Defaults.Contains(locale));
        Assert.Equal(SettingsViewModel.FactoryDefaults.DefaultLocale, viewModel.DefaultPanelValueOf(locale));
    }

    /// <summary>
    /// The positive control: the sweep reports a name with no marker, and a marker whose reset
    /// does not reach the default.
    /// </summary>
    [Fact]
    public void TheResetSweepReportsAMissingMarkerAndAResetThatMissesTheDefault()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));

        List<string> failures = MarkerResetFailures(
            viewModel,
            [nameof(SettingsViewModel.CredentialProviderUnlockSecret), "NotASetting"]);

        Assert.Equal(2, failures.Count);
        Assert.False(viewModel.Defaults.Contains(nameof(SettingsViewModel.CredentialProviderUnlockSecret)));
        Assert.True(SettingsViewModel.DefaultMarkerExclusions.ContainsKey(nameof(SettingsViewModel.CredentialProviderUnlockSecret)));
    }

    /// <summary>
    /// What a screen reader hears on the badge and on the reset button names the default value.
    /// </summary>
    [Fact]
    public async Task TheBadgeAndTheResetButtonNameTheDefaultForAScreenReader()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer);
        viewModel.LoadFromSettings(SettingsViewModel.ParseFactoryDefaults(ReadFactoryDefaultsFile()));

        viewModel.RdpDefaultNla = false;
        viewModel.TerminalFontSizeText = "20";

        SettingDefaultState nla = viewModel.Defaults[nameof(SettingsViewModel.RdpDefaultNla)];
        Assert.Equal("Modified from default: On", nla.BadgeAccessibleName);
        Assert.Equal("Reset to the default value: On", nla.ResetAccessibleName);
        Assert.Equal(
            "Modified from default: 14",
            viewModel.Defaults[nameof(SettingsViewModel.TerminalFontSize)].BadgeAccessibleName);
    }

    /// <summary>
    /// Modifies each named setting, then resets it through its marker, and names every step that
    /// did not do what it should.
    /// </summary>
    private static List<string> MarkerResetFailures(SettingsViewModel viewModel, IEnumerable<string> names)
    {
        List<string> failures = [];
        foreach (string name in names)
        {
            if (!viewModel.Defaults.Contains(name))
            {
                failures.Add($"{name}: no marker");
                continue;
            }

            PropertyInfo property = typeof(SettingsViewModel).GetProperty(name)!;
            object? changed = DifferentValue(property.PropertyType, property.GetValue(viewModel));
            if (changed is null)
            {
                failures.Add($"{name}: the sweep cannot modify a {property.PropertyType.Name}");
                continue;
            }

            property.SetValue(viewModel, changed);
            SettingDefaultState state = viewModel.Defaults[name];
            if (!state.IsModified)
            {
                failures.Add($"{name}: modified and not marked");
                continue;
            }

            state.ResetCommand.Execute(null);
            if (state.IsModified || viewModel.IsModifiedFromDefault(name))
            {
                failures.Add($"{name}: reset and still not the default");
            }
        }

        return failures;
    }

    private static string ReadFactoryDefaultsFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        Assert.True(File.Exists(path), $"settings.default.json not found at {path}");
        return File.ReadAllText(path);
    }
}
