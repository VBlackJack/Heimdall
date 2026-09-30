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
using System.ComponentModel;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The sessions tree's selection: an observable list that answers membership in constant time
/// and can be replaced wholesale with a single notification.
/// </summary>
/// <remarks>
/// The selection used to be rebuilt one <c>Add</c> at a time, and every <c>Add</c> raised a
/// collection change, which recomputed the count, the live-region text and five command states.
/// Ctrl+A over a few hundred sessions therefore announced the count a few hundred times to a
/// screen reader, and every membership test on the way was a linear scan. The whole set now
/// arrives in one <see cref="NotifyCollectionChangedAction.Reset"/>.
/// </remarks>
public sealed class SessionSelectionCollection : ObservableCollection<ServerItemViewModel>
{
    private readonly HashSet<ServerItemViewModel> _members = new(ReferenceEqualityComparer.Instance);

    /// <summary>Reports whether a session belongs to the selection, in constant time.</summary>
    /// <param name="item">The session to look up.</param>
    /// <returns><see langword="true"/> when the session is selected.</returns>
    public new bool Contains(ServerItemViewModel item) => item is not null && _members.Contains(item);

    /// <summary>
    /// Replaces every member with <paramref name="items"/> and raises one reset notification.
    /// </summary>
    /// <param name="items">The new members, already free of duplicates, in display order.</param>
    public void ReplaceAll(IReadOnlyList<ServerItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();

        Items.Clear();
        _members.Clear();
        foreach (ServerItemViewModel item in items)
        {
            Items.Add(item);
            _members.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <inheritdoc />
    protected override void ClearItems()
    {
        _members.Clear();
        base.ClearItems();
    }

    /// <inheritdoc />
    protected override void InsertItem(int index, ServerItemViewModel item)
    {
        base.InsertItem(index, item);
        _members.Add(item);
    }

    /// <inheritdoc />
    protected override void RemoveItem(int index)
    {
        ServerItemViewModel removed = this[index];
        base.RemoveItem(index);
        if (!Items.Contains(removed))
        {
            _members.Remove(removed);
        }
    }

    /// <inheritdoc />
    protected override void SetItem(int index, ServerItemViewModel item)
    {
        ServerItemViewModel replaced = this[index];
        base.SetItem(index, item);
        if (!Items.Contains(replaced))
        {
            _members.Remove(replaced);
        }

        _members.Add(item);
    }
}
