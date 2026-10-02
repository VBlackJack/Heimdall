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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed partial class SettingsViewModelTests
{
    /// <summary>
    /// The worded status lines of the panel are announced again when the interface language
    /// changes, and read in the new language.
    /// </summary>
    /// <remarks>
    /// The panel already followed the language for its default markers and its posture card. These
    /// five lines are computed from the localizer and were raised only when their own state
    /// changed, so after a switch they kept the old language until the PIN, the vault, the
    /// provider or the skipped version moved.
    /// </remarks>
    [Fact]
    public async Task TheWordedStatusLines_FollowALanguageSwitch()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer);
        string pinBefore = viewModel.PinStatusText;

        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await localizer.SwitchLocaleAsync("fr");

        Assert.Contains(nameof(SettingsViewModel.SkippedVersionText), raised);
        Assert.Contains(nameof(SettingsViewModel.CredProvHelpText), raised);
        Assert.Contains(nameof(SettingsViewModel.PinStatusText), raised);
        Assert.Contains(nameof(SettingsViewModel.VaultStatusText), raised);
        Assert.Contains(nameof(SettingsViewModel.AutoLockAvailabilityHint), raised);
        Assert.NotEqual(pinBefore, viewModel.PinStatusText);

        // Rewording is not an edit: the switch must not arm the unsaved-changes prompt.
        Assert.False(viewModel.IsDirty);
    }
}
