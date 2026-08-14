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

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Heimdall.App.Automation;

namespace Heimdall.App.Controls;

/// <summary>
/// The selection model behind a session tree, as UI Automation needs to see it.
/// </summary>
/// <remarks>
/// Deliberately expressed in terms of bound items rather than of any view model type: the tree is
/// the only thing that knows about containers, and the host is the only thing that knows about
/// selection, so neither has to learn the other's vocabulary.
/// </remarks>
public interface ISessionTreeSelectionHost
{
    /// <summary>Whether <paramref name="item"/> is part of the current selection.</summary>
    bool IsItemSelected(object? item);

    /// <summary>Replaces the selection with <paramref name="item"/> alone.</summary>
    void SelectOnlyItem(object? item);

    /// <summary>Adds <paramref name="item"/> to the selection, leaving the rest in place.</summary>
    void AddItemToSelection(object? item);

    /// <summary>Removes <paramref name="item"/> from the selection, leaving the rest in place.</summary>
    void RemoveItemFromSelection(object? item);
}

/// <summary>
/// The session tree, which supports multi-selection and says so to UI Automation.
/// </summary>
/// <remarks>
/// A stock <see cref="TreeView"/> is single-select, and this tree deliberately clears the native
/// <see cref="TreeViewItem.IsSelected"/> flag so WPF's own selection does not fight the multi-
/// selection held in the view model. The side effect was that assistive technology, which reads
/// selection through the Selection and SelectionItem patterns, saw nothing selected no matter how
/// many rows the user had picked. Overriding the peers is the only way to correct that:
/// <see cref="UIElement.OnCreateAutomationPeer"/> is protected virtual, so there is no attached-
/// property route to it.
/// </remarks>
public class SessionTreeView : TreeView
{
    /// <summary>The selection model this tree reports to UI Automation clients.</summary>
    public static readonly DependencyProperty SelectionHostProperty =
        DependencyProperty.Register(
            nameof(SelectionHost),
            typeof(ISessionTreeSelectionHost),
            typeof(SessionTreeView),
            new PropertyMetadata(null));

    public ISessionTreeSelectionHost? SelectionHost
    {
        get => (ISessionTreeSelectionHost?)GetValue(SelectionHostProperty);
        set => SetValue(SelectionHostProperty, value);
    }

    protected override DependencyObject GetContainerForItemOverride() => new SessionTreeViewItem();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is SessionTreeViewItem;

    protected override AutomationPeer OnCreateAutomationPeer() => new SessionTreeViewAutomationPeer(this);
}
