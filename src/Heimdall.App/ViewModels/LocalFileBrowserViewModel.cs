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
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;
using Heimdall.Sftp;

namespace Heimdall.App.ViewModels;

/// <summary>
/// ViewModel for the local filesystem browser panel. Manages directory state,
/// navigation history, file listing, filtering, and file operations while
/// delegating user prompts and notifications to <see cref="IDialogService"/>.
/// </summary>
public sealed partial class LocalFileBrowserViewModel : ObservableObject
{
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".sh"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml", ".conf", ".cfg", ".ini",
        ".ps1", ".bat", ".cmd", ".sh", ".bash", ".py", ".rb", ".js", ".ts", ".css", ".html",
        ".htm", ".cs", ".java", ".c", ".cpp", ".h", ".hpp", ".sql", ".csv", ".env",
        ".toml", ".properties", ".service", ".timer", ".socket", ".gitignore", ".editorconfig"
    };

    private readonly Stack<string> _history = new();
    private readonly List<LocalFileEntry> _allFiles = [];
    private readonly LocalizationManager? _localizer;
    private IDialogService? _dialogService;
    private IFileConflictDialogPresenter? _fileConflictDialogPresenter;
    private int _loadGeneration;

    [ObservableProperty]
    private string _currentPath = "";

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private string _emptyFolderText = "";

    [ObservableProperty]
    private bool _showEmptyFolder;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalFileBrowserViewModel"/> class.
    /// </summary>
    /// <param name="startPath">The initial directory path for the browser.</param>
    /// <param name="localizer">Optional localization manager for UI strings.</param>
    /// <param name="editorPath">Optional external editor path used by the view.</param>
    public LocalFileBrowserViewModel(
        string startPath,
        LocalizationManager? localizer = null,
        string? editorPath = null)
    {
        _localizer = localizer;

        var validatedPath = Directory.Exists(startPath)
            ? startPath
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        SessionRoot = validatedPath;
        EditorPath = editorPath;
        Files = [];
        CurrentPath = validatedPath;

        _ = LoadDirectoryCoreAsync(validatedPath, pushToHistory: false);
    }

    /// <summary>
    /// Gets the currently displayed files and directories after filtering.
    /// </summary>
    public ObservableCollection<LocalFileEntry> Files { get; }

    /// <summary>
    /// Gets the root directory associated with the local shell session.
    /// </summary>
    public string SessionRoot { get; }

    /// <summary>
    /// Gets the configured external editor path, if any.
    /// </summary>
    public string? EditorPath { get; }

    /// <summary>
    /// Raised when the user requests navigation to a directory path in the terminal.
    /// </summary>
    public event Action<string>? NavigateToPathRequested;

    /// <summary>
    /// Raised when the user requests execution of a script file in the terminal.
    /// </summary>
    public event Action<string>? RunInShellRequested;

    /// <summary>
    /// Raised when the user wants to edit a file in the embedded editor.
    /// </summary>
    public event Action<string>? EditInEditorRequested;

    /// <summary>
    /// Loads a directory and refreshes the unfiltered file cache.
    /// </summary>
    /// <param name="path">The directory path to load.</param>
    public Task LoadDirectory(string path)
    {
        return LoadDirectoryCoreAsync(path, pushToHistory: true);
    }

    /// <summary>
    /// Navigates to the previous directory in the back stack when available.
    /// </summary>
    public Task NavigateBack()
    {
        if (_history.TryPop(out string? previousPath) && previousPath is not null)
        {
            return LoadDirectoryCoreAsync(previousPath, pushToHistory: false);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Navigates to the parent directory of the current path.
    /// </summary>
    public Task NavigateUp()
    {
        DirectoryInfo? parent = Directory.GetParent(CurrentPath);
        if (parent is not null)
        {
            return LoadDirectory(parent.FullName);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Navigates back to the session root directory.
    /// </summary>
    public Task NavigateHome()
    {
        return LoadDirectory(SessionRoot);
    }

    /// <summary>
    /// Reloads the current directory contents without modifying history.
    /// </summary>
    public Task Refresh()
    {
        return LoadDirectoryCoreAsync(CurrentPath, pushToHistory: false);
    }

    /// <summary>
    /// Navigates to a user-entered path when it points to an existing directory.
    /// </summary>
    /// <param name="path">The requested directory path.</param>
    public Task NavigateToPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        string requestedPath = path.Trim();
        if (!Directory.Exists(requestedPath))
        {
            return Task.CompletedTask;
        }

        return LoadDirectory(requestedPath);
    }

    /// <summary>
    /// Handles the logical part of a double-click action.
    /// Returns <see langword="true"/> when the action was handled inside the ViewModel.
    /// </summary>
    /// <param name="entry">The selected file system entry.</param>
    /// <returns><see langword="true"/> if handled; otherwise <see langword="false"/>.</returns>
    public bool HandleFileDoubleClick(LocalFileEntry entry)
    {
        if (entry.IsDirectory)
        {
            _ = LoadDirectory(entry.FullPath);
            return true;
        }

        if (IsTextFile(entry.Name) && EditInEditorRequested is not null)
        {
            EditInEditorRequested.Invoke(entry.FullPath);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Raises the terminal navigation request for the supplied path.
    /// </summary>
    /// <param name="path">The path that should become the shell working directory.</param>
    public void InvokeNavigateToPath(string path)
    {
        NavigateToPathRequested?.Invoke(path);
    }

    /// <summary>
    /// Raises the run-in-shell request for the supplied file path.
    /// </summary>
    /// <param name="path">The file path to execute in the terminal.</param>
    public void InvokeRunInShell(string path)
    {
        RunInShellRequested?.Invoke(path);
    }

    /// <summary>
    /// Determines whether the supplied file should open in the embedded text editor.
    /// </summary>
    /// <param name="fileName">The file name to inspect.</param>
    /// <returns><see langword="true"/> for known text extensions; otherwise <see langword="false"/>.</returns>
    public bool IsTextFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && TextExtensions.Contains(extension);
    }

    /// <summary>
    /// Determines whether the supplied file can be run directly in the shell.
    /// </summary>
    /// <param name="fileName">The file name to inspect.</param>
    /// <returns><see langword="true"/> for runnable extensions; otherwise <see langword="false"/>.</returns>
    public bool IsRunnableFile(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(extension) && RunnableExtensions.Contains(extension);
    }

    /// <summary>
    /// Updates the dialog service after construction when the view resolves it lazily from DI.
    /// </summary>
    /// <param name="dialogService">The dialog service used for prompts and notifications.</param>
    internal void SetDialogService(IDialogService? dialogService)
    {
        _dialogService = dialogService;
    }

    internal static IReadOnlyList<FileConflictPlanItem> BuildConflictPlanItems(
        IReadOnlyList<LocalPasteOp> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        List<FileConflictPlanItem> planItems = new(operations.Count);
        foreach (LocalPasteOp operation in operations)
        {
            FileConflictItemKind kind = operation.Kind switch
            {
                LocalPasteOpKind.CopyFile => FileConflictItemKind.File,
                LocalPasteOpKind.CreateDirectory => FileConflictItemKind.Directory,
                _ => throw new ArgumentOutOfRangeException(nameof(operations), operation.Kind, "Unknown paste operation."),
            };
            planItems.Add(new FileConflictPlanItem(operation.SourcePath, operation.TargetPath, kind));
        }

        return planItems;
    }

    internal static bool ShouldOverwriteOnCopy(
        FileConflictAnalysisItem analysis,
        FileConflictEffectiveAction action)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return action == FileConflictEffectiveAction.Proceed && analysis.HasConflict;
    }

    internal static LocalRenameCollisionAction DetermineRenameCollisionAction(
        bool isDirectory,
        string sourceFullPath,
        string newPath,
        bool targetFileExists,
        bool targetDirectoryExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFullPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);

        if (!targetFileExists && !targetDirectoryExists)
        {
            return LocalRenameCollisionAction.None;
        }

        if (string.Equals(sourceFullPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return LocalRenameCollisionAction.None;
        }

        if (isDirectory || targetDirectoryExists)
        {
            return LocalRenameCollisionAction.BlockExistingTarget;
        }

        return targetFileExists
            ? LocalRenameCollisionAction.ConfirmOverwriteFile
            : LocalRenameCollisionAction.None;
    }

    /// <summary>
    /// Deletes the provided entries after user confirmation and refreshes the current directory.
    /// </summary>
    /// <param name="entries">The selected file system entries to delete.</param>
    public async Task DeleteEntriesAsync(IReadOnlyList<LocalFileEntry> entries)
    {
        if (entries.Count == 0 || !TryGetDialogService(out var dialogService))
        {
            return;
        }

        var title = L10n("FileBrowserConfirmDeleteTitle");

        foreach (var entry in entries)
        {
            var message = string.Format(L10n("FileBrowserConfirmDeleteMessage"), entry.Name);
            var confirmed = await dialogService.ShowConfirmAsync(title, message, "warning");
            if (!confirmed)
            {
                continue;
            }

            try
            {
                await Task.Run(() =>
                {
                    if (entry.IsDirectory)
                    {
                        Directory.Delete(entry.FullPath, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry.FullPath);
                    }
                });
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(L10n("FileBrowserDeleteError"), entry.Name, ex.Message);
                dialogService.ShowError(title, errorMessage);
            }
        }

        await Refresh();
    }

    /// <summary>
    /// Prompts the user for a new name and renames the selected entry.
    /// </summary>
    /// <param name="entry">The selected file system entry to rename.</param>
    public async Task RenameEntryAsync(LocalFileEntry entry)
    {
        if (!TryGetDialogService(out var dialogService))
        {
            return;
        }

        var title = L10n("FileBrowserRenameTitle");
        var prompt = L10n("FileBrowserRenamePrompt");
        var newName = (await dialogService.ShowInputAsync(title, prompt, entry.Name))?.Trim();

        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name)
        {
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.Contains("..", StringComparison.Ordinal))
        {
            dialogService.ShowWarning(title, L10n("ErrorInvalidFileName"));
            return;
        }

        try
        {
            string parentDir = Path.GetDirectoryName(entry.FullPath)!;
            string newPath = Path.GetFullPath(Path.Combine(parentDir, newName));
            string safeParentDir = parentDir.EndsWith(Path.DirectorySeparatorChar)
                ? parentDir
                : parentDir + Path.DirectorySeparatorChar;

            if (!newPath.StartsWith(safeParentDir, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(newPath, parentDir, StringComparison.OrdinalIgnoreCase))
            {
                dialogService.ShowWarning(title, L10n("ErrorInvalidFileName"));
                return;
            }

            var collisionAction = DetermineRenameCollisionAction(
                entry.IsDirectory,
                entry.FullPath,
                newPath,
                File.Exists(newPath),
                Directory.Exists(newPath));
            if (collisionAction == LocalRenameCollisionAction.ConfirmOverwriteFile)
            {
                var overwriteMessage = string.Format(L10n("FileBrowserPasteOverwriteMessage"), newName);
                var overwrite = await dialogService.ShowConfirmAsync(title, overwriteMessage, "warning");
                if (!overwrite)
                {
                    return;
                }
            }
            else if (collisionAction == LocalRenameCollisionAction.BlockExistingTarget)
            {
                var warningMessage = string.Format(L10n("FileBrowserRenameTargetExists"), newName);
                dialogService.ShowWarning(title, warningMessage);
                return;
            }

            await Task.Run(() =>
            {
                if (entry.IsDirectory)
                {
                    Directory.Move(entry.FullPath, newPath);
                }
                else
                {
                    File.Move(
                        entry.FullPath,
                        newPath,
                        overwrite: collisionAction == LocalRenameCollisionAction.ConfirmOverwriteFile);
                }
            });

            await Refresh();
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(L10n("FileBrowserRenameError"), entry.Name, ex.Message);
            dialogService.ShowError(title, errorMessage);
        }
    }

    /// <summary>
    /// Prompts the user for a folder name and creates it in the current directory.
    /// </summary>
    public async Task CreateFolderAsync()
    {
        if (!TryGetDialogService(out var dialogService))
        {
            return;
        }

        var title = L10n("FileBrowserNewFolderTitle");
        var prompt = L10n("FileBrowserNewFolderPrompt");
        var folderName = (await dialogService.ShowInputAsync(title, prompt))?.Trim();

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName.Contains("..", StringComparison.Ordinal))
        {
            dialogService.ShowWarning(title, L10n("ErrorInvalidFileName"));
            return;
        }

        try
        {
            string newPath = Path.GetFullPath(Path.Combine(CurrentPath, folderName));
            string safeCurrentPath = CurrentPath.EndsWith(Path.DirectorySeparatorChar)
                ? CurrentPath
                : CurrentPath + Path.DirectorySeparatorChar;

            if (!newPath.StartsWith(safeCurrentPath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(newPath, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                dialogService.ShowWarning(title, L10n("ErrorInvalidFileName"));
                return;
            }

            Directory.CreateDirectory(newPath);
            await Refresh();
        }
        catch (Exception ex)
        {
            var errorMessage = string.Format(L10n("FileBrowserNewFolderError"), ex.Message);
            dialogService.ShowError(title, errorMessage);
        }
    }

    /// <summary>
    /// Pastes files and directories from the supplied source paths into the current directory.
    /// </summary>
    /// <param name="sourcePaths">The clipboard file paths provided by the view.</param>
    public async Task PasteFilesAsync(IReadOnlyList<string> sourcePaths)
    {
        if (sourcePaths.Count == 0 || !TryGetDialogService(out IDialogService dialogService))
        {
            return;
        }

        string title = L10n("FileBrowserPasteOverwriteTitle");
        List<(string SourcePath, IReadOnlyList<LocalPasteOp> Operations)> plannedRoots = [];
        List<LocalPasteOp> operations = [];
        List<string> refusedRootNames = [];

        foreach (string sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                continue;
            }

            try
            {
                bool isDirectory;
                if (File.Exists(sourcePath))
                {
                    isDirectory = false;
                }
                else if (Directory.Exists(sourcePath))
                {
                    isDirectory = true;
                }
                else
                {
                    continue;
                }

                if (isDirectory && LocalPasteTreePlanner.IsSameOrDescendantPath(sourcePath, CurrentPath))
                {
                    refusedRootNames.Add(Path.GetFileName(sourcePath));
                    continue;
                }

                FileAttributes attributes = File.GetAttributes(sourcePath);
                LocalPasteEntry root = new(
                    sourcePath,
                    Path.GetFileName(sourcePath),
                    isDirectory,
                    (attributes & FileAttributes.ReparsePoint) != 0);
                IReadOnlyList<LocalPasteOp> rootOperations = LocalPasteTreePlanner.Plan(
                    [root],
                    CurrentPath,
                    directoryPath => Directory
                        .EnumerateFileSystemEntries(directoryPath)
                        .Select(childPath =>
                        {
                            FileAttributes childAttributes = File.GetAttributes(childPath);
                            return new LocalPasteEntry(
                                childPath,
                                Path.GetFileName(childPath),
                                (childAttributes & FileAttributes.Directory) != 0,
                                (childAttributes & FileAttributes.ReparsePoint) != 0);
                        })
                        .ToList());
                plannedRoots.Add((sourcePath, rootOperations));
                operations.AddRange(rootOperations);
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format(
                    L10n("FileBrowserPasteError"),
                    Path.GetFileName(sourcePath),
                    ex.Message);
                dialogService.ShowError(title, errorMessage);
            }
        }

        if (refusedRootNames.Count > 0)
        {
            string refusedNames = string.Join(", ", refusedRootNames);
            string errorMessage = string.Format(L10n("FileBrowserPasteRefusedSelfTarget"), refusedNames);
            dialogService.ShowError(title, errorMessage);
        }

        IReadOnlyList<FileConflictPlanItem> planItems = BuildConflictPlanItems(operations);
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            planItems,
            path => File.Exists(path)
                ? FileConflictItemKind.File
                : Directory.Exists(path)
                    ? FileConflictItemKind.Directory
                    : null,
            StringComparer.OrdinalIgnoreCase,
            FileConflictPolicy.Transfer);
        IReadOnlyList<FileConflictAnalysisItem> conflicts = analysis
            .Where(item => item.HasConflict)
            .ToList();

        IReadOnlyList<FileConflictDecision> decisions = [];
        if (conflicts.Count > 0)
        {
            _fileConflictDialogPresenter ??= new WpfFileConflictDialogPresenter();
            FileConflictDialogViewModel dialogViewModel = new(conflicts, _localizer);
            FileConflictDialogResult? dialogResult = await _fileConflictDialogPresenter
                .ShowAsync(dialogViewModel);
            if (dialogResult is null)
            {
                return;
            }

            decisions = dialogResult.Decisions;
        }

        IReadOnlyList<FileConflictResolvedItem> resolvedOperations = FileConflictPlanner.Resolve(
            analysis,
            decisions,
            path => File.Exists(path) || Directory.Exists(path),
            StringComparer.OrdinalIgnoreCase);

        int rootStartIndex = 0;
        foreach ((string sourcePath, IReadOnlyList<LocalPasteOp> rootOperations) in plannedRoots)
        {
            int currentRootStartIndex = rootStartIndex;
            rootStartIndex += rootOperations.Count;

            try
            {
                await Task.Run(() =>
                {
                    for (int localIndex = 0; localIndex < rootOperations.Count; localIndex++)
                    {
                        int operationIndex = currentRootStartIndex + localIndex;
                        LocalPasteOp operation = rootOperations[localIndex];
                        FileConflictResolvedItem resolved = resolvedOperations[operationIndex];
                        if (resolved.Action == FileConflictEffectiveAction.Skip)
                        {
                            continue;
                        }

                        switch (operation.Kind)
                        {
                            case LocalPasteOpKind.CreateDirectory:
                                Directory.CreateDirectory(resolved.EffectiveTargetPath);
                                break;
                            case LocalPasteOpKind.CopyFile:
                                bool overwrite = ShouldOverwriteOnCopy(
                                    analysis[operationIndex],
                                    resolved.Action);
                                File.Copy(
                                    operation.SourcePath,
                                    resolved.EffectiveTargetPath,
                                    overwrite);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(
                                    nameof(rootOperations),
                                    operation.Kind,
                                    "Unknown paste operation.");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format(
                    L10n("FileBrowserPasteError"),
                    Path.GetFileName(sourcePath),
                    ex.Message);
                dialogService.ShowError(title, errorMessage);
            }
        }

        await Refresh();
    }

    /// <summary>
    /// Builds and shows the properties dialog for the selected entry.
    /// </summary>
    /// <param name="entry">The selected file system entry.</param>
    public void ShowProperties(LocalFileEntry entry)
    {
        if (!TryGetDialogService(out var dialogService))
        {
            return;
        }

        try
        {
            dialogService.ShowInfo(L10n("FileBrowserPropertiesTitle"), BuildPropertiesText(entry));
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"Properties read failed: {ex.Message}");
        }
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
        UpdateStatusText();
    }

    private async Task LoadDirectoryCoreAsync(string path, bool pushToHistory)
    {
        int generation = ++_loadGeneration;
        IsLoading = true;

        try
        {
            List<LocalFileEntry> entries = await Task.Run(() => EnumerateDirectory(path));
            if (generation != _loadGeneration)
            {
                return;
            }

            if (pushToHistory && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                _history.Push(CurrentPath);
            }

            CurrentPath = path;
            _allFiles.Clear();
            _allFiles.AddRange(entries);

            ApplyFilter();
            CanGoBack = _history.Count > 0;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"LocalFileBrowser load failed: {ex.Message}");
            if (generation == _loadGeneration)
            {
                StatusText = L10n("FileBrowserLoadError");
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private static List<LocalFileEntry> EnumerateDirectory(string path)
    {
        List<LocalFileEntry> entries = [];

        foreach (string directoryPath in Directory.EnumerateDirectories(path))
        {
            try
            {
                DirectoryInfo info = new DirectoryInfo(directoryPath);
                entries.Add(new LocalFileEntry(info.Name, info.FullName, true, 0, info.LastWriteTime));
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] skip inaccessible directory: {ex.Message}");
            }
        }

        foreach (string filePath in Directory.EnumerateFiles(path))
        {
            try
            {
                FileInfo info = new FileInfo(filePath);
                entries.Add(new LocalFileEntry(info.Name, info.FullName, false, info.Length, info.LastWriteTime));
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] skip inaccessible file: {ex.Message}");
            }
        }

        return entries
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();

        Files.Clear();

        var source = string.IsNullOrEmpty(filter)
            ? _allFiles
            : _allFiles.Where(entry => entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        foreach (var entry in source)
        {
            Files.Add(entry);
        }

        ShowEmptyFolder = Files.Count == 0;
        EmptyFolderText = ShowEmptyFolder ? L10n("FileBrowserEmptyFolder") : string.Empty;
    }

    private void UpdateStatusText()
    {
        var template = L10n("FileBrowserStatusItems");
        StatusText = string.Format(template, Files.Count);
    }

    private string BuildPropertiesText(LocalFileEntry entry)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"{L10n("FileBrowserPropertiesName")}: {entry.Name}");
        builder.AppendLine($"{L10n("FileBrowserPropertiesPath")}: {entry.FullPath}");

        if (entry.IsDirectory)
        {
            var dirInfo = new DirectoryInfo(entry.FullPath);
            builder.AppendLine($"{L10n("FileBrowserPropertiesType")}: {L10n("FileBrowserPropertiesTypeFolder")}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesCreated")}: {dirInfo.CreationTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesModified")}: {dirInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesAttributes")}: {dirInfo.Attributes}");
        }
        else
        {
            var fileInfo = new FileInfo(entry.FullPath);
            builder.AppendLine($"{L10n("FileBrowserPropertiesType")}: {fileInfo.Extension}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesSize")}: {FileSize.Format(fileInfo.Length)}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesCreated")}: {fileInfo.CreationTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesModified")}: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesAccessed")}: {fileInfo.LastAccessTime:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"{L10n("FileBrowserPropertiesAttributes")}: {fileInfo.Attributes}");
        }

        return builder.ToString();
    }

    private bool TryGetDialogService(out IDialogService dialogService)
    {
        if (_dialogService is not null)
        {
            dialogService = _dialogService;
            return true;
        }

        Heimdall.Core.Logging.FileLogger.Warn("[LocalFileBrowser] dialog service unavailable.");
        dialogService = null!;
        return false;
    }

    private string L10n(string key) => _localizer?.GetString(key) ?? key;
}

/// <summary>
/// Represents a local filesystem entry displayed by the file browser ListView.
/// </summary>
public sealed record LocalFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastModified);

internal enum LocalRenameCollisionAction
{
    None,
    ConfirmOverwriteFile,
    BlockExistingTarget
}
