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
using Heimdall.App.ViewModels.CommandLibrary;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the tag filter.
/// </summary>
/// <remarks>
/// Tags were already searchable - the relevance search ranks them just after the title -
/// but asking for one meant typing it and getting back everything else that mentions the
/// word too. This narrows instead of ranking.
/// </remarks>
public sealed class CommandLibraryTagFilterTests
{
    [Fact]
    public async Task TheComboListsEveryTagOnceUnderAnAllSentinel()
    {
        var viewModel = await CreateViewModelAsync(
            Tagged("a", "Alpha", "prod", "linux"),
            Tagged("b", "Bravo", "linux"),
            Tagged("c", "Charlie"));
        using var _ = viewModel;

        // The sentinel first, then the tags, sorted and deduplicated.
        Assert.Equal(3, viewModel.TagFilterItems.Count);
        Assert.Equal(["linux", "prod"], viewModel.TagFilterItems.Skip(1));
    }

    [Fact]
    public async Task SelectingATagHidesEveryActionWithoutIt()
    {
        var viewModel = await CreateViewModelAsync(
            Tagged("a", "Alpha", "prod"),
            Tagged("b", "Bravo", "staging"),
            Tagged("c", "Charlie"));
        using var _ = viewModel;

        viewModel.TagFilterIndex = IndexOf(viewModel, "prod");

        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "a")));
        Assert.False(viewModel.ShouldShowAction(Entry(viewModel, "b")));
        Assert.False(viewModel.ShouldShowAction(Entry(viewModel, "c")));
    }

    /// <summary>
    /// Control for the test above: with the sentinel selected nothing is hidden, so the
    /// filter cannot be satisfied by a predicate that always refuses.
    /// </summary>
    [Fact]
    public async Task TheAllSentinelHidesNothing()
    {
        var viewModel = await CreateViewModelAsync(
            Tagged("a", "Alpha", "prod"),
            Tagged("c", "Charlie"));
        using var _ = viewModel;

        viewModel.TagFilterIndex = 0;

        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "a")));
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "c")));
    }

    /// <summary>
    /// A tag is something a person typed, twice, in two places.
    /// </summary>
    [Fact]
    public async Task TagsAreMatchedWithoutRegardToCaseOrSurroundingSpace()
    {
        var viewModel = await CreateViewModelAsync(
            Tagged("a", "Alpha", "Prod"),
            Tagged("b", "Bravo", "  prod  "));
        using var _ = viewModel;

        // The two spellings collapse to one entry in the combo.
        Assert.Equal(2, viewModel.TagFilterItems.Count);

        viewModel.TagFilterIndex = 1;

        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "a")));
        Assert.True(viewModel.ShouldShowAction(Entry(viewModel, "b")));
    }

    [Fact]
    public async Task ATagSelectionCountsAsAnActiveFilter()
    {
        var viewModel = await CreateViewModelAsync(Tagged("a", "Alpha", "prod"));
        using var _ = viewModel;

        Assert.False(viewModel.HasActiveFilters);

        viewModel.TagFilterIndex = 1;

        Assert.True(viewModel.HasActiveFilters);
    }

    // ── Tags in the detail panel ──────────────────────────────────

    [Fact]
    public async Task SelectingAnActionShowsItsTags()
    {
        var viewModel = await CreateViewModelAsync(Tagged("a", "Alpha", "prod", "linux"));
        using var _ = viewModel;

        viewModel.SelectedEntry = Entry(viewModel, "a");

        Assert.True(viewModel.HasSelectedTags);
        Assert.Contains("#prod", viewModel.SelectedActionTags, StringComparison.Ordinal);
        Assert.Contains("#linux", viewModel.SelectedActionTags, StringComparison.Ordinal);
        Assert.True(viewModel.HasDetailContent);
    }

    [Fact]
    public async Task AnActionWithNoTagsShowsNone()
    {
        var viewModel = await CreateViewModelAsync(Tagged("c", "Charlie"));
        using var _ = viewModel;

        viewModel.SelectedEntry = Entry(viewModel, "c");

        Assert.False(viewModel.HasSelectedTags);
        Assert.Equal(string.Empty, viewModel.SelectedActionTags);
    }

    [Fact]
    public async Task ClearingTheSelectionClearsTheTags()
    {
        var viewModel = await CreateViewModelAsync(Tagged("a", "Alpha", "prod"));
        using var _ = viewModel;
        viewModel.SelectedEntry = Entry(viewModel, "a");
        Assert.True(viewModel.HasSelectedTags);

        viewModel.SelectedEntry = null;

        Assert.False(viewModel.HasSelectedTags);
    }

    private static int IndexOf(CommandLibraryViewModel viewModel, string tag)
    {
        var index = viewModel.TagFilterItems
            .Select((text, position) => (text, position))
            .First(candidate => string.Equals(candidate.text, tag, StringComparison.Ordinal))
            .position;
        Assert.True(index > 0, "0 is the All sentinel, not a tag.");
        return index;
    }

    private static CommandLibraryActionEntry Entry(CommandLibraryViewModel viewModel, string id)
        => viewModel.AllEntries.Single(e => string.Equals(e.Source.Id, id, StringComparison.Ordinal));

    private static ActionModel Tagged(string id, string title, params string[] tags)
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction(id, title, "echo " + id);
        action.Tags = [.. tags];
        return action;
    }

    private static async Task<CommandLibraryViewModel> CreateViewModelAsync(params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
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
