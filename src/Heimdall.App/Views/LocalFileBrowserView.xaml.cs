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

using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Views;

/// <summary>
/// Simple local filesystem browser for local shell sessions.
/// Shows files and folders with navigation, mimicking the SFTP panel UX.
/// </summary>
public partial class LocalFileBrowserView : UserControl
{
    private const double FileListWidthPadding = 10;
    private const double MinimumNameColumnWidth = 200;

    // The arrows appended to the header of the column the list is sorted by.
    private const string SortArrowAscending = " \u25B2";
    private const string SortArrowDescending = " \u25BC";

    /// <summary>Host of the shell "Open with" dialog.</summary>
    internal const string RunDllExecutableName = "rundll32.exe";

    /// <summary>Entry point of the shell "Open with" dialog inside rundll32.</summary>
    private const string OpenAsRunDllEntryPoint = "shell32.dll,OpenAs_RunDLL";

    /// <summary>File-manager image; it sits in the Windows directory, not in System32.</summary>
    internal const string ExplorerExecutableName = "explorer.exe";

    private readonly LocalizationManager? _localizer;
    private readonly LocalFileBrowserViewModel _viewModel;
    private readonly FileListDragSource? _dragSource;
    private System.Windows.Controls.ListViewItem? _highlightedDropRow;

    /// <summary>
    /// Raised when the user requests navigation to a directory path in the terminal.
    /// The subscriber should send a cd command to the active shell session.
    /// </summary>
    public event Action<string>? NavigateToPathRequested
    {
        add => _viewModel.NavigateToPathRequested += value;
        remove => _viewModel.NavigateToPathRequested -= value;
    }

    /// <summary>
    /// Raised when the user requests execution of a script file in the terminal.
    /// The subscriber should send the appropriate run command to the active shell session.
    /// </summary>
    public event Action<string>? RunInShellRequested
    {
        add => _viewModel.RunInShellRequested += value;
        remove => _viewModel.RunInShellRequested -= value;
    }

    /// <summary>
    /// Raised when the user wants to edit a file in the embedded editor.
    /// </summary>
    public event Action<string>? EditInEditorRequested
    {
        add => _viewModel.EditInEditorRequested += value;
        remove => _viewModel.EditInEditorRequested -= value;
    }

    /// <summary>
    /// Refreshes the current directory listing.
    /// </summary>
    public void RefreshCurrentDirectory() => _ = _viewModel.Refresh();

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalFileBrowserView"/> class using the user profile as the start path.
    /// </summary>
    public LocalFileBrowserView()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalFileBrowserView"/> class.
    /// </summary>
    /// <param name="startPath">The initial directory shown in the browser.</param>
    /// <param name="localizer">Optional localization manager used for context menu strings and dialogs.</param>
    /// <param name="editorPath">Optional external editor path used by Open in editor.</param>
    public LocalFileBrowserView(string startPath, LocalizationManager? localizer = null, string? editorPath = null)
    {
        _localizer = localizer;
        _viewModel = new LocalFileBrowserViewModel(startPath, localizer, editorPath);

        InitializeComponent();
        DataContext = _viewModel;
        _dragSource = new FileListDragSource(
            FileListView,
            point => HitTestRow(point),
            () => true,
            StartLocalDrag);
        ApplyLocalization();
        UpdateColumnHeaders();
        Loaded += OnViewLoaded;
    }

