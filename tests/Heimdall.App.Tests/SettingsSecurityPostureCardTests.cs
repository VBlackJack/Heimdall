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
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The security posture card as the Settings panel words it: live on the pending values, marked
/// unsaved against the disk, and announced as a whole.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    /// <summary>
    /// Unticking NLA flags its line at once, before any save, and the overall line says so and
    /// that the change is not saved.
    /// </summary>
    [Fact]
    public async Task ThePostureCardFollowsAPendingChangeAndSaysItIsUnsaved()
    {
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(new AppSettings());
        SecurityPostureLine nla = viewModel.SecurityPosture.Get(SecurityPostureKey.RdpNla);
        Assert.False(nla.IsRisky);
        Assert.Equal("No risky setting", viewModel.SecurityPostureSummary);

        viewModel.RdpDefaultNla = false;

        Assert.True(nla.IsRisky);
        Assert.True(nla.IsUnsaved);
        Assert.Equal(1, viewModel.SecurityPostureRiskCount);
        Assert.True(viewModel.HasSecurityPostureRisk);
        Assert.Equal("1 item needs attention, unsaved changes included", viewModel.SecurityPostureSummary);
        Assert.Equal(
            "RDP Network Level Authentication (NLA): Off. Needs attention: "
                + "Without NLA, you sign in on a server that has not proved its identity. Not saved yet",
            nla.AccessibleName);
    }

    /// <summary>A risky value already on disk is flagged without being called unsaved.</summary>
    [Fact]
    public async Task ARiskyValueOnDiskIsFlaggedButNotUnsaved()
    {
        AppSettings saved = new() { FileShareEnableTftp = true, SessionLoggingEnabled = true };
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(saved);

        SecurityPostureLine tftp = viewModel.SecurityPosture.Get(SecurityPostureKey.TftpSharing);
        Assert.True(tftp.IsRisky);
        Assert.False(tftp.IsUnsaved);
        Assert.Equal(2, viewModel.SecurityPostureRiskCount);
        Assert.Equal("2 items need attention", viewModel.SecurityPostureSummary);
    }

    /// <summary>
    /// The saved side is read from disk, not from whatever the panel was last loaded with: after
    /// Reset Defaults loads the factory values over a risky saved choice, the line is safe and
    /// unsaved, because the disk still holds the risky one.
    /// </summary>
    [Fact]
    public async Task AfterAReloadFromOutsideTheDiskTheCardStillComparesWithTheDisk()
    {
        AppSettings saved = new() { UpdateCheckEnabled = false };
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(saved);
        SecurityPostureLine updates = viewModel.SecurityPosture.Get(SecurityPostureKey.UpdateChecks);
        Assert.True(updates.IsRisky);

        viewModel.LoadFromSettings(new AppSettings());
        await viewModel.WhenSavedPostureLoadedAsync();

        Assert.False(updates.IsRisky);
        Assert.True(updates.IsUnsaved);
    }

    /// <summary>Saving makes the pending values the saved ones, and the unsaved marks go.</summary>
    [Fact]
    public async Task SavingClearsTheUnsavedMarks()
    {
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(new AppSettings());
        viewModel.SyncKnownHostsAtStartup = true;
        SecurityPostureLine sync = viewModel.SecurityPosture.Get(SecurityPostureKey.KnownHostsSync);
        Assert.True(sync.IsUnsaved);

        Assert.True(await viewModel.TrySaveAsync());

        Assert.False(sync.IsUnsaved);
        Assert.True(sync.IsRisky);
        Assert.Equal("1 item needs attention", viewModel.SecurityPostureSummary);
    }

    /// <summary>The overall line raises its change, which is what the live region announces.</summary>
    [Fact]
    public async Task TheOverallLineRaisesItsChangeForTheLiveRegion()
    {
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(new AppSettings());
        List<string> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        viewModel.SessionLoggingEnabled = true;

        Assert.Contains(nameof(SettingsViewModel.SecurityPostureSummary), raised);
    }

    /// <summary>"Go to setting" names the control the decision chose.</summary>
    [Fact]
    public async Task GoToSettingAsksForTheLinesTarget()
    {
        (SettingsViewModel viewModel, FakeConfigManager _) = await CreatePostureViewModelAsync(new AppSettings());
        List<string> asked = [];
        viewModel.SettingNavigationRequested += asked.Add;

        viewModel.FileShareEnableTftp = true;
        viewModel.SecurityPosture.Get(SecurityPostureKey.TftpSharing).GoToCommand.Execute(null);

        Assert.Equal(["Mw_SettingsEnableTftpShareCheckBox"], asked);
    }

    private static async Task<(SettingsViewModel ViewModel, FakeConfigManager Config)> CreatePostureViewModelAsync(
        AppSettings saved)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        FakeConfigManager config = new() { Settings = saved };
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(saved);
        await viewModel.WhenSavedPostureLoadedAsync();
        return (viewModel, config);
    }
}
