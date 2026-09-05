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
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Represents a folder node in the TreeView. Folders can contain sub-folders
/// and servers with unlimited nesting depth. Replaces the old Project + Group model.
/// </summary>
public partial class FolderViewModel : ObservableObject, IInlineRenameNode, IAccessibleItemViewModel
{
    private readonly LocalizationManager? _localizer;
    private bool _suppressChildInvalidation;

    public FolderViewModel(LocalizationManager? localizer = null)
    {
        _localizer = localizer;

        // The field initializers bypass the generated setters, so the collections a folder is
        // born with would otherwise never report their changes: a server added to a fresh
        // folder used to be picked up only because the projection was rebuilt on a count
        // mismatch at the next read.
        _subFolders.CollectionChanged += OnCollectionInvalidated;
        _servers.CollectionChanged += OnCollectionInvalidated;
    }

    [ObservableProperty]
    private string _name = "";

    /// <summary>Full path from root (e.g., "ADSEC/Gateway/Linux").</summary>
    [ObservableProperty]
    private string _fullPath = "";

    /// <summary>
    /// The path shown on hover, or <see langword="null"/> for the root "no group" folder.
    /// </summary>
    /// <remarks>
    /// WPF opens no tooltip for a null content but does open an empty one for an empty string,
    /// which is what the "no group" folder's empty <see cref="FullPath"/> produced.
    /// </remarks>
    public string? TooltipText => string.IsNullOrEmpty(FullPath) ? null : FullPath;

    /// <summary>Hex colour of the folder icon, or empty for the themed default.</summary>
    [ObservableProperty]
    private string _color = "";

    /// <summary>Whether the icon takes <see cref="Color"/> rather than the themed default.</summary>
    public bool HasColor => !string.IsNullOrWhiteSpace(Color);

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

    /// <summary>Localized UI Automation name that distinguishes a folder from a server.</summary>
    public string AccessibleName => Format("SessionTreeFolderAccessibleName", Name);

    /// <summary>Localized keyboard guidance exposed to UI Automation clients.</summary>
    public string AccessibleHelpText => Translate("SessionTreeFolderAccessibleHelp");

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
    {
        if (!_suppressChildInvalidation)
        {
            InvalidateChildren();
        }
    }

    private int? _serverCountCache;

    /// <summary>
    /// Combined collection for the TreeView: sub-folders first, then servers.
    /// WPF resolves the correct DataTemplate by type.
    /// </summary>
    /// <remarks>
    /// One instance for the folder's whole life, brought up to date in place by a diff. It used
    /// to be a fresh <c>ArrayList</c> after every invalidation, and a new <c>ItemsSource</c>
    /// instance is a Reset to the item container generator: every child container was discarded
    /// and regenerated on every filter pass, which threw keyboard focus out of the tree whenever
    /// a session crossed the Connected filter or a search keystroke landed. Measured on
    /// 2026-09-05: after one invalidation the container of the focused row was a different
    /// instance, the old one was gone from the visual tree, and the window had no focused
    /// element left.
    /// </remarks>
    public ObservableCollection<object> Children { get; } = [];

    /// <summary>
    /// Applies visible membership changes as an ordered diff while retaining
    /// this folder and its backing collection instances.
    /// </summary>
    public void SynchronizeVisibleChildren(
        IReadOnlyList<FolderViewModel> folders,
        IReadOnlyList<ServerItemViewModel> servers)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(servers);

        _suppressChildInvalidation = true;
        try
        {
            SynchronizeCollection(SubFolders, folders);
            SynchronizeCollection(Servers, servers);
        }
        finally
        {
            _suppressChildInvalidation = false;
        }

        // Always invalidate: a retained child folder may have changed its
        // descendant count without changing this folder's direct membership.
        InvalidateChildren();
    }

    /// <summary>
    /// Brings <see cref="Children"/> in line with the sub-collections and drops the
    /// <see cref="ServerCount"/> cache.
    /// </summary>
    public void InvalidateChildren()
    {
        _serverCountCache = null;
        SynchronizeChildrenProjection();
        OnPropertyChanged(nameof(ServerCount));
    }

    /// <summary>
    /// Diffs <see cref="Children"/> against sub-folders then servers, so the collection the tree
    /// is bound to is edited rather than replaced.
    /// </summary>
    private void SynchronizeChildrenProjection()
    {
        List<object> target = new(SubFolders.Count + Servers.Count);
        foreach (FolderViewModel folder in SubFolders)
        {
            target.Add(folder);
        }

        foreach (ServerItemViewModel server in Servers)
        {
            target.Add(server);
        }

        SynchronizeCollection(Children, target);
    }

    /// <summary>Total server count (direct + recursive). Cached; call InvalidateChildren() on structural changes.</summary>
    public int ServerCount =>
        _serverCountCache ??= Servers.Count + SubFolders.Sum(f => f.ServerCount);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    partial void OnFullPathChanged(string value) => OnPropertyChanged(nameof(TooltipText));

    partial void OnColorChanged(string value) => OnPropertyChanged(nameof(HasColor));

    internal void RefreshLocalizedState()
    {
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(AccessibleHelpText));
    }

    private static void SynchronizeCollection<T>(
        ObservableCollection<T> collection,
        IReadOnlyList<T> target)
        where T : class
    {
        for (var targetIndex = 0; targetIndex < target.Count; targetIndex++)
        {
            T targetItem = target[targetIndex];
            if (targetIndex < collection.Count
                && ReferenceEquals(collection[targetIndex], targetItem))
            {
                continue;
            }

            var existingIndex = -1;
            for (var index = targetIndex + 1; index < collection.Count; index++)
            {
                if (ReferenceEquals(collection[index], targetItem))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                collection.Move(existingIndex, targetIndex);
            }
            else
            {
                collection.Insert(targetIndex, targetItem);
            }
        }

        while (collection.Count > target.Count)
        {
            collection.RemoveAt(collection.Count - 1);
        }
    }

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

    private string Translate(string key) =>
        _localizer?.HasKey(key) == true ? _localizer[key] : Fallback(key);

    private string Format(string key, params object[] args)
    {
        if (_localizer?.HasKey(key) == true)
        {
            return _localizer.Format(key, args);
        }

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            Fallback(key),
            args);
    }

    private static string Fallback(string key) => key switch
    {
        "SessionTreeFolderAccessibleName" => "{0}, folder",
        "SessionTreeFolderAccessibleHelp" =>
            "Folder. Use Left and Right Arrow or Enter to collapse or expand. Press Shift+F10 for actions.",
        _ => key
    };
}