    /// <summary>
    /// Applies localized strings to the filter placeholder and context menu items.
    /// </summary>
    private void ApplyLocalization()
    {
        FilterTextBox.Tag = L10n("FileBrowserFilterPlaceholder");

        CtxOpen.Header = L10n("FileBrowserCtxOpen");
        CtxOpenWith.Header = L10n("FileBrowserCtxOpenWith");
        CtxOpenInExplorer.Header = L10n("FileBrowserCtxOpenInExplorer");
        CtxOpenInTerminal.Header = L10n("FileBrowserCtxOpenInTerminal");
        CtxOpenInEditor.Header = L10n("FileBrowserCtxOpenInEditor");
        CtxRunInShell.Header = L10n("FileBrowserCtxRunInShell");
        CtxCopy.Header = L10n("FileBrowserCtxCopy");
        CtxPaste.Header = L10n("FileBrowserCtxPaste");
        CtxCopyPath.Header = L10n("FileBrowserCtxCopyPath");
        CtxRename.Header = L10n("FileBrowserCtxRename");
        CtxDelete.Header = L10n("FileBrowserCtxDelete");
        CtxNewFolder.Header = L10n("FileBrowserCtxNewFolder");
        CtxProperties.Header = L10n("FileBrowserCtxProperties");
        CtxRefresh.Header = L10n("FileBrowserCtxRefresh");
    }

