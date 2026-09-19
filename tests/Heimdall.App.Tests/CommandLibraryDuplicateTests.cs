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
using Heimdall.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins copying an action into the user's own library.
/// </summary>
/// <remarks>
/// The actions the library ships cannot be edited, so before this the only way to get a
/// variant of one was to retype it. The whole value of the feature rests on the copy being
/// a genuinely new action: if it carried any identifier of its source, saving it would
/// update that source instead, which for a shipped action means silently editing something
/// that is meant to be read-only.
/// </remarks>
public sealed class CommandLibraryDuplicateTests
{
    [Fact]
    public void ACopyCarriesTheContentButNoneOfTheIdentity()
    {
        var source = SeededAction();

        var copy = CommandActionDialogViewModel.AsCopyOf(source, "Tail a log (copy)").ToAction();

        Assert.NotEqual(source.Id, copy.Id);
        Assert.NotEqual(source.PublicId, copy.PublicId);
        Assert.NotEqual(
            source.LinuxCommandTemplate!.Id,
            copy.LinuxCommandTemplate!.Id);
        Assert.NotEqual(
            source.LinuxCommandTemplate.PublicId,
            copy.LinuxCommandTemplate.PublicId);

        Assert.Equal("Tail a log (copy)", copy.Title);
        Assert.Equal(source.Category, copy.Category);
        Assert.Equal(
            source.LinuxCommandTemplate.CommandPattern,
            copy.LinuxCommandTemplate.CommandPattern);
    }

    /// <summary>
    /// A copy belongs to the user, whatever it was copied from.
    /// </summary>
    [Fact]
    public void ACopyOfAShippedActionIsUserCreated()
    {
        var shipped = SeededAction();
        Assert.False(shipped.IsUserCreated);

        var copy = CommandActionDialogViewModel.AsCopyOf(shipped, "A copy").ToAction();

        Assert.True(copy.IsUserCreated);
    }

    /// <summary>
    /// The dialog must open as an add, not as an edit: the two differ in what the buttons
    /// say and in whether closing it warns about losing work.
    /// </summary>
    [Fact]
    public void ACopyOpensAsANewActionRatherThanAnEdit()
    {
        var vm = CommandActionDialogViewModel.AsCopyOf(SeededAction(), "A copy");

        Assert.False(vm.IsEditMode);
        Assert.True(vm.IsDirty);
    }

    /// <summary>
    /// The end the user sees: the library gains a row and the original is untouched.
    /// </summary>
    [Fact]
    public async Task DuplicatingAddsANewActionAndLeavesTheOriginalAlone()
    {
        var shipped = SeededAction();
        var actions = new FakeActionService([shipped]);
        var viewModel = await CreateViewModelAsync(actions);
        using var _ = viewModel;

        CommandActionDialogViewModel? shown = null;
        viewModel.ShowActionDialogAsync = vm => { shown = vm; return Task.FromResult(true); };
        viewModel.SelectedEntry = viewModel.AllEntries.Single();

        await viewModel.DuplicateSelectedAsync();

        Assert.NotNull(shown);
        var stored = (await actions.GetAllActionsAsync()).ToList();
        Assert.Equal(2, stored.Count);

        var original = stored.Single(a => string.Equals(a.Id, shipped.Id, StringComparison.Ordinal));
        Assert.Equal("Tail a log", original.Title);
        Assert.False(original.IsUserCreated);

        var copy = stored.Single(a => !string.Equals(a.Id, shipped.Id, StringComparison.Ordinal));
        Assert.True(copy.IsUserCreated);
    }

    /// <summary>
    /// Unlike Edit, this is offered for an action the user cannot edit. That is the point.
    /// </summary>
    [Fact]
    public async Task DuplicateIsOfferedForAnActionThatCannotBeEdited()
    {
        var viewModel = await CreateViewModelAsync(new FakeActionService([SeededAction()]));
        using var _ = viewModel;

        viewModel.SelectedEntry = viewModel.AllEntries.Single();

        Assert.False(viewModel.CanEditSelected);
        Assert.True(viewModel.CanDuplicateSelected);
        Assert.True(viewModel.DuplicateSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task DuplicateIsRefusedWithNoSelectionOrWhileBusy()
    {
        var viewModel = await CreateViewModelAsync(new FakeActionService([SeededAction()]));
        using var _ = viewModel;

        Assert.Null(viewModel.SelectedEntry);
        Assert.False(viewModel.DuplicateSelectedCommand.CanExecute(null));

        viewModel.SelectedEntry = viewModel.AllEntries.Single();
        Assert.True(viewModel.DuplicateSelectedCommand.CanExecute(null));

        viewModel.IsBusy = true;
        Assert.False(viewModel.DuplicateSelectedCommand.CanExecute(null));
    }

    /// <summary>An action as the library ships it: content, and not the user's.</summary>
    private static ActionModel SeededAction()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "seed-tail", "Tail a log", "tail -f {path}",
            CommandLibraryTestHelpers.RequiredParameter("path", "Path"));
        action.PublicId = Guid.NewGuid();
        action.IsUserCreated = false;
        action.LinuxCommandTemplate!.PublicId = Guid.NewGuid();
        return action;
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(FakeActionService actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => actions);
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService(),
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        return viewModel;
    }
}
