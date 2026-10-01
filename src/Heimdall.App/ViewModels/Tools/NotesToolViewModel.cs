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

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.ViewModels.Tools;

internal sealed partial class NotesToolViewModel : ObservableObject, IDisposable
{
    private readonly INotesStorageService _storage;
    private readonly LocalizationManager _localizer;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private IReadOnlyList<NoteListItem> _allNotes = Array.Empty<NoteListItem>();
    private CancellationTokenSource? _refreshCts;
    private ToolContext? _context;
    private bool _disposed;
    private bool _initialized;
    private bool _suppressCriteriaReload = false;
    private bool _suppressTagReload;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private NoteSortOrder _sortOrder = NoteSortOrder.DateDescending;
    [ObservableProperty] private string? _selectedTag;
    [ObservableProperty] private NoteTreeNode? _selectedNote;
    [ObservableProperty] private string? _currentNotePath;
    [ObservableProperty] private string _currentMarkdown = string.Empty;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _hasTagFilters;
    [ObservableProperty] private string _selectedNoteTitle = string.Empty;
    [ObservableProperty] private string _selectedNotePathDisplay = string.Empty;
    [ObservableProperty] private string _listFooterText = string.Empty;

    public ObservableCollection<string> AvailableTags { get; } = [];
    public ObservableCollection<NoteTagFilterChip> TagFilters { get; } = [];
    public ObservableCollection<NoteTreeNode> Notes { get; } = [];

    public event EventHandler<string>? CopyConfluenceRequested;
    public event EventHandler<string>? ExportConfluenceRequested;
    public event EventHandler<string>? ExportHtmlRequested;

    public NotesToolViewModel(
        INotesStorageService storage,
        LocalizationManager localizer,
        IUiDispatcher uiDispatcher)
    {
        _storage = storage;
        _localizer = localizer;
        _uiDispatcher = uiDispatcher;
        SelectedNoteTitle = L("ToolNotesNoSelection");
        SelectedNotePathDisplay = storage.NotesRootPath;
        ListFooterText = FormatNoteCount(0);
        StatusMessage = L("ToolNotesStatusReady");
    }

    public string NotesRootPath => _storage.NotesRootPath;

    public IReadOnlyList<NoteListItem> AllNotes => _allNotes;

    public void SetContext(ToolContext? context)
    {
        _context = context;
    }

    public Task InitializeAsync()
    {
        ThrowIfDisposed();
        _storage.EnsureInitialized();
        _initialized = true;
        SetStatus(L("ToolNotesStatusReady"));
        return Task.CompletedTask;
    }

