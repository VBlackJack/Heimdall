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

using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Heimdall.App.Controls;

namespace Heimdall.App.Automation;

/// <summary>
/// Declares the session tree as a multi-selection container and enumerates what is selected,
/// reading the view model's selection instead of WPF's single-selection state.
/// </summary>
/// <remarks>
/// <see cref="ISelectionProvider"/> is re-declared rather than inherited: the base peer implements
/// it explicitly and non-virtually, and answers <c>CanSelectMultiple = false</c>. Assistive
/// technology reads that first and then stops looking for more than one selected row, so the
/// multi-selection was invisible however many rows the user had picked.
/// </remarks>
public class SessionTreeViewAutomationPeer : TreeViewAutomationPeer, ISelectionProvider
{
    public SessionTreeViewAutomationPeer(SessionTreeView owner)
        : base(owner)
    {
    }

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Selection
            ? this
            : base.GetPattern(patternInterface);

    bool ISelectionProvider.CanSelectMultiple => true;

    bool ISelectionProvider.IsSelectionRequired => false;

    IRawElementProviderSimple[] ISelectionProvider.GetSelection()
        => [.. SelectedPeers()
            .Select(ClientVisiblePeerFor)
            .Select(ProviderFromPeer)
            .OfType<IRawElementProviderSimple>()];

    /// <summary>The peer a UI Automation client can resolve for a realized row.</summary>
    /// <remarks>
    /// An items control does not put container peers in the automation tree. It puts one data-item
    /// peer per item and hangs the container peer off it as
    /// <see cref="AutomationPeer.EventsSource"/>, so a container peer never gets an hwnd and
    /// <c>ProviderFromPeer</c> answers null for it - silently. Every selected row was dropped on
    /// the way out and the array came back empty however many rows were selected, which is the
    /// same nothing the defect this peer exists to fix used to report.
    /// </remarks>
    private static AutomationPeer ClientVisiblePeerFor(AutomationPeer peer) => peer.EventsSource ?? peer;

    /// <summary>Every realized row peer the selection host counts as selected, in tree order.</summary>
    /// <remarks>
    /// It walks the realized CONTAINERS rather than <see cref="AutomationPeer.GetChildren"/>: the
    /// base peer yields data-item peers that wrap the container peers rather than the container
    /// peers themselves, so a walk of the peer tree finds none of them.
    /// <para>
    /// Only realized rows can appear. A virtualized-away row has no container and therefore no
    /// peer, and UI Automation has no way to name an element that does not exist - that is the
    /// documented behaviour of a virtualizing container, not a gap here.
    /// </para>
    /// </remarks>
    public IEnumerable<SessionTreeViewItemAutomationPeer> SelectedPeers()
        => RealizedContainers((ItemsControl)Owner)
            .Select(UIElementAutomationPeer.CreatePeerForElement)
            .OfType<SessionTreeViewItemAutomationPeer>()
            .Where(peer => peer.IsItemSelected);

    private static IEnumerable<SessionTreeViewItem> RealizedContainers(ItemsControl root)
    {
        for (int index = 0; index < root.Items.Count; index++)
        {
            if (root.ItemContainerGenerator.ContainerFromIndex(index) is not SessionTreeViewItem container)
            {
                continue;
            }

            yield return container;

            foreach (SessionTreeViewItem nested in RealizedContainers(container))
            {
                yield return nested;
            }
        }
    }
}
