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
using Heimdall.App.Tests.Fakes;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class NotesToolViewModelTests
{
    [Fact]
    public async Task ReloadAsync_OnUiThread_DoesNotPostToDispatcher()
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: true);
        var vm = await CreateViewModelAsync(CreateStorage(Seed("alpha.md", "# Alpha\nalpha")), dispatcher);

        await vm.ReloadAsync();

        Assert.Equal(0, dispatcher.InvokeAsyncCalls);
    }

    [Fact]
    public async Task ReloadAsync_OffUiThread_PostsToDispatcher()
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: false);
        var vm = await CreateViewModelAsync(CreateStorage(Seed("alpha.md", "# Alpha\nalpha")), dispatcher);

        await vm.ReloadAsync();

        Assert.True(dispatcher.InvokeAsyncCalls > 0);
    }

    [Fact]
    public async Task ReloadAsync_PopulatesNotesAndAvailableTags()
    {
        var vm = await CreateViewModelAsync(
            Seed("ops/alpha.md", "# Alpha\nAlpha summary #ops", new DateTime(2026, 4, 20, 8, 0, 0, DateTimeKind.Utc)),
            Seed("dev/beta.md", "# Beta\nBeta summary #dev", new DateTime(2026, 4, 21, 8, 0, 0, DateTimeKind.Utc)),
            Seed("misc/gamma.md", "# Gamma\nGamma summary #ops #infra", new DateTime(2026, 4, 22, 8, 0, 0, DateTimeKind.Utc)));

        await vm.ReloadAsync();

        Assert.Equal(3, FlattenNotes(vm.Notes).Count);
        Assert.Equal(["dev", "infra", "ops"], vm.AvailableTags);
        Assert.Equal(3, vm.AllNotes.Count);
        Assert.Contains("3", vm.ListFooterText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchText_FiltersNotesUsingExistingStorageSemantics()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nshared summary #ops"),
            Seed("beta.md", "# Beta\nnetwork issue #infra"),
            Seed("gamma.md", "# Gamma\nother summary #ops"));

        await vm.ReloadAsync();
        vm.SearchText = "network";
        await WaitUntilAsync(() => FlattenTitles(vm.Notes).SequenceEqual(["Beta"]));

        Assert.Equal(["Beta"], FlattenTitles(vm.Notes));
    }

    [Fact]
    public async Task SortOrder_ReordersNotes()
    {
        var vm = await CreateViewModelAsync(
            Seed("zeta.md", "# Zeta\nzeta"),
            Seed("alpha.md", "# Alpha\nalpha"),
            Seed("beta.md", "# Beta\nbeta"));

        await vm.ReloadAsync();
        vm.SortOrder = NoteSortOrder.NameAscending;
        await WaitUntilAsync(() => FlattenTitles(vm.Notes).SequenceEqual(["Alpha", "Beta", "Zeta"]));

        Assert.Equal(["Alpha", "Beta", "Zeta"], FlattenTitles(vm.Notes));
    }

    [Fact]
    public async Task SelectedTag_FiltersNotesToMatchingTag()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"),
            Seed("gamma.md", "# Gamma\ngamma #ops"));

        await vm.ReloadAsync();
        vm.SelectedTag = "ops";
        await WaitUntilAsync(() => FlattenTitles(vm.Notes).SequenceEqual(["Alpha", "Gamma"]));

        Assert.Equal(["Alpha", "Gamma"], FlattenTitles(vm.Notes));
    }

    [Fact]
    public async Task OpenNoteCommand_SetsSelectionMarkdownAndHistory()
    {
        var storage = CreateStorage(
            Seed("alpha.md", "# Alpha\nalpha"),
            Seed("beta.md", "# Beta\nbeta"));
        var vm = await CreateViewModelAsync(storage);
        var paths = storage.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        await vm.ReloadAsync();
        await vm.OpenNoteCommand.ExecuteAsync(paths[0]);
        await vm.OpenNoteCommand.ExecuteAsync(paths[1]);

        Assert.Equal(paths[1], vm.CurrentNotePath);
        Assert.Equal("# Beta\nbeta", vm.CurrentMarkdown);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
        Assert.Equal("Beta", vm.SelectedNoteTitle);
    }

    [Fact]
    public async Task SaveCommand_NoOpsWhenNotDirty()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        vm.CurrentMarkdown = "# Alpha\nmodified";
        vm.IsDirty = false;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("# Alpha\nalpha", await storage.LoadNoteAsync(path));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SaveCommand_PersistsContentAndClearsDirty()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        vm.CurrentMarkdown = "# Alpha\nupdated";
        vm.IsDirty = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("# Alpha\nupdated", await storage.LoadNoteAsync(path));
        Assert.False(vm.IsDirty);
        Assert.False(vm.StatusIsError);
        Assert.Contains("saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryApplyEditorMarkdown_WhenTheEditorReportsWhatWasLoaded_KeepsTheNoteClean()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        bool hasEdits = vm.TryApplyEditorMarkdown("# Alpha\nalpha");

        Assert.False(hasEdits);
        Assert.False(vm.IsDirty);
        Assert.Equal("# Alpha\nalpha", vm.CurrentMarkdown);
    }

    [Fact]
    public async Task TryApplyEditorMarkdown_WhenTheMarkdownDiffers_MarksTheNoteDirtyAndKeepsIt()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        bool hasEdits = vm.TryApplyEditorMarkdown("# Alpha\nalpha edited");

        Assert.True(hasEdits);
        Assert.True(vm.IsDirty);
        Assert.Equal("# Alpha\nalpha edited", vm.CurrentMarkdown);
    }

    [Fact]
    public async Task TryApplyEditorMarkdown_WhenAlreadyDirty_StaysDirtyEvenIfTheTextReturnsToTheLoadedOne()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        vm.TryApplyEditorMarkdown("# Alpha\nalpha edited");
        bool hasEdits = vm.TryApplyEditorMarkdown("# Alpha\nalpha");

        Assert.True(hasEdits);
        Assert.True(vm.IsDirty);
        Assert.Equal("# Alpha\nalpha", vm.CurrentMarkdown);
    }

    [Fact]
    public async Task SaveCommand_WhenStorageFails_SurfacesError()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        storage.SaveFailure = new InvalidOperationException("boom");
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        vm.CurrentMarkdown = "# Alpha\nupdated";
        vm.IsDirty = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsDirty);
        Assert.True(vm.StatusIsError);
        Assert.Contains("boom", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TrySaveSynchronously_WhenTheWriteLockTimesOut_KeepsTheNoteDirtyAndRefuses()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        vm.CurrentMarkdown = "# Alpha\nlast edits";
        vm.IsDirty = true;
        storage.SyncSaveLockTimesOut = true;

        bool saved = vm.TrySaveSynchronously();

        // Reported saved, the tab closed over edits that were never written.
        Assert.False(saved);
        Assert.True(vm.IsDirty);
        Assert.Equal("# Alpha\nalpha", await storage.LoadNoteAsync(path));
    }

    [Fact]
    public async Task NewNoteFromTemplateCommand_CreatesSelectsAndReloads()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);

        await vm.ReloadAsync();
        await vm.NewNoteFromTemplateCommand.ExecuteAsync("Daily");

        Assert.Equal(2, FlattenNotes(vm.Notes).Count);
        Assert.NotNull(vm.CurrentNotePath);
        Assert.EndsWith("daily-note.md", vm.CurrentNotePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(vm.CurrentNotePath, storage.Paths.Single(path => path.EndsWith("daily-note.md", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DeleteCurrentNoteCommand_RemovesNoteAndClearsSelection()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();

        await vm.ReloadAsync(path);
        await vm.DeleteCurrentNoteCommand.ExecuteAsync(null);

        Assert.Empty(storage.Paths);
        Assert.Null(vm.CurrentNotePath);
        Assert.False(vm.HasSelection);
        Assert.Empty(FlattenNotes(vm.Notes));
    }

    [Fact]
    public async Task RenameCurrentNoteCommand_UpdatesStorageAndSelection()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var originalPath = storage.Paths.Single();

        await vm.ReloadAsync(originalPath);
        await vm.RenameCurrentNoteCommand.ExecuteAsync("Renamed Note");

        var renamedPath = storage.Paths.Single();
        Assert.NotEqual(originalPath, renamedPath);
        Assert.EndsWith("renamed-note.md", renamedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(renamedPath, vm.CurrentNotePath);
        Assert.Equal("Renamed Note", vm.SelectedNoteTitle);
        Assert.Contains("# Renamed Note", await storage.LoadNoteAsync(renamedPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateCurrentNoteCommand_CreatesCopyAndSelectsCopy()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha"));
        var vm = await CreateViewModelAsync(storage);
        var originalPath = storage.Paths.Single();

        await vm.ReloadAsync(originalPath);
        await vm.DuplicateCurrentNoteCommand.ExecuteAsync(null);

        Assert.Equal(2, storage.Paths.Count);
        Assert.NotEqual(originalPath, vm.CurrentNotePath);
        Assert.Contains("copy", vm.CurrentNotePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, FlattenNotes(vm.Notes).Count);
    }

    [Fact]
    public async Task GoBackAndGoForward_UpdateHistoryFlagsAndSelection()
    {
        var storage = CreateStorage(
            Seed("alpha.md", "# Alpha\nalpha"),
            Seed("beta.md", "# Beta\nbeta"),
            Seed("gamma.md", "# Gamma\ngamma"));
        var vm = await CreateViewModelAsync(storage);
        var paths = storage.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        await vm.ReloadAsync();
        await vm.OpenNoteCommand.ExecuteAsync(paths[0]);
        await vm.OpenNoteCommand.ExecuteAsync(paths[1]);
        await vm.OpenNoteCommand.ExecuteAsync(paths[2]);

        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);

        await vm.GoBackCommand.ExecuteAsync(null);

        Assert.Equal(paths[1], vm.CurrentNotePath);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanGoForward);

        await vm.GoForwardCommand.ExecuteAsync(null);

        Assert.Equal(paths[2], vm.CurrentNotePath);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
    }

    [Fact]
    public async Task ExportHtmlCommand_WhenNoteLoaded_RaisesExportHtmlRequestedWithHtmlPayload()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha body"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();
        var payloads = new List<string>();
        vm.ExportHtmlRequested += (_, payload) => payloads.Add(payload);

        await vm.ReloadAsync(path);
        await vm.ExportHtmlCommand.ExecuteAsync(null);

        var payload = Assert.Single(payloads);
        Assert.StartsWith("<!DOCTYPE html>", payload, StringComparison.Ordinal);
        Assert.Contains("Alpha", payload, StringComparison.Ordinal);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task CopyConfluenceCommand_WhenNoteLoaded_RaisesCopyConfluenceRequestedWithStoragePayload()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha body"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();
        var payloads = new List<string>();
        vm.CopyConfluenceRequested += (_, payload) => payloads.Add(payload);

        await vm.ReloadAsync(path);
        await vm.CopyConfluenceCommand.ExecuteAsync(null);

        var payload = Assert.Single(payloads);
        Assert.Contains("xmlns:ac=", payload, StringComparison.Ordinal);
        Assert.Contains("<div", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportConfluenceCommand_WhenNoteLoaded_RaisesExportConfluenceRequestedWithStoragePayload()
    {
        var storage = CreateStorage(Seed("alpha.md", "# Alpha\nalpha body"));
        var vm = await CreateViewModelAsync(storage);
        var path = storage.Paths.Single();
        var payloads = new List<string>();
        vm.ExportConfluenceRequested += (_, payload) => payloads.Add(payload);

        await vm.ReloadAsync(path);
        await vm.ExportConfluenceCommand.ExecuteAsync(null);

        var payload = Assert.Single(payloads);
        Assert.Contains("xmlns:ac=", payload, StringComparison.Ordinal);
        Assert.Contains("<div", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportHtmlCommand_WhenNoNoteLoaded_DoesNotRaiseAndSetsStatus()
    {
        var vm = await CreateViewModelAsync(Seed("alpha.md", "# Alpha\nalpha body"));
        var payloads = new List<string>();
        vm.ExportHtmlRequested += (_, payload) => payloads.Add(payload);

        await vm.ExportHtmlCommand.ExecuteAsync(null);

        Assert.Empty(payloads);
        Assert.True(vm.StatusIsError);
        Assert.Contains("No note selected", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectTagCommand_UpdatesSelectedTag()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));

        await vm.ReloadAsync();
        vm.SelectTagCommand.Execute("ops");
        await WaitUntilAsync(() => vm.SelectedTag == "ops");

        Assert.Equal("ops", vm.SelectedTag);
        Assert.Contains(vm.TagFilters, chip => chip.Value == "ops" && chip.IsSelected);
    }

    [Fact]
    public async Task SelectTagCommand_PassingNull_ClearsSelectedTag()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));

        await vm.ReloadAsync();
        vm.SelectTagCommand.Execute("ops");
        await WaitUntilAsync(() => vm.SelectedTag == "ops");

        vm.SelectTagCommand.Execute(null);
        await WaitUntilAsync(() => vm.SelectedTag is null);

        Assert.Null(vm.SelectedTag);
        Assert.Contains(vm.TagFilters, chip => chip.Value is null && chip.IsSelected);
    }

    // The chip list is a single-select list bound to SelectedTagFilter, so that UI Automation
    // reports the chosen chip as selected instead of the colour alone saying so.

    [Fact]
    public async Task PickingAChip_FiltersByItsTag_AndKeepsItSelected()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));
        await vm.ReloadAsync();

        vm.SelectedTagFilter = vm.TagFilters.Single(chip => chip.Value == "ops");
        await WaitUntilAsync(() => vm.SelectedTag == "ops");

        Assert.Equal("ops", vm.SelectedTag);
        Assert.Equal("ops", vm.SelectedTagFilter?.Value);
        Assert.True(vm.SelectedTagFilter?.IsSelected);
        Assert.Contains(vm.TagFilters, chip => ReferenceEquals(chip, vm.SelectedTagFilter));
    }

    [Fact]
    public async Task ChoosingATagByCommand_SelectsItsChipInTheList()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));
        await vm.ReloadAsync();

        vm.SelectTagCommand.Execute("infra");
        await WaitUntilAsync(() => vm.SelectedTag == "infra");

        Assert.Equal("infra", vm.SelectedTagFilter?.Value);
    }

    [Fact]
    public async Task TogglingTheChosenChipOff_ClearsTheFilter_AndSelectsTheAllChip()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));
        await vm.ReloadAsync();
        vm.SelectTagCommand.Execute("ops");
        await WaitUntilAsync(() => vm.SelectedTag == "ops");

        // What a single-select list reports when the chosen item is Ctrl+clicked.
        vm.SelectedTagFilter = null;
        await WaitUntilAsync(() => vm.SelectedTag is null);

        Assert.Null(vm.SelectedTag);
        Assert.NotNull(vm.SelectedTagFilter);
        Assert.Null(vm.SelectedTagFilter!.Value);
        Assert.True(vm.SelectedTagFilter.IsSelected);
    }

    [Fact]
    public async Task TogglingTheAllChipOff_LeavesTheAllChipSelected()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"));
        await vm.ReloadAsync();
        Assert.NotNull(vm.SelectedTagFilter);

        vm.SelectedTagFilter = null;

        Assert.NotNull(vm.SelectedTagFilter);
        Assert.Null(vm.SelectedTagFilter!.Value);
    }

    [Fact]
    public async Task RedrawingTheChips_IsNotTheUserClearingTheFilter()
    {
        var vm = await CreateViewModelAsync(
            Seed("alpha.md", "# Alpha\nalpha #ops"),
            Seed("beta.md", "# Beta\nbeta #infra"));
        await vm.ReloadAsync();

        // A bound list reports a null selection when its items are cleared, and a pick when they
        // are refilled. Neither is the user choosing a chip.
        vm.TagFilters.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                vm.SelectedTagFilter = null;
            }
        };

        vm.SelectTagCommand.Execute("ops");
        await WaitUntilAsync(() => vm.SelectedTag == "ops");

        Assert.Equal("ops", vm.SelectedTag);
        Assert.Equal("ops", vm.SelectedTagFilter?.Value);
    }

    private static async Task<NotesToolViewModel> CreateViewModelAsync(params FakeNoteSeed[] notes)
        => await CreateViewModelAsync(CreateStorage(notes), new FakeUiDispatcher());

    private static async Task<NotesToolViewModel> CreateViewModelAsync(FakeNotesStorage storage)
        => await CreateViewModelAsync(storage, new FakeUiDispatcher());

    private static async Task<NotesToolViewModel> CreateViewModelAsync(
        FakeNotesStorage storage,
        FakeUiDispatcher dispatcher)
    {
        var vm = new NotesToolViewModel(storage, await CreateLocalizerAsync("en"), dispatcher);
        await vm.InitializeAsync();
        return vm;
    }

    private static FakeNotesStorage CreateStorage(params FakeNoteSeed[] notes)
        => new(Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"), notes);

    private static FakeNoteSeed Seed(string relativePath, string content, DateTime? lastModifiedUtc = null)
        => new(relativePath, content, lastModifiedUtc);

    private static IReadOnlyList<NoteTreeNode> FlattenNotes(IEnumerable<NoteTreeNode> nodes)
    {
        var result = new List<NoteTreeNode>();
        foreach (var node in nodes)
        {
            if (!node.IsFolder)
            {
                result.Add(node);
            }

            if (node.Children.Count > 0)
            {
                result.AddRange(FlattenNotes(node.Children));
            }
        }

        return result;
    }

    private static IReadOnlyList<string> FlattenTitles(IEnumerable<NoteTreeNode> nodes)
        => FlattenNotes(nodes).Select(node => node.DisplayTitle).ToList();

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var timeoutAt = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > timeoutAt)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(25);
        }
    }
}
