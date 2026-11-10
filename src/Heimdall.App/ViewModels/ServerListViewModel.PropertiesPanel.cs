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

namespace Heimdall.App.ViewModels;

/// <summary>
/// What the properties panel under the tree shows: one session, a summary of several, or
/// nothing.
/// </summary>
/// <remarks>
/// A folder is never selected; the tree marks the folder under its cursor instead. The panel
/// follows whichever the user touched last: the window hands the cursor folder over when the
/// cursor moves onto it or the folder is clicked, and any selection the user makes takes it back,
/// so picking a session from the palette or a search is not hidden behind a folder the cursor
/// left on the screen. The folder is followed by its key, not by its row: a reload builds new
/// rows, and a search can hide the folder and show it again.
/// </remarks>
public partial class ServerListViewModel
{
    private string? _propertiesFolderKey;
    private FolderViewModel? _propertiesFolder;
    private PropertiesSummaryViewModel? _propertiesSummary;

    /// <summary>
    /// The folder the panel summarizes, as it is on screen now; null when the panel follows the
    /// selection, or while a search hides the folder.
    /// </summary>
    /// <remarks>
    /// Setting a folder hands the panel to it; setting null hands the panel back to the selection.
    /// </remarks>
    public FolderViewModel? PropertiesFolder
    {
        get => _propertiesFolder;
        set
        {
            string? key = value?.ExpansionKey;
            if (string.Equals(_propertiesFolderKey, key, StringComparison.Ordinal)
                && ReferenceEquals(_propertiesFolder, value))
            {
                return;
            }

            _propertiesFolderKey = key;
            RefreshPropertiesSummary();
        }
    }

    /// <summary>
    /// The summary the panel shows for a folder or a multi-selection; null when it shows one
    /// session or nothing.
    /// </summary>
    public PropertiesSummaryViewModel? PropertiesSummary => _propertiesSummary;

    /// <summary>Whether the panel shows a summary.</summary>
    public bool PropertiesPanelShowsSummary => _propertiesSummary is not null;

    /// <summary>Whether the panel shows the properties of the selected session.</summary>
    public bool PropertiesPanelShowsSession => _propertiesSummary is null && ShowSessionDetail;

    /// <summary>Whether the panel shows its hint that nothing is selected.</summary>
    public bool PropertiesPanelShowsEmpty => _propertiesSummary is null && SelectedServer is null;

    /// <summary>Whether the panel says the selected row is a tool, which has no session properties.</summary>
    public bool PropertiesPanelShowsTool => _propertiesSummary is null && ShowToolDetail;

    /// <summary>
    /// Rebuilds the summary from the current folder or selection, and tells the panel.
    /// </summary>
    /// <remarks>
    /// Called when the folder, the selection, the visible tree, a session state or the language
    /// changes. The summary is a snapshot, so every one of those has to come through here.
    /// </remarks>
    internal void RefreshPropertiesSummary()
    {
        FolderViewModel? folder = _propertiesFolderKey is { } key
            ? FindFolderOnScreen(GroupedServers, key)
            : null;
        if (!ReferenceEquals(folder, _propertiesFolder))
        {
            _propertiesFolder = folder;
            OnPropertyChanged(nameof(PropertiesFolder));
        }

        PropertiesSummaryViewModel? summary = null;
        if (folder is not null)
        {
            summary = PropertiesSummaryViewModel.Build(
                folder.Name,
                folder.FullPath,
                EnumerateFolderSessions(folder));
        }
        else if (HasMultiSelection)
        {
            summary = PropertiesSummaryViewModel.Build(
                SelectionCountText,
                "",
                SelectedItems);
        }

        if (summary is null && _propertiesSummary is null)
        {
            return;
        }

        _propertiesSummary = summary;
        OnPropertyChanged(nameof(PropertiesSummary));
        NotifyPropertiesPanelMode();
    }

    private void NotifyPropertiesPanelMode()
    {
        OnPropertyChanged(nameof(PropertiesPanelShowsSummary));
        OnPropertyChanged(nameof(PropertiesPanelShowsSession));
        OnPropertyChanged(nameof(PropertiesPanelShowsEmpty));
        OnPropertyChanged(nameof(PropertiesPanelShowsTool));
        OnPropertyChanged(nameof(PropertiesPanelShowsEditor));
        OnPropertyChanged(nameof(PropertiesPanelShowsActions));
        OnPropertyChanged(nameof(PropertiesPanelCanEditHere));
        BeginPropertiesEditCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// The selection moved: the panel follows it rather than a folder the cursor left behind.
    /// </summary>
    private void OnPropertiesPanelSelectionChanged()
    {
        // A filter or a collapse moving the selection is the view, not the user moving on.
        if (!_synchronizingSelection)
        {
            _propertiesFolderKey = null;
        }

        RefreshPropertiesSummary();
        ResolvePropertiesEditOnSelectionChange();
        NotifyPropertiesPanelMode();
    }

    private static FolderViewModel? FindFolderOnScreen(IEnumerable<FolderViewModel> folders, string key)
    {
        foreach (FolderViewModel folder in folders)
        {
            if (string.Equals(folder.ExpansionKey, key, StringComparison.Ordinal))
            {
                return folder;
            }

            if (FindFolderOnScreen(folder.SubFolders, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The sessions a folder holds on screen, its subfolders included.</summary>
    private static IEnumerable<ServerItemViewModel> EnumerateFolderSessions(FolderViewModel folder)
    {
        foreach (ServerItemViewModel server in folder.Servers)
        {
            yield return server;
        }

        foreach (FolderViewModel subFolder in folder.SubFolders)
        {
            foreach (ServerItemViewModel server in EnumerateFolderSessions(subFolder))
            {
                yield return server;
            }
        }
    }
}