    public bool TrySaveSynchronously()
    {
        ThrowIfDisposed();

        if (!IsDirty || CurrentNotePath is null)
        {
            return true;
        }

        try
        {
            _storage.SaveNote(CurrentNotePath, CurrentMarkdown);
            IsDirty = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsUnderNotesRoot(string filePath) => _storage.IsUnderNotesRoot(filePath);

    public string GetRelativePath(string filePath) => _storage.GetRelativePath(filePath);

    public void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    public async Task ReloadAsync(
        string? preferredPath = null,
        bool allowFallbackSelection = true,
        bool preserveCurrentNoteWhenFilteredOut = false)
    {
        ThrowIfDisposed();

        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();

        try
        {
            IsBusy = true;
            var notes = await _storage
                .ListNotesAsync(SearchText, SortOrder, _refreshCts.Token)
                .ConfigureAwait(true);

            if (_refreshCts.IsCancellationRequested)
            {
                return;
            }

            _allNotes = notes;
            await RunOnUiAsync(RebuildAvailableTags);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshVisibleNotesAsync(
            preferredPath,
            allowFallbackSelection,
            preserveCurrentNoteWhenFilteredOut).ConfigureAwait(true);
    }

    public async Task CreateFolderAsync(string folderName, string? parentFolder)
    {
        ThrowIfDisposed();
        _storage.CreateFolder(folderName, parentFolder);
        await ReloadAsync(CurrentNotePath).ConfigureAwait(true);
    }

    public async Task<string> MoveNoteToFolderAsync(string filePath, string targetFolder)
    {
        ThrowIfDisposed();
        var newPath = await _storage.MoveNoteToFolderAsync(filePath, targetFolder).ConfigureAwait(true);
        var preferredPath = string.Equals(CurrentNotePath, filePath, StringComparison.OrdinalIgnoreCase)
            ? newPath
            : CurrentNotePath;
        await ReloadAsync(preferredPath).ConfigureAwait(true);
        return newPath;
    }

    public async Task OpenLinkedNoteAsync(string noteReference)
    {
        ThrowIfDisposed();

        var notePath = await _storage.ResolveOrCreateNoteAsync(noteReference).ConfigureAwait(true);
        if (string.Equals(CurrentNotePath, notePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (CurrentNotePath is not null)
        {
            _backStack.Push(CurrentNotePath);
            _forwardStack.Clear();
            UpdateNavigationState();
        }

        await ReloadAsync(notePath).ConfigureAwait(true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private async Task NewNoteFromTemplateAsync(string templateKind)
    {
        ThrowIfDisposed();

        if (!Enum.TryParse<NoteTemplateKind>(templateKind, ignoreCase: true, out var kind))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await SaveAsync().ConfigureAwait(true);
            var notePath = await _storage.CreateNoteAsync(kind, _context, _localizer).ConfigureAwait(true);
            await ReloadAsync(notePath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenNoteAsync(string path) => OpenNoteCoreAsync(path, pushCurrentToBackStack: true);

    [RelayCommand]
    private async Task SaveAsync()
    {
        ThrowIfDisposed();

        if (!IsDirty || CurrentNotePath is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SetStatus(L("ToolNotesStatusSaving"));
            await _storage.SaveNoteAsync(CurrentNotePath, CurrentMarkdown).ConfigureAwait(true);
            IsDirty = false;
            SetStatus(string.Format(L("ToolNotesStatusSaved"), DateTime.Now.ToString("HH:mm:ss")));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteCurrentNoteAsync()
    {
        ThrowIfDisposed();

        if (CurrentNotePath is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            var deletedPath = CurrentNotePath;
            await _storage.DeleteNoteAsync(deletedPath).ConfigureAwait(true);
            CurrentNotePath = null;
            await ReloadAsync().ConfigureAwait(true);
            SetStatus(string.Format(L("ToolNotesStatusDeleted"), Path.GetFileNameWithoutExtension(deletedPath)));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameCurrentNoteAsync(string newName)
    {
        ThrowIfDisposed();

        if (CurrentNotePath is null || string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        try
        {
            IsBusy = true;
            await SaveAsync().ConfigureAwait(true);
            var newPath = await _storage.RenameNoteAsync(CurrentNotePath, newName).ConfigureAwait(true);
            CurrentNotePath = newPath;
            await ReloadAsync(newPath).ConfigureAwait(true);
            SetStatus(string.Format(L("ToolNotesStatusRenamed"), _storage.GetRelativePath(newPath)));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DuplicateCurrentNoteAsync()
    {
        ThrowIfDisposed();

        if (CurrentNotePath is null)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await SaveAsync().ConfigureAwait(true);
            var copyPath = await _storage
                .DuplicateNoteAsync(CurrentNotePath, L("ToolNotesDuplicateSuffix"))
                .ConfigureAwait(true);
            await ReloadAsync(copyPath).ConfigureAwait(true);
            SetStatus(string.Format(L("ToolNotesStatusDuplicated"), _storage.GetRelativePath(copyPath)));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyConfluenceAsync()
    {
        ThrowIfDisposed();

        if (!TryBuildConfluencePayload(out var payload))
        {
            return;
        }

        await RunBusyAsync(() =>
        {
            CopyConfluenceRequested?.Invoke(this, payload);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportConfluenceAsync()
    {
        ThrowIfDisposed();

        if (!TryBuildConfluencePayload(out var payload))
        {
            return;
        }

        await RunBusyAsync(() =>
        {
            ExportConfluenceRequested?.Invoke(this, payload);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        ThrowIfDisposed();

        if (!TryBuildHtmlPayload(out var payload))
        {
            return;
        }

        await RunBusyAsync(() =>
        {
            ExportHtmlRequested?.Invoke(this, payload);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void SelectTag(string? tag)
    {
        SelectedTag = string.IsNullOrWhiteSpace(tag) ? null : tag;
    }

    [RelayCommand]
    private async Task GoBack()
    {
        ThrowIfDisposed();

        if (_backStack.Count == 0)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(true);

        if (CurrentNotePath is not null)
        {
            _forwardStack.Push(CurrentNotePath);
        }

        var target = _backStack.Pop();
        UpdateNavigationState();
        await OpenNoteCoreAsync(target, pushCurrentToBackStack: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task GoForward()
    {
        ThrowIfDisposed();

        if (_forwardStack.Count == 0)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(true);

        if (CurrentNotePath is not null)
        {
            _backStack.Push(CurrentNotePath);
        }

        var target = _forwardStack.Pop();
        UpdateNavigationState();
        await OpenNoteCoreAsync(target, pushCurrentToBackStack: false).ConfigureAwait(true);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_initialized && !_suppressCriteriaReload)
        {
            _ = ReloadAsync(CurrentNotePath, allowFallbackSelection: false, preserveCurrentNoteWhenFilteredOut: true);
        }
    }

    partial void OnSortOrderChanged(NoteSortOrder value)
    {
        if (_initialized && !_suppressCriteriaReload)
        {
            _ = ReloadAsync(CurrentNotePath, allowFallbackSelection: false, preserveCurrentNoteWhenFilteredOut: true);
        }
    }

    partial void OnSelectedTagChanged(string? value)
    {
        RebuildTagFilters();

        if (_initialized && !_suppressTagReload)
        {
            _ = RefreshVisibleNotesAsync(
                CurrentNotePath,
                allowFallbackSelection: false,
                preserveCurrentNoteWhenFilteredOut: true);
        }
    }

    private async Task RefreshVisibleNotesAsync(
        string? preferredPath = null,
        bool allowFallbackSelection = true,
        bool preserveCurrentNoteWhenFilteredOut = false)
    {
        ThrowIfDisposed();

        var visibleNotes = GetVisibleNotes();
        var tree = NoteTreeNode.BuildTree(visibleNotes, _storage.NotesRootPath);
        await RunOnUiAsync(() =>
        {
            ReplaceCollection(Notes, tree);
            ListFooterText = FormatNoteCount(visibleNotes.Count);
            SelectedNote = FindNodeByPath(Notes, preferredPath ?? CurrentNotePath);
        });

        var targetPath = preferredPath ?? CurrentNotePath;
        var target = visibleNotes.FirstOrDefault(n =>
            string.Equals(n.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));

        if (target is not null)
        {
            await OpenNoteCoreAsync(target.FilePath, pushCurrentToBackStack: false).ConfigureAwait(true);
        }
        else if (!preserveCurrentNoteWhenFilteredOut || CurrentNotePath is null)
        {
            await ClearCurrentNoteAsync().ConfigureAwait(true);
        }
    }

    private IReadOnlyList<NoteListItem> GetVisibleNotes()
    {
        if (string.IsNullOrWhiteSpace(SelectedTag))
        {
            return _allNotes;
        }

        return _allNotes
            .Where(note => note.Tags.Any(tag => string.Equals(tag, SelectedTag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private void RebuildAvailableTags()
    {
        var availableTags = _allNotes
            .SelectMany(note => note.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (SelectedTag is not null
            && !availableTags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase))
        {
            _suppressTagReload = true;
            try
            {
                SelectedTag = null;
            }
            finally
            {
                _suppressTagReload = false;
            }
        }

        ReplaceCollection(AvailableTags, availableTags);
        RebuildTagFilters();
    }

    private void RebuildTagFilters()
    {
        HasTagFilters = AvailableTags.Count > 0;
        if (!HasTagFilters)
        {
            TagFilters.Clear();
            return;
        }

        var tagFilters = new List<NoteTagFilterChip>
        {
            new(null, L("ToolNotesAllTags"), string.IsNullOrWhiteSpace(SelectedTag))
        };

        tagFilters.AddRange(AvailableTags.Select(tag =>
            new NoteTagFilterChip(
                tag,
                tag,
                string.Equals(tag, SelectedTag, StringComparison.OrdinalIgnoreCase))));

        ReplaceCollection(TagFilters, tagFilters);
    }

    private async Task OpenNoteCoreAsync(string filePath, bool pushCurrentToBackStack)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            await ClearCurrentNoteAsync().ConfigureAwait(true);
            return;
        }

        if (string.Equals(CurrentNotePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            SelectedNote = FindNodeByPath(Notes, filePath);
            UpdateSelectionMetadata();
            return;
        }

        await SaveAsync().ConfigureAwait(true);

        try
        {
            IsBusy = true;

            if (pushCurrentToBackStack && CurrentNotePath is not null)
            {
                _backStack.Push(CurrentNotePath);
                _forwardStack.Clear();
                UpdateNavigationState();
            }

            var content = await _storage.LoadNoteAsync(filePath).ConfigureAwait(true);
            CurrentMarkdown = content;
            IsDirty = false;
            SelectedNote = FindNodeByPath(Notes, filePath);
            CurrentNotePath = filePath;
            HasSelection = true;
            UpdateSelectionMetadata();
            SetStatus(string.Format(L("ToolNotesStatusOpened"), _storage.GetRelativePath(filePath)));
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task ClearCurrentNoteAsync()
    {
        CurrentMarkdown = string.Empty;
        IsDirty = false;
        SelectedNote = null;
        CurrentNotePath = null;
        HasSelection = false;
        SelectedNoteTitle = L("ToolNotesNoSelection");
        SelectedNotePathDisplay = _storage.NotesRootPath;
        return Task.CompletedTask;
    }

    private void UpdateSelectionMetadata()
    {
        if (CurrentNotePath is null)
        {
            HasSelection = false;
            SelectedNoteTitle = L("ToolNotesNoSelection");
            SelectedNotePathDisplay = _storage.NotesRootPath;
            return;
        }

        HasSelection = true;
        SelectedNoteTitle = SelectedNote?.Note?.Title ?? Path.GetFileNameWithoutExtension(CurrentNotePath);
        SelectedNotePathDisplay = _storage.GetRelativePath(CurrentNotePath);
    }

    private void UpdateNavigationState()
    {
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    private static NoteTreeNode? FindNodeByPath(IEnumerable<NoteTreeNode> nodes, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        foreach (var node in nodes)
        {
            if (string.Equals(node.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            if (node.Children.Count == 0)
            {
                continue;
            }

            var match = FindNodeByPath(node.Children, filePath);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private async Task RunOnUiAsync(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _uiDispatcher.InvokeAsync(action);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool TryBuildConfluencePayload(out string payload)
    {
        payload = string.Empty;

        if (!HasSelection || CurrentNotePath is null)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), L("ToolNotesNoSelection")), isError: true);
            return false;
        }

        payload = ConfluenceStorageConverter.Convert(CurrentMarkdown ?? string.Empty);
        return true;
    }

    private bool TryBuildHtmlPayload(out string payload)
    {
        payload = string.Empty;

        if (!HasSelection || CurrentNotePath is null)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), L("ToolNotesNoSelection")), isError: true);
            return false;
        }

        payload = MarkdownPreviewBuilder.BuildHtmlDocument(CurrentMarkdown ?? string.Empty, SelectedNoteTitle);
        return true;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            IsBusy = true;
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetStatus(string.Format(L("ToolNotesStatusError"), ex.Message), isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string L(string key) => _localizer[key];

    private string FormatNoteCount(int count) =>
        _localizer.FormatCount(count, "ToolNotesCountOne", "ToolNotesCount", count);
}

internal sealed record NoteTagFilterChip(string? Value, string DisplayLabel, bool IsSelected);
