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

/// <summary>
/// Moving the open note to another folder keeps its unsaved edits.
/// </summary>
public sealed class NotesToolMoveWithEditsTests
{
    private const string EditedText = "# Alpha\nedited";

    [Fact]
    public async Task MoveOpenDirtyNote_EndsWithTheEditedTextAtTheNewPathAndNothingAtTheOldOne()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string oldPath) = await OpenDirtyNoteAsync();
        string target = Path.Combine(storage.NotesRootPath, "archive");

        string? newPath = await vm.MoveNoteToFolderAsync(oldPath, target);

        Assert.NotNull(newPath);
        Assert.DoesNotContain(oldPath, storage.Paths);
        Assert.Equal(EditedText, await storage.LoadNoteAsync(newPath!));
        Assert.Equal(newPath, vm.CurrentNotePath);
        Assert.Equal(EditedText, vm.CurrentMarkdown);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task MoveOpenDirtyNote_WhenTheSaveFailsAndTheDiscardIsDeclined_MovesNothing()
    {
        var dialog = new SessionCoordinatorPreMountTests.FakeDialogService { ConfirmResult = false };
        (FakeNotesStorage storage, NotesToolViewModel vm, string oldPath) = await OpenDirtyNoteAsync(dialog);
        storage.SaveFailure = new InvalidOperationException("disk full");
        string target = Path.Combine(storage.NotesRootPath, "archive");

        string? newPath = await vm.MoveNoteToFolderAsync(oldPath, target);

        Assert.Null(newPath);
        Assert.Contains(oldPath, storage.Paths);
        Assert.Single(storage.Paths);
        Assert.Equal(oldPath, vm.CurrentNotePath);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task MoveAnotherNote_DoesNotTouchTheOpenNotesEdits()
    {
        var storage = new FakeNotesStorage(
            Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"),
            new FakeNoteSeed("alpha.md", "# Alpha\nalpha"),
            new FakeNoteSeed("beta.md", "# Beta\nbeta"));
        NotesToolViewModel vm = await CreateViewModelAsync(storage);
        string[] paths = storage.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        await vm.ReloadAsync(paths[0]);
        vm.CurrentMarkdown = EditedText;
        vm.IsDirty = true;

        string? newPath = await vm.MoveNoteToFolderAsync(paths[1], Path.Combine(storage.NotesRootPath, "archive"));

        Assert.NotNull(newPath);
        Assert.Equal(paths[0], vm.CurrentNotePath);
        Assert.True(vm.IsDirty);
        Assert.Equal(EditedText, vm.CurrentMarkdown);
    }

    [Fact]
    public async Task MoveOpenNote_RewritesTheHistoryEntriesThatHeldTheOldPath()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha, string beta) = await OpenAlphaWithHistoryAsync();

        string? newPath = await vm.MoveNoteToFolderAsync(alpha, Path.Combine(storage.NotesRootPath, "archive"));

        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(beta, vm.CurrentNotePath);
        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(newPath, vm.CurrentNotePath);
        Assert.Equal("# Alpha\nalpha", vm.CurrentMarkdown);
        Assert.DoesNotContain(alpha, storage.Paths);
    }

    [Fact]
    public async Task RenameOpenNote_RewritesTheHistoryEntriesThatHeldTheOldPath()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha, string beta) = await OpenAlphaWithHistoryAsync();

        await vm.RenameCurrentNoteCommand.ExecuteAsync("renamed");
        string renamed = vm.CurrentNotePath!;

        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(beta, vm.CurrentNotePath);
        await vm.GoBackCommand.ExecuteAsync(null);
        Assert.Equal(renamed, vm.CurrentNotePath);
        Assert.Contains("alpha", vm.CurrentMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain(alpha, storage.Paths);
    }

    [Fact]
    public async Task MoveOpenDirtyNote_WhenTheSaveFailsAndTheDiscardIsConfirmed_ShowsTheFileContent()
    {
        var dialog = new SessionCoordinatorPreMountTests.FakeDialogService { ConfirmResult = true };
        (FakeNotesStorage storage, NotesToolViewModel vm, string oldPath) = await OpenDirtyNoteAsync(dialog);
        storage.SaveFailure = new InvalidOperationException("disk full");

        string? newPath = await vm.MoveNoteToFolderAsync(oldPath, Path.Combine(storage.NotesRootPath, "archive"));

        Assert.NotNull(newPath);
        Assert.Equal("# Alpha\nalpha", await storage.LoadNoteAsync(newPath!));
        Assert.Equal("# Alpha\nalpha", vm.CurrentMarkdown);
        Assert.False(vm.IsDirty);
    }

    private static async Task<(FakeNotesStorage Storage, NotesToolViewModel Vm, string Alpha, string Beta)>
        OpenAlphaWithHistoryAsync()
    {
        var storage = new FakeNotesStorage(
            Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"),
            new FakeNoteSeed("alpha.md", "# Alpha\nalpha"),
            new FakeNoteSeed("beta.md", "# Beta\nbeta"));
        NotesToolViewModel vm = await CreateViewModelAsync(storage);
        string[] paths = storage.Paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        await vm.ReloadAsync(paths[0]);
        await vm.OpenNoteCommand.ExecuteAsync(paths[1]);
        await vm.OpenNoteCommand.ExecuteAsync(paths[0]);
        return (storage, vm, paths[0], paths[1]);
    }

    private static async Task<(FakeNotesStorage Storage, NotesToolViewModel Vm, string Path)> OpenDirtyNoteAsync(
        IDialogService? dialogService = null)
    {
        var storage = new FakeNotesStorage(
            Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"),
            new FakeNoteSeed("alpha.md", "# Alpha\nalpha"));
        NotesToolViewModel vm = await CreateViewModelAsync(storage, dialogService);
        string path = storage.Paths.Single();
        await vm.ReloadAsync(path);
        vm.CurrentMarkdown = EditedText;
        vm.IsDirty = true;
        return (storage, vm, path);
    }

    private static async Task<NotesToolViewModel> CreateViewModelAsync(
        FakeNotesStorage storage,
        IDialogService? dialogService = null)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        var vm = new NotesToolViewModel(storage, localizer, new FakeUiDispatcher(), dialogService);
        await vm.InitializeAsync();
        return vm;
    }
}
