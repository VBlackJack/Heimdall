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
/// The panel is dirty when its pending values differ from what is on disk, not when something was
/// once edited.
/// </summary>
/// <remarks>
/// Ticking "Enable TFTP sharing" and unticking it again left Save enabled and the unsaved dot on
/// the tab, over a panel whose every value was the saved one: the flag was set by any change and
/// cleared only by a load or a save.
/// </remarks>
public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task EditingACheckBoxAndBackLeavesThePanelClean()
    {
        SettingsViewModel viewModel = await CreateLoadedFromDiskAsync(new AppSettings());

        viewModel.FileShareEnableTftp = true;
        Assert.True(viewModel.IsDirty);

        viewModel.FileShareEnableTftp = false;
        Assert.False(viewModel.IsDirty);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.RevertChangesCommand.CanExecute(null));
    }

    [Fact]
    public async Task TypingANumberAndBackLeavesThePanelClean()
    {
        SettingsViewModel viewModel = await CreateLoadedFromDiskAsync(new AppSettings { TerminalFontSize = 14 });

        viewModel.TerminalFontSizeText = "20";
        Assert.True(viewModel.IsDirty);

        viewModel.TerminalFontSizeText = "14";
        Assert.False(viewModel.IsDirty);
    }

    /// <summary>A box holding text that is not its number is a pending edit, whatever the number says.</summary>
    [Fact]
    public async Task ATextThatDoesNotCommitKeepsThePanelDirty()
    {
        SettingsViewModel viewModel = await CreateLoadedFromDiskAsync(new AppSettings { TerminalFontSize = 14 });

        viewModel.TerminalFontSizeText = "14x";

        Assert.True(viewModel.IsDirty);
    }

    /// <summary>
    /// An edit that is not a value the comparison can read back - an external tool added - keeps
    /// the panel dirty even when a value is edited back afterwards.
    /// </summary>
    [Fact]
    public async Task AnEditTheComparisonCannotSeeKeepsThePanelDirty()
    {
        SettingsViewModel viewModel = await CreateLoadedFromDiskAsync(new AppSettings());

        viewModel.AddExternalToolCommand.Execute(null);
        viewModel.FileShareEnableTftp = true;
        viewModel.FileShareEnableTftp = false;

        Assert.True(viewModel.IsDirty);
    }

    /// <summary>After a save, editing back to the value just saved leaves the panel clean.</summary>
    [Fact]
    public async Task AfterASaveTheNewValuesAreTheOnesToComeBackTo()
    {
        SettingsViewModel viewModel = await CreateLoadedFromDiskAsync(new AppSettings());
        viewModel.SyncKnownHostsAtStartup = true;
        Assert.True(await viewModel.TrySaveAsync());

        viewModel.SyncKnownHostsAtStartup = false;
        Assert.True(viewModel.IsDirty);
        viewModel.SyncKnownHostsAtStartup = true;
        Assert.False(viewModel.IsDirty);
    }

    private static async Task<SettingsViewModel> CreateLoadedFromDiskAsync(AppSettings onDisk)
    {
        FakeConfigManager config = new() { Settings = onDisk };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(await config.LoadSettingsAsync());
        await viewModel.WhenSavedSettingsLoadedAsync();
        return viewModel;
    }
}
