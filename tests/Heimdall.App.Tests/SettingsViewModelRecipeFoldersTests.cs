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
using System.Text.Json.Nodes;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The editor of the folders recipes may run programs from: a folder picker only, a reason for every
/// refusal, a pending list that travels with Save, the default marker and the settings import, and a
/// settings file that cannot smuggle a network path in.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private static async Task<(SettingsViewModel ViewModel, FakeRecipeFileDialogs Files, FakeConfigManager Config)> RecipeFolderPanelAsync(
        AppSettings? settings = null,
        LocalizationManager? localizer = null)
    {
        FakeConfigManager config = new() { Settings = settings ?? new AppSettings() };
        if (localizer is null)
        {
            localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        }

        FakeRecipeFileDialogs files = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.AttachRecipes(null, files);
        viewModel.LoadFromSettings(config.Settings);
        return (viewModel, files, config);
    }

    [Fact]
    public async Task AFolderPickedOnALocalDriveIsAddedToThePendingList()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        files.FolderPath = @"D:\Tools\";

        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);

        Assert.Equal([@"D:\Tools"], viewModel.RecipeProgramFolders);
        Assert.True(viewModel.HasRecipeProgramFolders);
        Assert.False(viewModel.IsRecipeProgramFoldersEmpty);
        Assert.False(viewModel.HasRecipeProgramFolderStatus);
    }

    [Theory]
    [InlineData(@"\\server\share\tools", "A network path cannot be added")]
    [InlineData(@"\\?\C:\Tools", "A device path cannot be added")]
    [InlineData(@"tools", "Only a full path that starts with a drive letter")]
    [InlineData(@"..\tools", "Only a full path that starts with a drive letter")]
    [InlineData(@"D:\Tools:stream", "contains a character that is not allowed")]
    public async Task AFolderThatIsNotAnAbsoluteLocalPathIsRefusedWithItsReasonAndNothingChanges(string picked, string reason)
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        files.FolderPath = picked;

        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.True(viewModel.HasRecipeProgramFolderStatus);
        Assert.Contains(reason, viewModel.RecipeProgramFolderStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledPickerChangesNothing()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        files.FolderPath = null;

        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.RecipeProgramFolders);
        Assert.False(viewModel.HasRecipeProgramFolderStatus);
    }

    [Fact]
    public async Task AFolderAlreadyListedIsRefusedWhateverItsCase()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync(
            new AppSettings { RecipeProgramFolders = [@"D:\Tools"] });
        files.FolderPath = @"d:\tools";

        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);

        Assert.Equal([@"D:\Tools"], viewModel.RecipeProgramFolders);
        Assert.Contains("already in the list", viewModel.RecipeProgramFolderStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalIsClearedByTheNextAcceptedFolder()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        files.FolderPath = @"\\server\share";
        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasRecipeProgramFolderStatus);

        files.FolderPath = @"D:\Tools";
        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasRecipeProgramFolderStatus);
    }

    [Fact]
    public async Task TheListLoadsFromTheSettingsAndIsWrittenBackOnSave()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, FakeConfigManager config) = await RecipeFolderPanelAsync(
            new AppSettings { RecipeProgramFolders = [@"D:\One"] });
        Assert.Equal([@"D:\One"], viewModel.RecipeProgramFolders);

        files.FolderPath = @"E:\Two";
        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal([@"D:\One", @"E:\Two"], Assert.IsType<AppSettings>(config.SavedSettings).RecipeProgramFolders);
    }

    [Fact]
    public async Task AnAddedFolderMakesThePanelDirtyAndRemovingItAgainClearsIt()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        viewModel.IsDirty = false;
        files.FolderPath = @"D:\Tools";

        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);
        Assert.True(viewModel.IsDirty);

        viewModel.RemoveRecipeProgramFolderCommand.Execute(@"D:\Tools");
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task TheDefaultMarkerShowsAFolderAsAModificationAndTheResetEmptiesTheList()
    {
        (SettingsViewModel viewModel, FakeRecipeFileDialogs files, _) = await RecipeFolderPanelAsync();
        Assert.False(viewModel.Defaults[nameof(SettingsViewModel.RecipeProgramFolders)].IsModified);

        files.FolderPath = @"D:\Tools";
        await viewModel.AddRecipeProgramFolderCommand.ExecuteAsync(null);
        Assert.True(viewModel.Defaults[nameof(SettingsViewModel.RecipeProgramFolders)].IsModified);

        viewModel.ResetSettingToDefault(nameof(SettingsViewModel.RecipeProgramFolders));

        Assert.Empty(viewModel.RecipeProgramFolders);
    }

    [Fact]
    public void TheListIsOneOfTheSettingsAFileCanCarryAndOneOfThePanelPersistedOnes()
    {
        Assert.Contains(nameof(AppSettings.RecipeProgramFolders), SettingsViewModel.PersistedPropertyNames);
        Assert.Contains(nameof(AppSettings.RecipeProgramFolders), SettingsTransfer.TransferableKeys);
    }

    [Fact]
    public async Task ASettingsFileThatAddsAFolderShowsItAsASensitiveChangeBeforeAnythingIsApplied()
    {
        string file = Path.Combine(Path.GetTempPath(), "heimdall-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            JsonObject document = new()
            {
                ["format"] = "heimdall-settings",
                ["version"] = 1,
                ["settings"] = new JsonObject { [nameof(AppSettings.RecipeProgramFolders)] = new JsonArray(@"D:\Tools") },
            };
            File.WriteAllText(file, document.ToJsonString());
            FakeConfigManager target = new();
            FakeDialogService dialog = new() { ConfirmResult = true };
            LocalizationManager localizer = new();
            await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
            SettingsViewModel importer = CreateViewModel(target, dialog, localizer: localizer);
            importer.LoadFromSettings(target.Settings);
            importer.SettingsImportPathProvider = () => file;

            await importer.ImportSettingsCommand.ExecuteAsync(null);

            string preview = Assert.Single(dialog.ConfirmCalls).Message;
            Assert.StartsWith(localizer["SettingsImportPreviewSensitive"], preview, StringComparison.Ordinal);
            Assert.Contains(localizer["SettingsLabelRecipeProgramFolders"], preview, StringComparison.Ordinal);
            Assert.Contains(@"D:\Tools", preview, StringComparison.Ordinal);
            Assert.Equal([@"D:\Tools"], importer.RecipeProgramFolders);
            Assert.True(importer.IsDirty);
            Assert.Equal(0, target.SaveSettingsCallCount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ASettingsFileCannotCarryANetworkOrDevicePathOrADuplicateIntoTheList()
    {
        JsonObject file = new()
        {
            ["format"] = "heimdall-settings",
            ["version"] = 1,
            ["settings"] = new JsonObject
            {
                [nameof(AppSettings.RecipeProgramFolders)] = new JsonArray(
                    @"\\evil\share", @"\\?\C:\x", "relative", @"D:\Tools\", @"d:\tools", "42"),
            },
        };

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(new AppSettings(), file);

        Assert.Equal([@"D:\Tools"], merged.RecipeProgramFolders);
        Assert.Equal(nameof(AppSettings.RecipeProgramFolders), Assert.Single(changed).Key);
    }

    [Fact]
    public async Task RemovingAFolderTakesOutOnlyThatOne()
    {
        (SettingsViewModel viewModel, _, _) = await RecipeFolderPanelAsync(
            new AppSettings { RecipeProgramFolders = [@"D:\One", @"D:\Two", @"D:\Three"] });

        viewModel.RemoveRecipeProgramFolderCommand.Execute(@"d:\two");

        Assert.Equal([@"D:\One", @"D:\Three"], viewModel.RecipeProgramFolders);
    }

    [Fact]
    public void ASettingsFileWithOnlyRefusedFoldersChangesNothing()
    {
        JsonObject file = new()
        {
            ["format"] = "heimdall-settings",
            ["version"] = 1,
            ["settings"] = new JsonObject { [nameof(AppSettings.RecipeProgramFolders)] = new JsonArray(@"\\evil\share") },
        };

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(new AppSettings(), file);

        Assert.Empty(merged.RecipeProgramFolders);
        Assert.Empty(changed);
    }
}
