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

using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the runbook editor: what its gestures do to the runbook it saves, and what the library
/// stores when the operator saves or deletes one.
/// </summary>
public sealed class RunbookEditorTests
{
    private static readonly ActionModel Status = CommandLibraryTestHelpers.CreateLinuxAction(
        "status", "Service status", "systemctl status {unit}",
        CommandLibraryTestHelpers.RequiredParameter("unit", "Unit"));

    private static readonly ActionModel Logs = CommandLibraryTestHelpers.CreateLinuxAction(
        "logs", "Service logs", "journalctl -u {unit}",
        CommandLibraryTestHelpers.RequiredParameter("unit", "Unit"));

    private static readonly ActionModel Restart = CommandLibraryTestHelpers.CreateLinuxAction(
        "restart", "Restart service", "systemctl restart {unit}",
        CommandLibraryTestHelpers.RequiredParameter("unit", "Unit"));

    [Fact]
    public void MoveUp_PutsTheStepAboveItsNeighbour_InTheSavedRunbook()
    {
        var editor = RunbookDialogViewModel.FromRunbook(ThreeSteps(), Localize, Find);

        editor.MoveUp(editor.Steps[2]);

        Assert.Equal(["status", "restart", "logs"], editor.ToRunbook().Commands.Select(command => command.ActionId));
    }

    [Fact]
    public void RemoveStep_DropsTheStepFromTheSavedRunbook()
    {
        var editor = RunbookDialogViewModel.FromRunbook(ThreeSteps(), Localize, Find);

        editor.RemoveStep(editor.Steps[1]);

        Assert.Equal(["status", "restart"], editor.ToRunbook().Commands.Select(command => command.ActionId));
    }

    [Fact]
    public async Task AddAction_KeepsAVariableReferenceAsTheParameterValue()
    {
        var editor = RunbookDialogViewModel.ForNew(Localize, Find);
        editor.PickActionAsync = () => Task.FromResult<CommandLibraryPickerResult?>(new CommandLibraryPickerResult(
            "status",
            "Service status",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "{{unit}}" }));

        await editor.AddActionAsync();

        var step = Assert.Single(editor.ToRunbook().Commands);
        Assert.Equal("status", step.ActionId);
        Assert.Equal("{{unit}}", step.Parameters["unit"]);
    }

    [Theory]
    [InlineData("my host")]
    [InlineData("1st")]
    public void Validate_RefusesAVariableNameThatIsNotAName(string name)
    {
        var editor = RunbookDialogViewModel.ForNew(Localize, Find);
        editor.Name = "Restart a unit";
        editor.Variables.Add(new RunbookVariableEntry { Name = name, Value = "x" });

        Assert.False(editor.Validate());
        Assert.Contains(name, editor.ValidationError);
    }

    // Positive control for the theory above: a proper name passes, so the refusal is about the
    // name and not about every variable.
    [Fact]
    public void Validate_AcceptsAVariableName()
    {
        var editor = RunbookDialogViewModel.ForNew(Localize, Find);
        editor.Name = "Restart a unit";
        editor.Variables.Add(new RunbookVariableEntry { Name = "unit_name", Value = "x" });

        Assert.True(editor.Validate());
        Assert.Null(editor.ValidationError);
    }

    [Fact]
    public async Task NewRunbook_StoresTheStepsWithTheCommandGeneratedFromTheDefaults()
    {
        var batches = new FakeBatchService();
        using var library = await RunbookTestHelpers.CreateLibraryAsync(
            batches, new CollectingHistoryService(), new SilentDialogService(), "srv-01", "SSH", Status);
        library.ShowRunbookDialogAsync = editor =>
        {
            editor.Name = "Check a unit";
            editor.Variables.Add(new RunbookVariableEntry { Name = "unit", Value = "nginx" });
            editor.Steps.Add(RunbookStepEditorItem.ForAction(
                "status", "Service status", library.FindRunbookAction("status"),
                new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "{{unit}}" }));
            return Task.FromResult(true);
        };

        await library.NewRunbookAsync();

        var stored = Assert.Single(batches.Stored);
        Assert.Equal("Check a unit", stored.Name);
        Assert.Equal("nginx", stored.Variables["unit"]);
        var step = Assert.Single(stored.Commands);
        Assert.Equal("{{unit}}", step.Parameters["unit"]);
        Assert.Equal("systemctl status 'nginx'", step.Command);
        Assert.Equal(Platform.Linux, step.Platform);
        Assert.Equal(library.SelectedRunbook?.Source.Id, stored.Id);
    }

    [Fact]
    public async Task DeleteRunbook_Confirmed_RemovesItFromTheStore()
    {
        var batches = new FakeBatchService();
        batches.Seed(new CommandBatch { Id = "rb-1", Name = "Restart a unit" });
        using var library = await RunbookTestHelpers.CreateLibraryAsync(
            batches, new CollectingHistoryService(), new SilentDialogService { ConfirmResult = true }, "srv-01", "SSH", Status);
        await library.ToggleRunbooksAsync();
        library.SelectedRunbook = library.Runbooks.Single();

        await library.DeleteRunbookAsync();

        Assert.Empty(batches.Stored);
        Assert.Empty(library.Runbooks);
        Assert.True(library.IsRunbooksEmpty);
    }

    // Positive control: a refused confirmation keeps the runbook, so the test above is about the
    // confirmed gesture and not about a store that empties itself.
    [Fact]
    public async Task DeleteRunbook_Refused_KeepsIt()
    {
        var batches = new FakeBatchService();
        batches.Seed(new CommandBatch { Id = "rb-1", Name = "Restart a unit" });
        using var library = await RunbookTestHelpers.CreateLibraryAsync(
            batches, new CollectingHistoryService(), new SilentDialogService { ConfirmResult = false }, "srv-01", "SSH", Status);
        await library.ToggleRunbooksAsync();
        library.SelectedRunbook = library.Runbooks.Single();

        await library.DeleteRunbookAsync();

        Assert.Single(batches.Stored);
    }

    [Fact]
    public async Task ToggleRunbooks_OpensThePanel_ClosesTheOthers_AndListsTheStore()
    {
        var batches = new FakeBatchService();
        batches.Seed(ThreeSteps());
        using var library = await RunbookTestHelpers.CreateLibraryAsync(
            batches, new CollectingHistoryService(), new SilentDialogService(), "srv-01", "SSH", Status, Logs, Restart);
        library.SelectAction(library.AllEntries[0]);

        await library.ToggleRunbooksAsync();

        Assert.True(library.IsRunbooksVisible);
        Assert.False(library.IsGeneratorVisible);
        Assert.False(library.IsHistoryVisible);
        var row = Assert.Single(library.Runbooks);
        Assert.Equal("Restart a unit", row.Name);
        Assert.Equal("3 steps", row.StepCountText);
    }

    private static CommandBatch ThreeSteps() => new()
    {
        Id = "rb-restart",
        Name = "Restart a unit",
        Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["unit"] = "nginx" },
        Commands =
        [
            WithOrder(RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}")), 0),
            WithOrder(RunbookTestHelpers.ActionStep("logs", ("unit", "{{unit}}")), 1),
            WithOrder(RunbookTestHelpers.ActionStep("restart", ("unit", "{{unit}}")), 2)
        ]
    };

    private static BatchCommandItem WithOrder(BatchCommandItem item, int order)
    {
        item.Order = order;
        return item;
    }

    // The key with a slot, so a formatted message still says which value it is about.
    private static string Localize(string key) => key + " {0}";

    private static ActionModel? Find(string id) => new[] { Status, Logs, Restart }.FirstOrDefault(action => action.Id == id);
}
