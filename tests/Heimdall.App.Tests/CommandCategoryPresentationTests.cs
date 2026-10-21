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
using System.Windows.Data;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// A shipped category is stored as a key and shown as a glyph and a localized label on every
/// surface; a category an operator typed is shown as typed.
/// </summary>
public sealed class CommandCategoryPresentationTests
{
    private const string FrenchSecurityLabel = "\U0001F512 Sécurité et chiffrement";
    private const string CustomCategory = "Mes scripts";

    [Fact]
    public async Task AKey_ResolvesToItsGlyphAndLocalizedLabel_AndBack()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        Func<string, string> localize = key => localizer[key];

        Assert.Equal(FrenchSecurityLabel, CommandPresentationResolver.ResolveCategoryLabel(CommandCategories.Security, localize));
        Assert.Equal(CustomCategory, CommandPresentationResolver.ResolveCategoryLabel(CustomCategory, localize));

        Assert.Equal(CommandCategories.Security, CommandPresentationResolver.ResolveCategoryKey(FrenchSecurityLabel, localize));
        Assert.Equal(CustomCategory, CommandPresentationResolver.ResolveCategoryKey(CustomCategory, localize));
    }

    [Fact]
    public async Task EveryShippedKey_HasALabelInEveryLanguage()
    {
        foreach (var locale in new[] { "en", "fr", "es" })
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(locale);
            foreach (var key in CommandCategories.All)
            {
                var label = CommandPresentationResolver.ResolveCategoryLabel(key, k => localizer[k]);
                Assert.NotEqual(key, label);
                Assert.DoesNotContain("ToolCmdLib", label, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheLibrary_GroupsAndFiltersByLabel_AndNamesTheRowWithIt()
    {
        StaDispatcherRunner.Run(async () =>
        {
            LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
            var shipped = CommandLibraryTestHelpers.CreateLinuxAction("s", "Audit", "echo s");
            shipped.Category = CommandCategories.Security;
            var typed = CommandLibraryTestHelpers.CreateLinuxAction("t", "Mine", "echo t");
            typed.Category = CustomCategory;

            using var viewModel = new CommandLibraryViewModel(
                CommandLibraryTestHelpers.CreateResolverServiceProvider(shipped, typed),
                configManager: null!,
                localizer,
                new SilentDialogService(),
                gitSyncService: null!,
                transferService: null!);
            await viewModel.InitializeAsync(targetHost: null);

            Assert.Contains(FrenchSecurityLabel, viewModel.CategoryFilterItems);
            Assert.Contains(CustomCategory, viewModel.CategoryFilterItems);
            Assert.DoesNotContain(CommandCategories.Security, viewModel.CategoryFilterItems);

            var groups = viewModel.ActionsView!.Groups!.Cast<CollectionViewGroup>().Select(g => g.Name).ToList();
            Assert.Contains(FrenchSecurityLabel, groups);
            Assert.Contains(CustomCategory, groups);

            var row = viewModel.AllEntries.Single(e => e.Source.Id == "s");
            Assert.EndsWith(FrenchSecurityLabel, row.AccessibleName, StringComparison.Ordinal);

            // The combo shows labels but filters by key.
            viewModel.CategoryFilterIndex = viewModel.CategoryFilterItems.IndexOf(FrenchSecurityLabel);
            var visible = viewModel.ActionsView.Cast<CommandLibraryActionEntry>().Select(e => e.Source.Id).ToList();
            Assert.Equal(new[] { "s" }, visible);
        });
    }

    [Fact]
    public async Task TheEditor_OffersLabels_AndStoresTheKeyOfAChosenLabel()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        var action = CommandLibraryTestHelpers.CreateLinuxAction("s", "Audit", "echo s");
        action.Category = CommandCategories.Security;

        var dialog = CommandActionDialogViewModel.FromAction(action);
        dialog.UseCategoryLabels([CommandCategories.Security, CustomCategory], key => localizer[key]);

        Assert.Equal(FrenchSecurityLabel, dialog.Category);
        Assert.Equal(new[] { FrenchSecurityLabel, CustomCategory }, dialog.AvailableCategories);
        Assert.False(dialog.IsDirty);
        Assert.Equal(CommandCategories.Security, dialog.ToAction().Category);

        dialog.Category = "Nouvelle catégorie";
        Assert.Equal("Nouvelle catégorie", dialog.ToAction().Category);
    }

    [Fact]
    public async Task ThePickerAndThePalette_ReadTheCategoryByLabel()
    {
        LocalizationManager localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync("fr");
        var action = CommandLibraryTestHelpers.CreateLinuxAction("s", "Audit", "echo s");
        action.Category = CommandCategories.Security;
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);

        var picker = new CommandLibraryPickerDialogViewModel(localizer, provider.GetRequiredService<IServiceScopeFactory>());
        await picker.InitializeAsync(null);
        Assert.Equal(FrenchSecurityLabel, Assert.Single(picker.Actions).Category);

        var palette = new CommandPaletteViewModel(null!, localizer, null!, null!, null!, null!, null!, null!, null!);
        Assert.True(palette.ScoreSnippetForQuery(action, "chiffrement") > 0);
    }

    [Fact]
    public async Task AnImport_MapsAnOldShippedCategoryToItsKey()
    {
        const string json = """
            { "actions": [ { "id": "old", "title": "Old export", "category": "🔒 Security & Encryption", "platform": 1, "level": 0 } ] }
            """;
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, json);
            var target = new FakeActionService([]);

            var result = await new CommandLibraryTransferService().ImportAsync(target, path);

            Assert.Equal(1, result.Imported);
            Assert.Equal(CommandCategories.Security, Assert.Single(await target.GetAllActionsAsync()).Category);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