    /// <summary>
    /// Resolves a localized string by key, falling back to the key itself when no localizer is available.
    /// </summary>
    private string L10n(string key) => _localizer?.GetString(key) ?? key;

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.SetDialogService((Application.Current as App)?.Services?.GetService<IDialogService>());
    }

    private void OnFileListSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FileListView?.View is not GridView gridView || gridView.Columns.Count < 3)
        {
            return;
        }

        double fixedWidth = 0;
        for (int i = 1; i < gridView.Columns.Count; i++)
        {
            fixedWidth += gridView.Columns[i].ActualWidth;
        }

        double available = FileListView.ActualWidth
            - fixedWidth
            - SystemParameters.VerticalScrollBarWidth
            - FileListWidthPadding;

        if (available > MinimumNameColumnWidth)
        {
            gridView.Columns[0].Width = available;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // The keys that act on the selection are honoured only while the focus is in the list. The
        // handler listens in the tunnelling phase, so without that test Enter on the Back button
        // opened the selected file before the button saw the key.
        FileBrowserShortcut shortcut = FileBrowserShortcutPolicy.Resolve(
            e.Key == Key.System ? e.SystemKey : e.Key,
            Keyboard.Modifiers,
            inlineEditorOpen: false,
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase,
            FileListView.IsKeyboardFocusWithin);

        switch (shortcut)
        {
            case FileBrowserShortcut.Refresh:
                _ = _viewModel.Refresh();
                break;

            case FileBrowserShortcut.FocusFilter:
                FilterTextBox.Focus();
                FilterTextBox.SelectAll();
                break;

            case FileBrowserShortcut.FocusPath:
                PathTextBox.Focus();
                PathTextBox.SelectAll();
                break;

            case FileBrowserShortcut.NavigateBack:
                _ = _viewModel.NavigateBack();
                break;

            case FileBrowserShortcut.ParentFolder:
                _ = _viewModel.NavigateUp();
                break;

            case FileBrowserShortcut.NewFolder:
                OnCtxNewFolder(sender, e);
                break;

            case FileBrowserShortcut.Rename:
                OnCtxRename(sender, e);
                break;

            case FileBrowserShortcut.Delete:
                OnCtxDelete(sender, e);
                break;

            case FileBrowserShortcut.Open:
                OpenSelectedEntry();
                break;

            case FileBrowserShortcut.Copy:
                OnCtxCopy(sender, e);
                break;

            case FileBrowserShortcut.Paste:
                OnCtxPaste(sender, e);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Escape in the filter box clears it; a second Escape hands the keyboard back to the list.</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (FilterTextBox.Text.Length > 0)
        {
            _viewModel.FilterText = string.Empty;
        }
        else
        {
            FileListView.Focus();
        }

        e.Handled = true;
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header || header.Column is null)
        {
            return;
        }

        string key = SftpColumn.GetKey(header.Column);
        if (key.Length == 0)
        {
            return;
        }

        _viewModel.ToggleSortColumn(key);
        UpdateColumnHeaders();
    }

    private void UpdateColumnHeaders()
    {
        if (FileListView?.View is not GridView gridView)
        {
            return;
        }

        string arrow = _viewModel.SortDirection == System.ComponentModel.ListSortDirection.Ascending
            ? SortArrowAscending
            : SortArrowDescending;

        foreach (GridViewColumn column in gridView.Columns)
        {
            string key = SftpColumn.GetKey(column);
            if (key.Length == 0)
            {
                continue;
            }

            string baseName = L10n($"LocalFileBrowserCol{key}");
            column.Header = string.Equals(key, _viewModel.SortColumn, StringComparison.Ordinal)
                ? baseName + arrow
                : baseName;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.NavigateBack();
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.NavigateUp();
    }

    private void OnHomeClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.NavigateHome();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = _viewModel.Refresh();
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = _viewModel.NavigateToPath(PathTextBox.Text?.Trim());
            e.Handled = true;
        }
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelectedEntry();
    }

    private void OpenSelectedEntry()
    {
        if (FileListView.SelectedItem is not LocalFileEntry entry)
        {
            return;
        }

        if (_viewModel.HandleFileDoubleClick(entry))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = entry.FullPath,
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] file open: {ex.Message}");
            _viewModel.ReportOpenFailure(entry.Name, ex.Message);
        }
    }

    private void OnCtxOpen(object sender, RoutedEventArgs e)
    {
        OpenSelectedEntry();
    }

    private void OnCtxOpenWith(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not LocalFileEntry entry || entry.IsDirectory)
        {
            return;
        }

        try
        {
            Process.Start(CreateOpenWithStartInfo(entry.FullPath))?.Dispose();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"Open With failed: {ex.Message}");
        }
    }

    private void OnCtxOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is LocalFileEntry entry)
        {
            try
            {
                // Windows paths cannot contain double quotes, CR, or LF, so literal quoting is safe here.
                if (!entry.IsDirectory)
                {
                    Process.Start(CreateExplorerRevealStartInfo(entry.FullPath))?.Dispose();
                }
                else
                {
                    Process.Start(CreateExplorerBrowseStartInfo(entry.FullPath))?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] open in Explorer: {ex.Message}");
            }
        }
        else
        {
            try
            {
                Process.Start(CreateExplorerBrowseStartInfo(_viewModel.CurrentPath))?.Dispose();
            }
            catch (Exception ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] open Explorer: {ex.Message}");
            }
        }
    }

    private void OnCtxOpenInTerminal(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is LocalFileEntry { IsDirectory: true } entry)
        {
            _viewModel.InvokeNavigateToPath(entry.FullPath);
        }
    }

    private void OnCtxOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not LocalFileEntry entry || entry.IsDirectory)
        {
            return;
        }

        if (!TryCreateEditorStartInfo(_viewModel.EditorPath, entry.FullPath, out var processStartInfo, out var rejectionKey))
        {
            ShowEditorLaunchWarning(L10n(rejectionKey!));
            return;
        }

        try
        {
            Process.Start(processStartInfo!)?.Dispose();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"Failed to open editor: {ex.Message}");
        }
    }

    private void OnCtxRunInShell(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not LocalFileEntry entry || entry.IsDirectory)
        {
            return;
        }

        _viewModel.InvokeRunInShell(entry.FullPath);
    }

    private void OnCtxCopy(object sender, RoutedEventArgs e)
    {
        var selected = FileListView.SelectedItems.Cast<LocalFileEntry>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var fileList = new StringCollection();
        foreach (var entry in selected)
        {
            fileList.Add(entry.FullPath);
        }

        Clipboard.SetFileDropList(fileList);
    }

    private async void OnCtxPaste(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsFileDropList())
            {
                return;
            }

            StringCollection fileList = Clipboard.GetFileDropList();
            if (fileList is null)
            {
                return;
            }

            await _viewModel.PasteFilesAsync(fileList.Cast<string>().ToList());
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[LocalFileBrowser] paste handler failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the start info for the shell "Open with" dialog.
    /// Windows paths cannot contain double quotes, CR, or LF, so literal quoting is safe here.
    /// </summary>
    internal static ProcessStartInfo CreateOpenWithStartInfo(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InSystemDirectory(RunDllExecutableName),
            Arguments = $"{OpenAsRunDllEntryPoint} \"{fullPath}\"",
            UseShellExecute = false,
            WorkingDirectory = SystemExecutablePath.SystemDirectory
        };
    }

    /// <summary>
    /// Builds the start info that opens the file manager with the entry selected.
    /// Windows paths cannot contain double quotes, CR, or LF, so literal quoting is safe here.
    /// </summary>
    internal static ProcessStartInfo CreateExplorerRevealStartInfo(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InWindowsDirectory(ExplorerExecutableName),
            Arguments = $"/select,\"{fullPath}\"",
            WorkingDirectory = SystemExecutablePath.WindowsDirectory
        };
    }

    /// <summary>
    /// Builds the start info that opens the file manager on a directory.
    /// Windows paths cannot contain double quotes, CR, or LF, so literal quoting is safe here.
    /// </summary>
    internal static ProcessStartInfo CreateExplorerBrowseStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new ProcessStartInfo
        {
            FileName = SystemExecutablePath.InWindowsDirectory(ExplorerExecutableName),
            Arguments = $"\"{path}\"",
            WorkingDirectory = SystemExecutablePath.WindowsDirectory
        };
    }

    // The policy is shared with the remote browser; these stay as the names the callers use.
    internal static bool TryCreateEditorStartInfo(
        string? configuredEditorPath,
        string filePath,
        out ProcessStartInfo? processStartInfo,
        out string? rejectionKey)
        => EditorLaunchPolicy.TryCreateEditorStartInfo(
            configuredEditorPath,
            filePath,
            out processStartInfo,
            out rejectionKey);

    internal static string ResolveEditorPath(string? configuredEditorPath)
        => EditorLaunchPolicy.ResolveEditorPath(configuredEditorPath);

    internal static ProcessStartInfo CreateEditorStartInfo(string editorPath, string filePath)
        => EditorLaunchPolicy.CreateEditorStartInfo(editorPath, filePath);

    private void ShowEditorLaunchWarning(string message)
    {
        var dialogService = (Application.Current as App)?.Services?.GetService<IDialogService>();
        if (dialogService is not null)
        {
            dialogService.ShowWarning(L10n("FileBrowserCtxOpenInEditor"), message);
            return;
        }

        Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] editor launch rejected: {message}");
    }

    private void OnCtxCopyPath(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is LocalFileEntry entry)
        {
            Clipboard.SetText(entry.FullPath);
        }
    }

    private void OnFileListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => ListViewContextMenuHelper.SelectRowOnRightClick(sender, e);

    // ------------------------------------------------------------------
    // Drag and drop between this list and a remote one
    // ------------------------------------------------------------------

    private void OnFileListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragSource?.OnPreviewMouseLeftButtonDown(e);

    private void OnFileListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragSource?.OnPreviewMouseLeftButtonUp(e);

    private void OnFileListPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        => _dragSource?.OnPreviewMouseMove(e);

    /// <summary>Drags the selected files out as real files, so a remote list (or Explorer) can take them.</summary>
    private void StartLocalDrag(IReadOnlyList<object> rows)
    {
        string[] paths = rows.OfType<LocalFileEntry>().Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        System.Windows.DataObject data = new();
        data.SetData(System.Windows.DataFormats.FileDrop, paths);
        try
        {
            System.Windows.DragDrop.DoDragDrop(FileListView, data, System.Windows.DragDropEffects.Copy);
        }
        finally
        {
            SetDropHighlight(null);
        }
    }

    /// <summary>
    /// Only entries dragged out of a remote list are accepted: they are downloaded into the folder
    /// under the pointer, or into the one shown.
    /// </summary>
    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (SftpRemoteDragPayload.From(e.Data) is null)
        {
            SetDropHighlight(null);
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        LocalFileEntry? hovered = HitTestRow(e.GetPosition(FileListView));
        SetDropHighlight(hovered is { IsDirectory: true } ? hovered : null);
        e.Effects = System.Windows.DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, System.Windows.DragEventArgs e) => SetDropHighlight(null);

    private async void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        SetDropHighlight(null);
        if (SftpRemoteDragPayload.From(e.Data) is not { } payload)
        {
            return;
        }

        LocalFileEntry? hovered = HitTestRow(e.GetPosition(FileListView));
        string targetFolder = hovered is { IsDirectory: true } ? hovered.FullPath : _viewModel.CurrentPath;
        try
        {
            await payload.Source.DownloadFilesAsync(payload.Entries, targetFolder);
            await _viewModel.Refresh();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[LocalFileBrowser] drop download failed: {ex.Message}");
        }
    }

    private LocalFileEntry? HitTestRow(System.Windows.Point point)
    {
        if (FileListView.InputHitTest(point) is not System.Windows.DependencyObject hit)
        {
            return null;
        }

        System.Windows.DependencyObject? container = ItemsControl.ContainerFromElement(FileListView, hit);
        return (container as System.Windows.Controls.ListViewItem)?.DataContext as LocalFileEntry;
    }

    private void SetDropHighlight(LocalFileEntry? folder)
    {
        System.Windows.Controls.ListViewItem? container = folder is null
            ? null
            : FileListView.ItemContainerGenerator.ContainerFromItem(folder) as System.Windows.Controls.ListViewItem;
        if (ReferenceEquals(container, _highlightedDropRow))
        {
            return;
        }

        if (_highlightedDropRow is not null)
        {
            SftpDropTarget.SetIsDropTarget(_highlightedDropRow, false);
        }

        _highlightedDropRow = container;
        if (container is not null)
        {
            SftpDropTarget.SetIsDropTarget(container, true);
        }
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var hasSelection = FileListView.SelectedItems.Count > 0;
        var singleSelection = FileListView.SelectedItems.Count == 1;
        var isDirectory = singleSelection && FileListView.SelectedItem is LocalFileEntry { IsDirectory: true };
        var isFile = singleSelection && FileListView.SelectedItem is LocalFileEntry { IsDirectory: false };
        var isRunnable = isFile && FileListView.SelectedItem is LocalFileEntry entry
            && _viewModel.IsRunnableFile(entry.Name);

        CtxOpen.IsEnabled = hasSelection;
        CtxOpenWith.IsEnabled = isFile;
        CtxOpenWith.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        CtxOpenInExplorer.IsEnabled = true;
        CtxOpenInEditor.IsEnabled = isFile;
        CtxOpenInEditor.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        CtxRunInShell.IsEnabled = isRunnable;
        CtxRunInShell.Visibility = isRunnable ? Visibility.Visible : Visibility.Collapsed;
        CtxCopy.IsEnabled = hasSelection;
        CtxPaste.IsEnabled = Clipboard.ContainsFileDropList();
        CtxCopyPath.IsEnabled = hasSelection;
        CtxDelete.IsEnabled = hasSelection;
        CtxRename.IsEnabled = singleSelection;
        CtxOpenInTerminal.IsEnabled = isDirectory;
        CtxNewFolder.IsEnabled = true;
        CtxProperties.IsEnabled = singleSelection;
        CtxRefresh.IsEnabled = true;
    }

    private async void OnCtxDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            List<LocalFileEntry> selected = FileListView.SelectedItems.Cast<LocalFileEntry>().ToList();
            if (selected.Count == 0)
            {
                return;
            }

            await _viewModel.DeleteEntriesAsync(selected);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[LocalFileBrowser] delete handler failed: {ex.Message}");
        }
    }

    private async void OnCtxRename(object sender, RoutedEventArgs e)
    {
        try
        {
            if (FileListView.SelectedItem is not LocalFileEntry entry)
            {
                return;
            }

            await _viewModel.RenameEntryAsync(entry);
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[LocalFileBrowser] rename handler failed: {ex.Message}");
        }
    }

    private async void OnCtxNewFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.CreateFolderAsync();
        }
        catch (Exception ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"[LocalFileBrowser] new folder handler failed: {ex.Message}");
        }
    }

    private void OnCtxProperties(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is not LocalFileEntry entry)
        {
            return;
        }

        _viewModel.ShowProperties(entry);
    }
}
