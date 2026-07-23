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

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Represents a folder node in the TreeView. Folders can contain sub-folders
/// and servers with unlimited nesting depth. Replaces the old Project + Group model.
/// </summary>
public partial class FolderViewModel : ObservableObject, IInlineRenameNode
{
    [ObservableProperty]
    private string _name = "";

    /// <summary>Full path from root (e.g., "ADSEC/Gateway/Linux").</summary>
    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private string _color = "";

    /// <summary>
    /// Whether this folder is expanded in the TreeView.
    /// Bound TwoWay to TreeViewItem.IsExpanded for state persistence.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editName = "";

    /// <summary>
    /// Stable key for expand/collapse state persistence.
    /// Uses FullPath for named folders, sentinel for the root "no group" folder.
    /// </summary>
    public string ExpansionKey =>
        string.IsNullOrEmpty(FullPath) ? "::nogroup" : FullPath;

    [ObservableProperty]
    private ObservableCollection<FolderViewModel> _subFolders = [];

    [ObservableProperty]
    private ObservableCollection<ServerItemViewModel> _servers = [];

    partial void OnSubFoldersChanged(
        ObservableCollection<FolderViewModel>? oldValue,
        ObservableCollection<FolderViewModel> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnCollectionInvalidated;
        newValue.CollectionChanged += OnCollectionInvalidated;
        InvalidateChildren();
    }

    partial void OnServersChanged(
        ObservableCollection<ServerItemViewModel>? oldValue,
        ObservableCollection<ServerItemViewModel> newValue)
    {
        if (oldValue is not null)
            oldValue.CollectionChanged -= OnCollectionInvalidated;
        newValue.CollectionChanged += OnCollectionInvalidated;
        InvalidateChildren();
    }

    /// <summary>
    /// Shared handler for <see cref="INotifyCollectionChanged.CollectionChanged"/> on
    /// the <see cref="SubFolders"/> and <see cref="Servers"/> backing collections.
    /// A single named method (instead of per-assignment lambdas) allows symmetric
    /// detach in the partial setter below, preventing handler accumulation when a
    /// collection reference is replaced.
    /// </summary>
    private void OnCollectionInvalidated(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateChildren();

    private ArrayList? _childrenCache;
    private int? _serverCountCache;

    /// <summary>
    /// Combined collection for the TreeView: sub-folders first, then servers.
    /// WPF resolves the correct DataTemplate by type.
    /// Cached to avoid re-allocation on each access (perf with 768+ items).
    /// </summary>
    public IList Children
    {
        get
        {
            if (_childrenCache is null || _childrenCache.Count != SubFolders.Count + Servers.Count)
            {
                _childrenCache = new ArrayList(SubFolders.Count + Servers.Count);
                foreach (var f in SubFolders) _childrenCache.Add(f);
                foreach (var s in Servers) _childrenCache.Add(s);
            }

            return _childrenCache;
        }
    }

    /// <summary>Invalidate the Children and ServerCount caches when sub-collections change.</summary>
    public void InvalidateChildren()
    {
        _childrenCache = null;
        _serverCountCache = null;
    }

    /// <summary>Total server count (direct + recursive). Cached; call InvalidateChildren() on structural changes.</summary>
    public int ServerCount =>
        _serverCountCache ??= Servers.Count + SubFolders.Sum(f => f.ServerCount);

    /// <inheritdoc />
    public void BeginInlineEdit()
    {
        EditName = Name;
        IsEditing = true;
    }

    /// <inheritdoc />
    public void CancelInlineEdit()
    {
        EditName = Name;
        IsEditing = false;
    }

    /// <inheritdoc />
    public void CompleteInlineEdit()
    {
        EditName = Name;
        IsEditing = false;
    }
}
