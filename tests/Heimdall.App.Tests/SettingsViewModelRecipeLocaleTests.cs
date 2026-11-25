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

using System.Collections.Specialized;
using System.IO;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed partial class SettingsViewModelTests
{
    // The localizer raises the language change on the thread that finished loading the file, which is
    // a pool thread. The recipe list is a bound collection: rebuilding it there is what made the
    // switch fail once the Recipes card had been shown.
    [Fact]
    public async Task ALanguageSwitchRaisedOffTheUiThread_RebuildsTheLoadedRecipeListOnTheDispatcher()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new();
        FakeUiDispatcher dispatcher = new(checkAccess: false);
        FakeRecipeStore store = new();
        store.Recipes.Add(RecipeBuilders.Loaded(RecipeBuilders.Recipe(id: "acme.one", name: "One")));
        string root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-locale-tests", Guid.NewGuid().ToString("N"));
        using RecipeSettingsViewModel recipes = new(
            store,
            new RecipeImportService(store, new FakeRecipeProgramLocator(), new FakeToolAvailability()),
            new ConfigManager(AppContext.BaseDirectory, Path.Combine(root, "config")),
            new RecipeRecordingDialogs(),
            localizer,
            new FakeRecipeFileDialogs(),
            () => { },
            dispatcher);
        SettingsViewModel viewModel = CreateRecipePanel(config, localizer, recipes: recipes, uiDispatcher: dispatcher, dialog: dialog);
        viewModel.LoadFromSettings(config.Settings);
        recipes.EnsureLoaded();
        List<bool> changesOnTheDispatcher = [];
        ((INotifyCollectionChanged)recipes.Items).CollectionChanged += (_, _) => changesOnTheDispatcher.Add(dispatcher.CheckAccess());

        // The default locale is the one the panel applies itself; the switch below is the pool-thread path.
        await Task.Run(() => localizer.SwitchLocaleAsync("fr"));

        Assert.Equal("fr", localizer.CurrentLocale);
        Assert.NotEmpty(changesOnTheDispatcher);
        Assert.All(changesOnTheDispatcher, onTheDispatcher => Assert.True(onTheDispatcher));
        Assert.Single(recipes.Items);
        Assert.Empty(dialog.WarningCalls);
    }

    [Fact]
    public async Task APickedLanguageIsNotReportedAsFailed_WhenTheRecipeListWasLoaded()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new();
        FakeUiDispatcher dispatcher = new(checkAccess: false);
        FakeRecipeStore store = new();
        store.Recipes.Add(RecipeBuilders.Loaded(RecipeBuilders.Recipe(id: "acme.one", name: "One")));
        string root = Path.Combine(Path.GetTempPath(), "heimdall-recipe-locale-tests", Guid.NewGuid().ToString("N"));
        using RecipeSettingsViewModel recipes = new(
            store,
            new RecipeImportService(store, new FakeRecipeProgramLocator(), new FakeToolAvailability()),
            new ConfigManager(AppContext.BaseDirectory, Path.Combine(root, "config")),
            new RecipeRecordingDialogs(),
            localizer,
            new FakeRecipeFileDialogs(),
            () => { },
            dispatcher);
        SettingsViewModel viewModel = CreateRecipePanel(config, localizer, recipes: recipes, uiDispatcher: dispatcher, dialog: dialog);
        viewModel.LoadFromSettings(config.Settings);
        recipes.EnsureLoaded();
        int onTheDispatcher = 0;
        int offTheDispatcher = 0;
        ((INotifyCollectionChanged)recipes.Items).CollectionChanged += (_, _) =>
        {
            if (dispatcher.CheckAccess())
            {
                onTheDispatcher++;
            }
            else
            {
                offTheDispatcher++;
            }
        };

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();

        Assert.Equal("fr", localizer.CurrentLocale);
        Assert.Equal("fr", viewModel.DefaultLocale);
        Assert.Empty(dialog.WarningCalls);
        Assert.Equal(0, offTheDispatcher);
        Assert.True(onTheDispatcher > 0);
    }
}
