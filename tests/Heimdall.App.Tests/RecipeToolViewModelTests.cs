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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;
using static Heimdall.App.Tests.RecipeBuilders;

namespace Heimdall.App.Tests;

/// <summary>
/// The recipe view model: the fields it builds, the session target it reads, what stops a run
/// before anything starts, the folder each run gets, and how a result is put into words. The
/// runner is a fake: no test starts a process.
/// </summary>
public sealed class RecipeToolViewModelTests
{
    private static readonly RecipeArgument HostArgument = RecipeArgument.Bare("{Host}");

    private static readonly RecipeArgument PortArgument = RecipeArgument.Bare("{Port}");

    private static RecipeArgument Field(string name) => RecipeArgument.Bare("{in:" + name + "}");

    private static ToolContext Session(string host = "10.0.0.5", int? port = 22) => new(TargetHost: host, TargetPort: port);

    // ---- fields ---------------------------------------------------------------------------------

    [Fact]
    public async Task EveryInputTypeBecomesTheRightKindOfField()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(inputs:
        [
            Input("s", RecipeInputType.String),
            Input("i", RecipeInputType.Int),
            Input("n", RecipeInputType.Number),
            Input("h", RecipeInputType.Hostname),
            Input("a", RecipeInputType.IpAddress),
            Input("p", RecipeInputType.Path),
            Input("b", RecipeInputType.Bool),
            Input("c", RecipeInputType.Choice, required: true, choices: ["fast", "slow"]),
        ]);

        RecipeToolViewModel viewModel = harness.Create(recipe);

