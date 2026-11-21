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
/// Edits made while a save is in flight are never marked clean without having been written, and
/// a note switch, rename, move or delete never loses or resurrects them.
/// </summary>
public sealed class NotesToolSaveRaceTests
{
    private const string Original = "# Alpha\nalpha";
    private const string EditA = "# Alpha\nA";
    private const string EditAb = "# Alpha\nAB";
    private const int ExpectedPassCap = 3;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShortSyncWait = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task SaveWithEditDuringTheWrite_WritesTheLaterEditToo_AndEndsClean()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.TryApplyEditorMarkdown(EditAb));
        Assert.True(vm.IsDirty);
        gate.SetResult();
        await save.WaitAsync(WaitLimit);

        Assert.Equal([EditA, EditAb], storage.SavedContents);
        Assert.Equal(EditAb, await storage.LoadNoteAsync(alpha));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SaveWithoutEditDuringTheWrite_EndsCleanAfterOneWrite()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, _) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        gate.SetResult();
        await save.WaitAsync(WaitLimit);

        Assert.Equal([EditA], storage.SavedContents);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SaveWhenEditsKeepArriving_StopsAtTheCapAndStaysDirty()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, _) = await OpenAlphaDirtyAsync();
        int edits = 0;
        storage.OnSaveStarted = _ => vm.TryApplyEditorMarkdown($"# Alpha\nedit-{++edits}");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(ExpectedPassCap, storage.SavedContents.Count);
        Assert.True(vm.IsDirty);
        Assert.Equal($"# Alpha\nedit-{ExpectedPassCap}", vm.CurrentMarkdown);
    }

    [Fact]
    public async Task OpenOtherNote_WhileASaveWithAPendingEditIsInFlight_KeepsTheEditInTheFirstNote()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        string beta = BetaPath(storage);
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);
        Task open = vm.OpenNoteCommand.ExecuteAsync(beta);
        gate.SetResult();
        await Task.WhenAll(save, open).WaitAsync(WaitLimit);

        Assert.Equal(EditAb, await storage.LoadNoteAsync(alpha));
        Assert.Equal(beta, vm.CurrentNotePath);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task RenameOpenNote_WhileASaveWithAPendingEditIsInFlight_CarriesTheEdit()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, _) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);
        Task rename = vm.RenameCurrentNoteCommand.ExecuteAsync("renamed");
        gate.SetResult();
        await Task.WhenAll(save, rename).WaitAsync(WaitLimit);

        Assert.Equal(2, storage.Paths.Count);
        Assert.Equal("# renamed\nAB", await storage.LoadNoteAsync(vm.CurrentNotePath!));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task MoveOpenNote_WhileASaveWithAPendingEditIsInFlight_CarriesTheEdit()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);
        Task<string?> move = vm.MoveNoteToFolderAsync(alpha, Path.Combine(storage.NotesRootPath, "archive"));
        gate.SetResult();
        await Task.WhenAll(save, move).WaitAsync(WaitLimit);

        Assert.Equal(EditAb, await storage.LoadNoteAsync(vm.CurrentNotePath!));
        Assert.Equal(EditAb, vm.CurrentMarkdown);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task OpenLinkedNote_WhileASaveWithAPendingEditIsInFlight_KeepsTheEditInTheFirstNote()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);
        Task link = vm.OpenLinkedNoteAsync("Beta");
        gate.SetResult();
        await Task.WhenAll(save, link).WaitAsync(WaitLimit);

        Assert.Equal(EditAb, await storage.LoadNoteAsync(alpha));
        Assert.Equal(BetaPath(storage), vm.CurrentNotePath);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SynchronousSave_WhileAnAsyncSaveIsInFlight_TimesOutWithoutMarkingClean_AndTheAsyncSaveWritesTheLatest()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        storage.SyncSaveWait = ShortSyncWait;
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);

        Assert.False(vm.TrySaveSynchronously());
        Assert.True(vm.IsDirty);
        gate.SetResult();
        await save.WaitAsync(WaitLimit);

        Assert.Equal(EditAb, await storage.LoadNoteAsync(alpha));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task SynchronousSave_DuringADelete_TreatsTheNoteAsCleanAndDoesNotRecreateIt()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        storage.SyncSaveWait = ShortSyncWait;
        TaskCompletionSource gate = storage.DeleteGate = NewGate();

        Task delete = vm.DeleteCurrentNoteCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);

        Assert.True(vm.TrySaveSynchronously());
        Assert.False(vm.IsDirty);
        Assert.Empty(storage.SavedContents);
        gate.SetResult();
        await delete.WaitAsync(WaitLimit);

        Assert.DoesNotContain(alpha, storage.Paths);
        Assert.Empty(storage.SavedContents);
    }

    [Fact]
    public async Task SynchronousSave_DuringARename_IsRefusedAndWritesNothingAtTheOldPath()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        await vm.SaveCommand.ExecuteAsync(null);
        storage.SyncSaveWait = ShortSyncWait;
        TaskCompletionSource gate = storage.MutationGate = NewGate();

        Task rename = vm.RenameCurrentNoteCommand.ExecuteAsync("renamed");
        vm.TryApplyEditorMarkdown(EditAb);

        Assert.False(vm.TrySaveSynchronously());
        Assert.True(vm.IsDirty);
        gate.SetResult();
        await rename.WaitAsync(WaitLimit);

        Assert.DoesNotContain(alpha, storage.Paths);
        Assert.Equal([EditA], storage.SavedContents);
    }

    [Fact]
    public async Task SynchronousSave_DuringAMove_IsRefusedAndWritesNothingAtTheOldPath()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        await vm.SaveCommand.ExecuteAsync(null);
        storage.SyncSaveWait = ShortSyncWait;
        TaskCompletionSource gate = storage.MutationGate = NewGate();

        Task<string?> move = vm.MoveNoteToFolderAsync(alpha, Path.Combine(storage.NotesRootPath, "archive"));
        vm.TryApplyEditorMarkdown(EditAb);

        Assert.False(vm.TrySaveSynchronously());
        Assert.True(vm.IsDirty);
        gate.SetResult();
        await move.WaitAsync(WaitLimit);

        Assert.DoesNotContain(alpha, storage.Paths);
        Assert.Equal([EditA], storage.SavedContents);
    }

    [Fact]
    public async Task EditThroughTheEditorEntryPoint_DuringASave_KeepsTheNoteDirtyUntilItIsWritten()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, _) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        bool applied = vm.TryApplyEditorMarkdown(EditAb);

        Assert.True(applied);
        Assert.True(vm.IsDirty);
        gate.SetResult();
        await save.WaitAsync(WaitLimit);
        Assert.Equal(EditAb, storage.SavedContents[^1]);
    }

    [Fact]
    public async Task DeleteOpenNote_WhileASaveIsInFlight_DoesNotRecreateTheFile()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.SaveGate = NewGate();

        Task save = vm.SaveCommand.ExecuteAsync(null);
        Task delete = vm.DeleteCurrentNoteCommand.ExecuteAsync(null);
        gate.SetResult();
        await Task.WhenAll(save, delete).WaitAsync(WaitLimit);

        Assert.DoesNotContain(alpha, storage.Paths);
        Assert.False(vm.IsDirty);
        Assert.NotEqual(alpha, vm.CurrentNotePath);
    }

    [Fact]
    public async Task DeleteOpenNote_WithEditsTypedDuringTheDelete_DoesNotWriteThemBack()
    {
        (FakeNotesStorage storage, NotesToolViewModel vm, string alpha) = await OpenAlphaDirtyAsync();
        TaskCompletionSource gate = storage.DeleteGate = NewGate();

        Task delete = vm.DeleteCurrentNoteCommand.ExecuteAsync(null);
        vm.TryApplyEditorMarkdown(EditAb);
        Task save = vm.SaveCommand.ExecuteAsync(null);
        gate.SetResult();
        await Task.WhenAll(delete, save).WaitAsync(WaitLimit);

        Assert.DoesNotContain(alpha, storage.Paths);
        Assert.False(vm.IsDirty);
        Assert.Empty(storage.SavedContents);
    }

    [Fact]
    public async Task RenameOpenNote_ShowsTheRewrittenHeading_AndTheNextSaveDoesNotRevertIt()
    {
        FakeNotesStorage storage = CreateStorage();
        NotesToolViewModel vm = await CreateViewModelAsync(storage);
        await vm.ReloadAsync(AlphaPath(storage));

        await vm.RenameCurrentNoteCommand.ExecuteAsync("renamed");

        Assert.StartsWith("# renamed", vm.CurrentMarkdown, StringComparison.Ordinal);
        Assert.False(vm.IsDirty);
        vm.TryApplyEditorMarkdown(vm.CurrentMarkdown + "\nmore");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("# renamed", await storage.LoadNoteAsync(vm.CurrentNotePath!), StringComparison.Ordinal);
    }

    private static TaskCompletionSource NewGate() => new();

    private static string AlphaPath(FakeNotesStorage storage)
        => storage.Paths.First(path => path.EndsWith("alpha.md", StringComparison.OrdinalIgnoreCase));

    private static string BetaPath(FakeNotesStorage storage)
        => storage.Paths.First(path => path.EndsWith("beta.md", StringComparison.OrdinalIgnoreCase));

    private static FakeNotesStorage CreateStorage()
        => new(
            Path.Combine(Path.GetTempPath(), $"heimdall-notes-fake-{Guid.NewGuid():N}"),
            new FakeNoteSeed("alpha.md", Original),
            new FakeNoteSeed("beta.md", "# Beta\nbeta"));

    private static async Task<(FakeNotesStorage Storage, NotesToolViewModel Vm, string Alpha)> OpenAlphaDirtyAsync()
    {
        FakeNotesStorage storage = CreateStorage();
        NotesToolViewModel vm = await CreateViewModelAsync(storage);
        string alpha = AlphaPath(storage);
        await vm.ReloadAsync(alpha);
        Assert.True(vm.TryApplyEditorMarkdown(EditA));
        return (storage, vm, alpha);
    }

    private static async Task<NotesToolViewModel> CreateViewModelAsync(FakeNotesStorage storage)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        var vm = new NotesToolViewModel(storage, localizer, new FakeUiDispatcher());
        await vm.InitializeAsync();
        return vm;
    }
}
