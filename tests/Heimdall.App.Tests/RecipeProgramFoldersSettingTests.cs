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
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The folders the user allows recipes to run programs from: empty by default, optional in the
/// settings file, and never added silently by a settings import.
/// </summary>
public sealed class RecipeProgramFoldersSettingTests : IDisposable
{
    private const string Profile = @"C:\Users\someone";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "heimdall-recipe-folders-tests", Guid.NewGuid().ToString("N"));

    public RecipeProgramFoldersSettingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    [Fact]
    public void ThereAreNoFoldersByDefault()
        => Assert.Empty(new AppSettings().RecipeProgramFolders);

    [Fact]
    public async Task ASettingsFileWithoutTheKeyLoadsWithNoFoldersAndTheKeyIsWrittenInCamelCase()
    {
        ConfigManager manager = new(AppContext.BaseDirectory, Path.Combine(_root, "data"));
        await manager.InitializeAsync();

        AppSettings loaded = await manager.LoadSettingsAsync();
        Assert.Empty(loaded.RecipeProgramFolders);

        loaded.RecipeProgramFolders = [@"D:\Tools"];
        await manager.SaveSettingsAsync(loaded);

        Assert.Contains("\"recipeProgramFolders\"", await File.ReadAllTextAsync(manager.SettingsPath), StringComparison.Ordinal);
        Assert.Equal([@"D:\Tools"], (await manager.LoadSettingsAsync()).RecipeProgramFolders);
    }

    [Fact]
    public void TheListIsASensitiveKeyOfTheImportPreview()
        => Assert.Contains(nameof(AppSettings.RecipeProgramFolders), SettingsImportPreview.SensitiveKeys);

    [Fact]
    public void TheListHasAPlaceInTheLabelCatalogForTheDayThePanelEditsIt()
    {
        Assert.True(SettingsLabelCatalog.TryGetPlace(nameof(AppSettings.RecipeProgramFolders), out SettingsPanelPlace? place));
        Assert.Equal("SettingsLabelRecipeProgramFolders", place!.LabelKey);
        Assert.Empty(SettingsLabelCatalog.Unplaced([nameof(AppSettings.RecipeProgramFolders)]));
    }

    [Fact]
    public void ASettingsFileCannotAddAFolderBecauseThePanelDoesNotEditTheListYet()
    {
        JsonObject file = new()
        {
            ["format"] = "heimdall-settings",
            ["version"] = 1,
            ["settings"] = new JsonObject { [nameof(AppSettings.RecipeProgramFolders)] = new JsonArray(@"D:\Tools") },
        };

        (AppSettings merged, IReadOnlyList<SettingsTransferChange> changed) = SettingsTransfer.Import(new AppSettings(), file);

        Assert.Empty(merged.RecipeProgramFolders);
        Assert.DoesNotContain(changed, item => item.Key == nameof(AppSettings.RecipeProgramFolders));
    }

    [Fact]
    public async Task OnceTheListIsTransferableAChangeToItIsShownAsSensitiveWithItsLabelAndEveryFolder()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), "en");
        SettingsTransferChange change = new(
            nameof(AppSettings.RecipeProgramFolders),
            new JsonArray(@"D:\Tools", @"E:\More"),
            new JsonArray());

        string preview = SettingsImportPreview.Compose(localizer, [change]);

        Assert.StartsWith(localizer["SettingsImportPreviewSensitive"], preview, StringComparison.Ordinal);
        Assert.Contains(localizer["SettingsLabelRecipeProgramFolders"], preview, StringComparison.Ordinal);
        Assert.Contains(@"D:\Tools", preview, StringComparison.Ordinal);
        Assert.Contains(@"E:\More", preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheLabelIsInEveryLanguage(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RecipeViewModelHarness.RepositoryRoot(), "locales"), locale);

        Assert.True(localizer.HasKey("SettingsLabelRecipeProgramFolders"));
        Assert.False(string.IsNullOrWhiteSpace(localizer["SettingsLabelRecipeProgramFolders"]));
    }
}
