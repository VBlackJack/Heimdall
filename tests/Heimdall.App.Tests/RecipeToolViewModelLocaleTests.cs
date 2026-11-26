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
using Heimdall.App.Services;
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

    [Fact]
    public async Task ALanguageSwitchRewordsTheRowCountAndTheTruncationNoticeOfTheResult()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(
            RecipeRunStatus.Succeeded, 0, "name,port\r\nssh,22\r\nhttp,80\r\n", truncated: true));
        using RecipeToolViewModel viewModel = harness.Create(RecipeBuilders.Recipe(output: RecipeOutputFormat.Csv));
        await viewModel.RunCommand.ExecuteAsync(null);
        string english = viewModel.RowCountText;
        string englishTruncation = viewModel.TruncationText;
        Assert.NotEmpty(english);
        Assert.NotEmpty(englishTruncation);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(harness.Localizer.Format("RecipeOutputRows", 2), viewModel.RowCountText);
        Assert.Equal(harness.Localizer["RecipeOutputTruncated"], viewModel.TruncationText);
        Assert.NotEqual(english, viewModel.RowCountText);
        Assert.NotEqual(englishTruncation, viewModel.TruncationText);
    }

    [Fact]
    public async Task ATextResultKeepsAnEmptyRowCountAndNoNoticeAfterALanguageSwitch()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "plain"));
        using RecipeToolViewModel viewModel = harness.Create(RecipeBuilders.Recipe());
        await viewModel.RunCommand.ExecuteAsync(null);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(string.Empty, viewModel.RowCountText);
        Assert.Equal(string.Empty, viewModel.TruncationText);
    }

    [Fact]
    public async Task ALanguageSwitchRewordsTheGeneralErrorOfARefusedRun()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        using RecipeToolViewModel viewModel = harness.Create(RecipeBuilders.Recipe());
        await viewModel.RunCommand.ExecuteAsync(null);
        string english = viewModel.GeneralError;
        Assert.NotEmpty(english);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(harness.Localizer.Format("ErrorToolDisabledByUser", viewModel.Title), viewModel.GeneralError);
        Assert.NotEqual(english, viewModel.GeneralError);
    }

    [Fact]
    public async Task ALanguageSwitchRewordsTheErrorOfAFieldWithoutValidatingAgain()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using RecipeToolViewModel viewModel = harness.Create(RefusingRecipe());
        RecipeFieldViewModel field = Assert.Single(viewModel.Fields);
        field.Text = "-x";
        await viewModel.RunCommand.ExecuteAsync(null);
        string english = field.Error;
        Assert.NotEmpty(english);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.Equal(harness.Localizer["RecipeRefusalLeadingDash"], field.Error);
        Assert.NotEqual(english, field.Error);
    }

    [Fact]
    public async Task AnEditAfterAFieldErrorClearsItAndALaterSwitchDoesNotBringItBack()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        using RecipeToolViewModel viewModel = harness.Create(RefusingRecipe());
        RecipeFieldViewModel field = Assert.Single(viewModel.Fields);
        field.Text = "-x";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(field.HasError);

        field.Text = "x";
        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.False(field.HasError);
        Assert.Equal(string.Empty, field.Error);
    }

    [Fact]
    public async Task AGeneralErrorClearedByTheNextRunDoesNotComeBackWithALanguageSwitch()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        using RecipeToolViewModel viewModel = harness.Create(RecipeBuilders.Recipe());
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasGeneralError);
        harness.Availability.State = ToolAvailabilityState.Available;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasGeneralError);

        await harness.Localizer.SwitchLocaleAsync("fr");

        Assert.False(viewModel.HasGeneralError);
    }

    [Fact]
    public async Task ARewordingQueuedBeforeDisposeDoesNothingWhenItRunsAfterIt()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "a,b\r\n1,2\r\n"));
        QueueingDispatcher dispatcher = new();
        RecipeToolViewModel viewModel = new(
            RecipeBuilders.Recipe(output: RecipeOutputFormat.Csv),
            RecipeBuilders.StoredPath,
            RecipeBuilders.StoredSha256,
            harness.Services with { UiDispatcher = dispatcher });
        viewModel.Initialize(null, harness.Localizer);
        await viewModel.RunCommand.ExecuteAsync(null);
        await harness.Localizer.SwitchLocaleAsync("fr");
        Assert.Single(dispatcher.Queue);
        List<string?> changes = [];
        viewModel.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        viewModel.Dispose();
        dispatcher.Queue.Single()();

        Assert.Empty(changes);
    }

    private static Recipe RefusingRecipe()
        => RecipeBuilders.Recipe(
            inputs: [RecipeBuilders.Input(FieldName, RecipeInputType.String)],
            arguments: [RecipeArgument.Bare("{in:" + FieldName + "}")]);

    /// <summary>A dispatcher that holds what it is asked to run until the test runs it.</summary>
    private sealed class QueueingDispatcher : IUiDispatcher
    {
        public List<Action> Queue { get; } = [];

        public void Invoke(Action action) => Queue.Add(action);

        public T Invoke<T>(Func<T> func) => throw new NotSupportedException();

        public Task InvokeAsync(Action action) => throw new NotSupportedException();

        public Task InvokeAsync(Func<Task> action) => throw new NotSupportedException();

        public bool CheckAccess() => true;
    }
}
