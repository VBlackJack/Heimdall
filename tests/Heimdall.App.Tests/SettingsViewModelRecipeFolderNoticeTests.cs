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
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

public sealed partial class SettingsViewModelTests
{
    private static string WindowsFolder => RecipeMachine.Instance.WindowsDirectory;

    private static async Task<SettingsViewModel> LoadedFolderPanelAsync()
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync("en");
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        return viewModel;
    }

    [Fact]
    public async Task AddingTheWindowsFolder_Succeeds_AndSetsTheNoticeButNotTheStatus()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();
        Assert.False(string.IsNullOrWhiteSpace(WindowsFolder));

        bool added = viewModel.TryAddRecipeProgramFolder(WindowsFolder);

        Assert.True(added);
        Assert.Contains(RecipeFolderRule.Normalize(WindowsFolder), viewModel.RecipeProgramFolders);
        Assert.True(viewModel.HasRecipeProgramFolderNotice);
        Assert.StartsWith("This folder was added.", viewModel.RecipeProgramFolderNotice, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderStatus);
    }

    [Fact]
    public async Task AddingAFolderUnderWindows_SetsTheNotice()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();

        Assert.True(viewModel.TryAddRecipeProgramFolder(System.IO.Path.Combine(WindowsFolder, "System32", "Tools")));

        Assert.True(viewModel.HasRecipeProgramFolderNotice);
    }

    [Fact]
    public async Task AddingAnUnrelatedFolder_LeavesTheNoticeAndTheStatusEmpty()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();

        Assert.True(viewModel.TryAddRecipeProgramFolder(@"Z:\Elsewhere\Tools"));

        Assert.False(viewModel.HasRecipeProgramFolderNotice);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderNotice);
        Assert.Equal(string.Empty, viewModel.RecipeProgramFolderStatus);
    }

    [Fact]
    public async Task ARefusal_StillSetsTheStatusAndNoNotice_AndTakesAwayAnEarlierNotice()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();
        viewModel.TryAddRecipeProgramFolder(WindowsFolder);
        Assert.True(viewModel.HasRecipeProgramFolderNotice);

        bool added = viewModel.TryAddRecipeProgramFolder(@"C:\");

        Assert.False(added);
        Assert.False(string.IsNullOrEmpty(viewModel.RecipeProgramFolderStatus));
        Assert.False(viewModel.HasRecipeProgramFolderNotice);
    }

    [Fact]
    public async Task RemovingAFolder_ClearsTheNotice()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();
        viewModel.TryAddRecipeProgramFolder(WindowsFolder);
        Assert.True(viewModel.HasRecipeProgramFolderNotice);

        viewModel.RemoveRecipeProgramFolderCommand.Execute(RecipeFolderRule.Normalize(WindowsFolder));

        Assert.False(viewModel.HasRecipeProgramFolderNotice);
        Assert.Empty(viewModel.RecipeProgramFolders);
    }

    [Fact]
    public async Task TheNoticeIsAnnouncedThroughItsOwnProperties()
    {
        SettingsViewModel viewModel = await LoadedFolderPanelAsync();
        List<string?> changed = [];
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.TryAddRecipeProgramFolder(WindowsFolder);

        Assert.Contains(nameof(SettingsViewModel.RecipeProgramFolderNotice), changed);
        Assert.Contains(nameof(SettingsViewModel.HasRecipeProgramFolderNotice), changed);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheNoticeKeyExistsInTheThreeLanguages(string locale)
    {
        LocalizationManager localizer = await LoadLocalizerForToolPacksAsync(locale);

        Assert.True(localizer.HasKey("RecipeFolderNoticeBuiltInRoot"));
        Assert.False(string.IsNullOrWhiteSpace(localizer["RecipeFolderNoticeBuiltInRoot"]));
    }
}
