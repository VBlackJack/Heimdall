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

using System.ComponentModel;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Tests;

/// <summary>
/// The recipe view follows the interface language. The localizer raises the change on the thread
/// that finished loading the language file, and the labels and hints are bound, so they are set
/// through the dispatcher.
/// </summary>
public sealed class RecipeToolViewModelLocaleTests
{
    private const string FieldName = "target";

    [Fact]
    public async Task ALanguageSwitchRaisedOffTheUiThread_RewordsTheFieldsThroughTheDispatcher()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        FakeUiDispatcher dispatcher = new(checkAccess: false);
        RecipeTranslation french = new("Sonde", "Sonde la chose.", new Dictionary<string, string> { [FieldName] = "Cible" })
        {
            FieldHints = new Dictionary<string, string> { [FieldName] = "Un nom d'hote." },
        };
        Recipe recipe = RecipeBuilders.Recipe(
            inputs: [RecipeBuilders.Input(FieldName, RecipeInputType.String, label: "Target")],
            labels: new Dictionary<string, RecipeTranslation> { ["fr"] = french });
        using RecipeToolViewModel viewModel = new(
            recipe,
            RecipeBuilders.StoredPath,
            RecipeBuilders.StoredSha256,
            harness.Services with { UiDispatcher = dispatcher });
        viewModel.Initialize(null, harness.Localizer);
        RecipeFieldViewModel field = Assert.Single(viewModel.Fields);
        List<bool> changesOnTheDispatcher = [];
        PropertyChangedEventHandler record = (_, _) => changesOnTheDispatcher.Add(dispatcher.CheckAccess());
        viewModel.PropertyChanged += record;
        field.PropertyChanged += record;

        await Task.Run(() => harness.Localizer.SwitchLocaleAsync("fr"));

        Assert.Equal("Cible", field.Label);
        Assert.Equal("Un nom d'hote.", field.Hint);
        Assert.Equal("Sonde", viewModel.Title);
        Assert.NotEmpty(changesOnTheDispatcher);
        Assert.All(changesOnTheDispatcher, onTheDispatcher => Assert.True(onTheDispatcher));
    }
}
