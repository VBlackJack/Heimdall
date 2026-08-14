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

using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using Heimdall.App.Controls;

namespace Heimdall.App.Automation;

/// <summary>
/// Reports a session-tree row's selection from the view model rather than from the native
/// <see cref="System.Windows.Controls.TreeViewItem.IsSelected"/> flag, which the tree clears on
/// purpose so WPF's single-selection does not fight the multi-selection the view model holds.
/// </summary>
/// <remarks>
/// <see cref="ISelectionItemProvider"/> is re-declared rather than inherited: the base peer
/// implements it explicitly and non-virtually, so the only way to answer differently is to
/// re-implement the interface on this type and hand it back from
/// <see cref="GetPattern(PatternInterface)"/>.
/// </remarks>
public class SessionTreeViewItemAutomationPeer : TreeViewItemAutomationPeer, ISelectionItemProvider
{
    private INotifyPropertyChanged? _observedItem;

    public SessionTreeViewItemAutomationPeer(SessionTreeViewItem owner)
        : base(owner)
    {
        owner.DataContextChanged += OnOwnerDataContextChanged;
        Observe(owner.DataContext);
    }

    /// <summary>The bound item this row currently displays; null once recycled away from one.</summary>
    public object? BoundItem => (Owner as SessionTreeViewItem)?.DataContext;

    /// <summary>Whether the selection host counts this row's item as selected.</summary>
    public bool IsItemSelected => FindSelectionHost() is { } host && host.IsItemSelected(BoundItem);

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.SelectionItem
            ? this
            : base.GetPattern(patternInterface);

    bool ISelectionItemProvider.IsSelected => IsItemSelected;

    IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer
    {
        get
        {
            SessionTreeView? tree = FindOwningTree();
            return tree is null ? null : ProviderFromPeer(CreatePeerForElement(tree));
        }
    }

    void ISelectionItemProvider.Select() => RequireHost().SelectOnlyItem(BoundItem);

    void ISelectionItemProvider.AddToSelection() => RequireHost().AddItemToSelection(BoundItem);

    void ISelectionItemProvider.RemoveFromSelection() => RequireHost().RemoveItemFromSelection(BoundItem);

    /// <summary>
    /// Re-points the peer at the item a recycled container now shows.
    /// </summary>
    /// <remarks>
    /// The tree virtualizes with <c>VirtualizationMode.Recycling</c>, so one container - and
    /// therefore one peer - serves many items over its life. Without this, the peer would keep
    /// answering for the item the row displayed when it was first realized.
    /// </remarks>
    private void OnOwnerDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Observe(e.NewValue);

    private void Observe(object? item)
    {
        if (_observedItem is not null)
        {
            _observedItem.PropertyChanged -= OnItemPropertyChanged;
            _observedItem = null;
        }

        if (item is INotifyPropertyChanged notifier)
        {
            _observedItem = notifier;
            notifier.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, SelectionPropertyName, StringComparison.Ordinal))
        {
            return;
        }

        // Best effort: WPF drops the call when no UI Automation client is listening, which is the
        // normal case, so nothing downstream may depend on it having been delivered.
        bool selected = IsItemSelected;
        RaisePropertyChangedEvent(
            SelectionItemPatternIdentifiers.IsSelectedProperty,
            !selected,
            selected);
    }

    private ISessionTreeSelectionHost RequireHost()
        => FindSelectionHost()
            ?? throw new InvalidOperationException(
                "The session tree exposes no selection host, so selection cannot be changed.");

    private ISessionTreeSelectionHost? FindSelectionHost() => FindOwningTree()?.SelectionHost;

    private SessionTreeView? FindOwningTree()
    {
        DependencyObject? current = Owner;
        while (current is not null)
        {
            if (current is SessionTreeView tree)
            {
                return tree;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private const string SelectionPropertyName = "IsSelected";
}