        Assert.Equal(8, viewModel.Fields.Count);
        Assert.All(viewModel.Fields.Take(6), field => Assert.True(field.IsText && !field.IsBool && !field.IsChoice));
        RecipeFieldViewModel flag = viewModel.Fields[6];
        Assert.True(flag.IsBool && !flag.IsText && !flag.ShowLabel);
        RecipeFieldViewModel choice = viewModel.Fields[7];
        Assert.True(choice.IsChoice && !choice.IsText);
        Assert.Equal(["fast", "slow"], choice.Choices);
    }

    [Fact]
    public async Task DefaultsArePrefilled()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(inputs:
        [
            Input("s", RecipeInputType.String, defaultValue: "hello"),
            Input("b", RecipeInputType.Bool, defaultValue: "true"),
            Input("o", RecipeInputType.Bool, defaultValue: "false"),
            Input("c", RecipeInputType.Choice, defaultValue: "slow", choices: ["fast", "slow"]),
            Input("e", RecipeInputType.String),
        ]);

        RecipeToolViewModel viewModel = harness.Create(recipe);

        Assert.Equal("hello", viewModel.Fields[0].Text);
        Assert.True(viewModel.Fields[1].IsChecked);
        Assert.False(viewModel.Fields[2].IsChecked);
        Assert.Equal("slow", viewModel.Fields[3].SelectedChoice);
        Assert.Equal(string.Empty, viewModel.Fields[4].Text);
    }

    [Fact]
    public async Task ARequiredChoiceWithoutDefaultStartsOnItsFirstValueAndAnOptionalOneMayStayEmpty()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(inputs:
        [
            Input("r", RecipeInputType.Choice, required: true, choices: ["one", "two"]),
            Input("o", RecipeInputType.Choice, choices: ["one", "two"]),
        ]);

        RecipeToolViewModel viewModel = harness.Create(recipe);

        Assert.Equal("one", viewModel.Fields[0].SelectedChoice);
        Assert.Equal(string.Empty, viewModel.Fields[1].SelectedChoice);
        Assert.Equal(["", "one", "two"], viewModel.Fields[1].Choices);
    }

    [Fact]
    public async Task EachFieldHasItsLabelAsItsAutomationName()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(inputs: [Input("t", RecipeInputType.Hostname, label: "Target")]));

        Assert.Equal("Target", viewModel.Fields[0].Label);
        Assert.Equal("Target", viewModel.Fields[0].AutomationName);
    }

    // ---- labels ---------------------------------------------------------------------------------

    private static Recipe Translated() => Recipe(
        name: "Probe",
        inputs: [Input("t", RecipeInputType.Hostname, label: "Target"), Input("c", RecipeInputType.Int, label: "Count")],
        labels: new Dictionary<string, RecipeTranslation>
        {
            ["fr"] = new("Sonde", "Sonde la chose.", new Dictionary<string, string> { ["t"] = "Cible" }),
        });

    [Fact]
    public async Task ALabelForTheCurrentLanguageIsUsedForNameDescriptionAndFields()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("fr");

        RecipeToolViewModel viewModel = harness.Create(Translated());

        Assert.Equal("Sonde", viewModel.Title);
        Assert.Equal("Sonde la chose.", viewModel.Description);
        Assert.Equal("Cible", viewModel.Fields[0].Label);
        Assert.Equal("Count", viewModel.Fields[1].Label);
    }

    [Fact]
    public async Task WithoutALabelForTheLanguageTheEnglishBaseIsUsed()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("es");

        RecipeToolViewModel viewModel = harness.Create(Translated());

        Assert.Equal("Probe", viewModel.Title);
        Assert.Equal("Probes the thing.", viewModel.Description);
        Assert.Equal("Target", viewModel.Fields[0].Label);
    }

    [Fact]
    public void ARegionalTagMatchesItsLanguageAndAnExactTagWinsOverIt()
    {
        Recipe recipe = Recipe(labels: new Dictionary<string, RecipeTranslation>
        {
            ["fr-CA"] = new("Sonde canadienne", null, new Dictionary<string, string>()),
            ["fr"] = new("Sonde", null, new Dictionary<string, string>()),
        });

        Assert.Equal("Sonde canadienne", RecipeToolText.Resolve(recipe, "fr-CA").Name);
        Assert.Equal("Sonde", RecipeToolText.Resolve(recipe, "fr").Name);
        Assert.Equal("Sonde", RecipeToolText.Resolve(recipe, "FR").Name);
        Assert.Equal("Probe", RecipeToolText.Resolve(recipe, "es").Name);
        Assert.Equal("Probes the thing.", RecipeToolText.Resolve(recipe, "fr").Description);
    }

    [Fact]
    public async Task ATranslatedNameThatLooksLikeALocaleKeyIsStillPlainText()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync("fr");
        Recipe recipe = Recipe(labels: new Dictionary<string, RecipeTranslation>
        {
            ["fr"] = new("PaletteToolPing", null, new Dictionary<string, string>()),
        });

        Assert.Equal("PaletteToolPing", harness.Create(recipe).Title);
    }

    // ---- session target -------------------------------------------------------------------------

    [Fact]
    public async Task ARecipeThatUsesTheHostAndThePortShowsTheSessionItWillUse()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();

        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument, PortArgument]), Session("10.0.0.5", 2222));

        Assert.True(viewModel.UsesSession);
        Assert.Contains("10.0.0.5", viewModel.SessionTargetText);
        Assert.Contains("2222", viewModel.SessionTargetText);
    }

    [Fact]
    public async Task ARecipeThatUsesOnlyTheHostShowsOnlyTheHost()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();

        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument]), Session("srv01", 22));

        Assert.Contains("srv01", viewModel.SessionTargetText);
        Assert.DoesNotContain("22", viewModel.SessionTargetText);
    }

    [Fact]
    public async Task ARecipeThatUsesNeitherShowsNothingAboutSessions()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();

        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [RecipeArgument.Bare("-n")]), Session());

        Assert.False(viewModel.UsesSession);
        Assert.Equal(string.Empty, viewModel.SessionTargetText);
    }

    [Fact]
    public async Task WithoutASessionTheViewSaysSoAndTheRunIsRefusedWithAClearMessage()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument]));

        Assert.Equal(harness.Localizer["RecipeSessionTargetNone"], viewModel.SessionTargetText);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(harness.Localizer["RecipeRunNoSession"], viewModel.GeneralError);
        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ARecipeThatNeedsThePortIsRefusedWhenTheSessionHasNone()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument, PortArgument]), Session("srv01", port: null));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(harness.Localizer["RecipeRunNoSession"], viewModel.GeneralError);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public async Task ARecipeThatUsesNoSessionRunsWithoutOne()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [RecipeArgument.Bare("-n")]));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Single(harness.Runner.Calls);
        Assert.Equal(string.Empty, viewModel.GeneralError);
    }

    [Fact]
    public async Task TheSessionValuesReachTheArgumentsAsTheyAre()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument, PortArgument]), Session("srv01", 2222));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(["srv01", "2222"], harness.Runner.Calls.Single().Arguments);
    }

    // ---- refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedValueIsShownOnItsOwnFieldInWords()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(
            inputs: [Input("good", RecipeInputType.String), Input("bad", RecipeInputType.String)],
            arguments: [Field("good"), Field("bad")]);
        RecipeToolViewModel viewModel = harness.Create(recipe);
        viewModel.Fields[0].Text = "fine";
        viewModel.Fields[1].Text = "-rf";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.False(viewModel.Fields[0].HasError);
        Assert.True(viewModel.Fields[1].HasError);
        Assert.Equal(harness.Localizer["RecipeRefusalLeadingDash"], viewModel.Fields[1].Error);
        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task AMissingRequiredValueIsShownOnItsField()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(inputs: [Input("t", RecipeInputType.String, required: true)], arguments: [Field("t")]);
        RecipeToolViewModel viewModel = harness.Create(recipe);

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(harness.Localizer["RecipeRefusalRequiredMissing"], viewModel.Fields[0].Error);
    }

    [Fact]
    public async Task EditingAFieldClearsItsRefusal()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(inputs: [Input("t", RecipeInputType.String)], arguments: [Field("t")]);
        RecipeToolViewModel viewModel = harness.Create(recipe);
        viewModel.Fields[0].Text = "-x";
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.True(viewModel.Fields[0].HasError);

        viewModel.Fields[0].Text = "x";

        Assert.False(viewModel.Fields[0].HasError);
    }

    [Fact]
    public async Task AHostThatBreaksTheValueRuleIsRefusedAsTheSessionValue()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(arguments: [HostArgument]), Session("-oProxyCommand=x"));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.StartsWith(
            harness.Localizer.Format("RecipeRunSessionValueRefused", string.Empty).TrimEnd(),
            viewModel.GeneralError,
            StringComparison.Ordinal);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public async Task TheValuesTheUserTypedAreHandedToTheExpansionAsTheyAre()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Recipe recipe = Recipe(
            inputs:
            [
                Input("t", RecipeInputType.String),
                Input("flag", RecipeInputType.Bool, whenTrue: ["-v"]),
                Input("c", RecipeInputType.Choice, choices: ["fast", "slow"]),
            ],
            arguments: [Field("t"), Field("flag"), RecipeArgument.Bare("--mode={in:c}")]);
        RecipeToolViewModel viewModel = harness.Create(recipe);
        viewModel.Fields[0].Text = "two words";
        viewModel.Fields[1].IsChecked = true;
        viewModel.Fields[2].SelectedChoice = "slow";

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(["two words", "-v", "--mode=slow"], harness.Runner.Calls.Single().Arguments);
    }

    // ---- program check --------------------------------------------------------------------------

    [Fact]
    public async Task ASameProgramRunsWithTheStoredPathAndTheFoldersTheUserAllowed()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.AllowedFolders.Add(@"D:\Tools");
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        FakeRecipeRunner.RunCall call = Assert.Single(harness.Runner.Calls);
        Assert.Equal(StoredPath, call.ProgramPath);
        (string? program, IReadOnlyList<string> folders, string stored) = Assert.Single(harness.Resolver.Calls);
        Assert.Equal("ping.exe", program);
        Assert.Equal([@"D:\Tools"], folders);
        Assert.Equal(StoredPath, stored);
    }

    [Theory]
    [InlineData(RecipeProgramComparison.Different, "RecipeProgramDifferent")]
    [InlineData(RecipeProgramComparison.NoLongerResolves, "RecipeProgramNoLongerResolves")]
    public async Task AProgramThatChangedStopsTheRunAndNamesTheStoredPath(RecipeProgramComparison answer, string key)
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Resolver.Answer = answer;
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(harness.Localizer.Format(key, StoredPath), viewModel.GeneralError);
        Assert.Contains(StoredPath, viewModel.GeneralError);
        Assert.Empty(harness.Runner.Calls);
        Assert.Empty(harness.RunFolders());
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task TheFoldersAreReadAgainAtEachRun()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);
        harness.AllowedFolders.Add(@"E:\More");
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.Resolver.Calls[0].Folders);
        Assert.Equal([@"E:\More"], harness.Resolver.Calls[1].Folders);
    }

    // ---- working folder -------------------------------------------------------------------------

    [Fact]
    public async Task EachRunGetsAFreshEmptyFolderThatIsDeletedAfterASuccess()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, harness.Runner.Calls.Count);
        Assert.All(harness.Runner.Calls, call => Assert.True(call.FolderWasEmptyAtStart));
        Assert.NotEqual(harness.Runner.Calls[0].WorkingDirectory, harness.Runner.Calls[1].WorkingDirectory);
        Assert.StartsWith(harness.RunsRoot, harness.Runner.Calls[0].WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(harness.RunFolders());
    }

    [Fact]
    public async Task TheFolderIsDeletedWhenTheProgramFails()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.FailedExitCode, exitCode: 3));
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.RunFolders());
    }

    [Fact]
    public async Task TheFolderIsDeletedWhenTheRunnerThrows()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => throw new InvalidOperationException("boom");
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Empty(harness.RunFolders());
        Assert.Equal(harness.Localizer["RecipeRunUnexpected"], viewModel.GeneralError);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task TheFolderIsDeletedWhenTheRunIsCancelled()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Script = async call =>
        {
            started.SetResult();
            TaskCompletionSource never = new();
            await using (call.Token.Register(() => never.TrySetResult()))
            {
                await never.Task.ConfigureAwait(false);
            }

            return FakeRecipeRunner.Result(RecipeRunStatus.Cancelled);
        };
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        Assert.Single(harness.RunFolders());
        viewModel.StopCommand.Execute(null);
        await run;

        Assert.Empty(harness.RunFolders());
        Assert.Equal(harness.Localizer["RecipeStatusCancelled"], viewModel.StatusText);
    }

    [Fact]
    public async Task StaleRunFoldersAreCleanedAtStart()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        Directory.CreateDirectory(Path.Combine(harness.RunsRoot, "left-by-a-crash"));
        File.WriteAllText(Path.Combine(harness.RunsRoot, "left-by-a-crash", "x.txt"), "x");

        harness.WorkFolders.CleanStale();

        Assert.Empty(harness.RunFolders());
    }

    // ---- one run at a time, stop, close ---------------------------------------------------------

    [Fact]
    public async Task WhileARunIsUnderWayTheViewIsLockedAndAnotherRunCannotStart()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<RecipeRunResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Script = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };
        RecipeToolViewModel viewModel = harness.Create(Recipe(inputs: [Input("t", RecipeInputType.String)]));

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;

        Assert.True(viewModel.IsRunning);
        Assert.False(viewModel.IsIdle);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        Assert.True(viewModel.StopCommand.CanExecute(null));
        Assert.All(viewModel.Fields, field => Assert.True(field.IsLocked));
        viewModel.RunCommand.Execute(null);
        Assert.Single(harness.Runner.Calls);

        release.SetResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0));
        await run;

        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        Assert.False(viewModel.StopCommand.CanExecute(null));
        Assert.All(viewModel.Fields, field => Assert.False(field.IsLocked));
    }

    [Fact]
    public async Task StopCancelsTheTokenTheRunnerWasGiven()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Runner.Script = async call =>
        {
            started.SetResult();
            TaskCompletionSource cancelled = new();
            await using (call.Token.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task.ConfigureAwait(false);
            }

            return FakeRecipeRunner.Result(RecipeRunStatus.Cancelled);
        };
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        Assert.False(harness.Runner.Calls.Single().Token.IsCancellationRequested);
        viewModel.StopCommand.Execute(null);
        await run;

        Assert.True(harness.Runner.Calls.Single().Token.IsCancellationRequested);
        Assert.True(viewModel.IsStatusFailure);
    }

    [Fact]
    public async Task ClosingTheViewCancelsTheRunWaitsForItAndRemovesTheFolder()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool killed = false;
        harness.Runner.Script = async call =>
        {
            started.SetResult();
            TaskCompletionSource cancelled = new();
            await using (call.Token.Register(() => cancelled.TrySetResult()))
            {
                await cancelled.Task.ConfigureAwait(false);
            }

            await Task.Delay(100).ConfigureAwait(false);
            killed = true;
            return FakeRecipeRunner.Result(RecipeRunStatus.Cancelled);
        };
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        Task run = viewModel.RunCommand.ExecuteAsync(null);
        await started.Task;
        await Task.Run(viewModel.Dispose);

        Assert.True(killed, "Dispose returned before the program was gone.");
        Assert.Empty(harness.RunFolders());
        await run;
    }

    [Fact]
    public async Task APackTurnedOffDuringTheSessionRefusesTheNextRun()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(name: "Probe"));
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Single(harness.Runner.Calls);

        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Single(harness.Runner.Calls);
        Assert.Equal(harness.Localizer.Format("ErrorToolDisabledByUser", "Probe"), viewModel.GeneralError);
        Assert.False(viewModel.IsRunning);
    }

    [Fact]
    public async Task ThePackTurnedOffTakesTheToolOutOfTheRegistryAndTheRunNamesThePackNotTheRecipe()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(name: "Probe"));

        // What the real service answers once the registrar removed the recipe tools of a pack that is off.
        harness.Availability.State = ToolAvailabilityState.Unknown;
        harness.Availability.PackEnabled = false;
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(harness.Localizer.Format("ErrorToolDisabledByUser", "Probe"), viewModel.GeneralError);
        Assert.Empty(harness.Runner.Calls);
    }

    [Fact]
    public async Task AForbiddenPackAndARemovedRecipeAreRefusedInTheirOwnWords()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        RecipeToolViewModel viewModel = harness.Create(Recipe(name: "Probe"));

        harness.Availability.State = ToolAvailabilityState.ForbiddenByPolicy;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(harness.Localizer.Format("ErrorToolForbiddenByPolicy", "Probe"), viewModel.GeneralError);

        harness.Availability.State = ToolAvailabilityState.Unknown;
        harness.Availability.PackForbidden = true;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(harness.Localizer.Format("ErrorToolForbiddenByPolicy", "Probe"), viewModel.GeneralError);

        harness.Availability.PackForbidden = false;
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal(harness.Localizer["RecipeRunToolRemoved"], viewModel.GeneralError);
        Assert.Empty(harness.Runner.Calls);
    }

    // ---- result ---------------------------------------------------------------------------------

    public static TheoryData<RecipeRunStatus, int?, RecipeRunRefusal, string> StatusCases() => new()
    {
        { RecipeRunStatus.Succeeded, 0, RecipeRunRefusal.None, "Succeeded." },
        { RecipeRunStatus.FailedExitCode, 7, RecipeRunRefusal.None, "Failed with exit code 7." },
        { RecipeRunStatus.TimedOut, null, RecipeRunRefusal.None, "Timed out after 30 seconds." },
        { RecipeRunStatus.Cancelled, null, RecipeRunRefusal.None, "Cancelled." },
        { RecipeRunStatus.FailedToStart, null, RecipeRunRefusal.None, "The program could not be started." },
        { RecipeRunStatus.Refused, null, RecipeRunRefusal.WorkingDirectoryNotEmpty, "The run was refused: the working folder is not empty" },
    };

    [Theory]
    [MemberData(nameof(StatusCases))]
    public async Task EachStatusIsPutIntoWords(RecipeRunStatus status, int? exitCode, RecipeRunRefusal refusal, string expected)
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(status, exitCode, refusal: refusal));
        RecipeToolViewModel viewModel = harness.Create(Recipe(timeoutSeconds: 30));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(expected, viewModel.StatusText);
        Assert.True(viewModel.HasStatus);
        Assert.Equal(status != RecipeRunStatus.Succeeded, viewModel.IsStatusFailure);
    }

    [Fact]
    public async Task AOneSecondTimeoutUsesTheSingularWording()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.TimedOut));
        RecipeToolViewModel viewModel = harness.Create(Recipe(timeoutSeconds: 1));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal("Timed out after 1 second.", viewModel.StatusText);
    }

    [Fact]
    public async Task ACutOutputCarriesATruncationNotice()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "partial", truncated: true));
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasTruncation);
        Assert.Equal(harness.Localizer["RecipeOutputTruncated"], viewModel.TruncationText);
    }

    [Fact]
    public async Task AWholeOutputHasNoTruncationNotice()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "all"));
        RecipeToolViewModel viewModel = harness.Create(Recipe());

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasTruncation);
    }

    [Fact]
    public async Task TextOutputIsShownAsTextAndCopied()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "line one\r\nline two\r\n"));
        RecipeToolViewModel viewModel = harness.Create(Recipe(output: RecipeOutputFormat.Text));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowText);
        Assert.False(viewModel.ShowGrid);
        Assert.Equal("line one\r\nline two", viewModel.OutputText);
        Assert.Equal("line one\r\nline two", viewModel.CopyText);
        Assert.True(viewModel.HasOutput);
    }

    [Fact]
    public async Task CsvOutputIsShownAsAGrid()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "name,port\r\nssh,22\r\nhttp,80\r\n"));
        RecipeToolViewModel viewModel = harness.Create(Recipe(output: RecipeOutputFormat.Csv));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowGrid);
        Assert.False(viewModel.ShowText);
        Assert.NotNull(viewModel.OutputView);
        Assert.Equal(2, viewModel.OutputTable!.Rows.Count);
        Assert.Equal("port", viewModel.OutputTable.Columns[1].ColumnName);
        Assert.Equal("80", viewModel.OutputTable.Rows[1][1]);
        Assert.Equal("2 rows", viewModel.RowCountText);
        Assert.StartsWith("name\tport", viewModel.CopyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonOutputIsShownAsAGrid()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, """[{"a":1,"b":"x"},{"a":2,"b":"y"}]"""));
        RecipeToolViewModel viewModel = harness.Create(Recipe(output: RecipeOutputFormat.Json));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.ShowGrid);
        Assert.Equal(2, viewModel.OutputTable!.Rows.Count);
        Assert.Equal("y", viewModel.OutputTable.Rows[1]["b"]);
    }

    [Theory]
    [InlineData(RecipeOutputFormat.Json, "this is not json")]
    [InlineData(RecipeOutputFormat.Json, "[]")]
    [InlineData(RecipeOutputFormat.Csv, "")]
    public async Task OutputThatDoesNotParseFallsBackToText(RecipeOutputFormat format, string output)
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, output));
        RecipeToolViewModel viewModel = harness.Create(Recipe(output: format));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.False(viewModel.ShowGrid);
        Assert.Null(viewModel.OutputView);
        Assert.Equal(output, viewModel.OutputText);
    }

    [Fact]
    public async Task ErrorOutputIsShownApartFromTheOutput()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.FailedExitCode, 1, "out", "something went wrong\r\n"));
        RecipeToolViewModel viewModel = harness.Create(Recipe(stderr: RecipeStderrMode.Separate));

        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasErrorOutput);
        Assert.Equal("something went wrong", viewModel.ErrorOutputText);
        Assert.Equal("out", viewModel.OutputText);
    }

    [Fact]
    public async Task ANewRunClearsTheResultOfTheLastOne()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();
        harness.Runner.Script = _ => Task.FromResult(FakeRecipeRunner.Result(RecipeRunStatus.Succeeded, 0, "first"));
        RecipeToolViewModel viewModel = harness.Create(Recipe());
        await viewModel.RunCommand.ExecuteAsync(null);
        Assert.Equal("first", viewModel.OutputText);

        harness.Availability.State = ToolAvailabilityState.DisabledByUser;
        await viewModel.RunCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.OutputText);
        Assert.Equal(string.Empty, viewModel.StatusText);
        Assert.False(viewModel.HasOutput);
    }

    [Fact]
    public async Task TheProvenanceIsShownInTheHeader()
    {
        using RecipeViewModelHarness harness = await RecipeViewModelHarness.CreateAsync();

        RecipeToolViewModel viewModel = harness.Create(Recipe(author: "Acme"));

        Assert.Equal("Unverified recipe by Acme", viewModel.Provenance);
    }
}
