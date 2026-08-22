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
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using FlaUI.UIA3;
using Heimdall.App.Automation;
using Heimdall.App.Controls;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

/// <summary>
/// The session tree holds its multi-selection in the view model and clears the native
/// <see cref="TreeViewItem.IsSelected"/> flag on purpose, so WPF's single-selection does not fight
/// it. Assistive technology reads selection through the Selection and SelectionItem patterns, and
/// those used to answer from the native flag - which meant they reported nothing selected however
/// many rows the user had picked. These tests pin the patterns to the view model instead.
/// </summary>
/// <remarks>
/// They run in the BLOCKING lane: an offscreen window on the shared STA host is enough to realize
/// containers and build peers, and no interactive desktop is involved. The FlaUI test at the end
/// of this file is the complementary end-to-end proof through the real UI Automation stack, and it
/// carries the RequiresDesktop trait because it needs a live window handle.
/// </remarks>
public sealed class SessionTreeSelectionAutomationTests
{
    [StaFact]
    public void SessionTree_ReportsItselfAsAMultiSelectionContainer()
    {
        FakeSelectionHost host = new();
        SessionTreeView tree = CreateTree(host, CreateServers(2));
        Window window = ShowTree(tree);

        try
        {
            ISelectionProvider provider = SelectionProviderOf(tree);

            Assert.True(provider.CanSelectMultiple);
            Assert.False(provider.IsSelectionRequired);

            // The stock peer answers false, which is exactly the defect: assistive technology reads
            // CanSelectMultiple first and then stops looking for a second selected row. Asserting
            // the difference keeps this test from passing on a tree that changed nothing.
            TreeView stockTree = new() { ItemsSource = CreateServers(2) };
            ISelectionProvider stockProvider =
                (ISelectionProvider)new TreeViewAutomationPeer(stockTree).GetPattern(PatternInterface.Selection)!;
            Assert.False(stockProvider.CanSelectMultiple);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTree_TwoSelectedRows_AreBothVisibleThroughTheSelectionPattern()
    {
        List<ServerItemViewModel> servers = CreateServers(3);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[0]);
        host.Selected.Add(servers[2]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            // The selected set is asserted through the peers rather than through GetSelection():
            // GetSelection hands back marshalled providers, and nothing can be marshalled until a
            // UI Automation client is attached, so counting it here would measure the absence of a
            // client rather than the selection. SessionTree_MultiSelection_IsVisibleThroughRealUiAutomation
            // is where the marshalled array is proved, with a real client on the other end - and it
            // has to be, because which peer is marshallable is a question only a live tree answers.
            Assert.Equal(
                ["srv-0", "srv-2"],
                SelectedPeersOf(tree).Select(peer => ((ServerItemViewModel)peer.BoundItem!).DisplayName));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTree_NothingSelected_ReportsAnEmptySelection()
    {
        FakeSelectionHost host = new();
        SessionTreeView tree = CreateTree(host, CreateServers(3));
        Window window = ShowTree(tree);

        try
        {
            Assert.Empty(SelectedPeersOf(tree));
            Assert.Empty(SelectionProviderOf(tree).GetSelection());
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_ReportsSelectionFromTheHostNotTheNativeFlag()
    {
        List<ServerItemViewModel> servers = CreateServers(2);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[1]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            SessionTreeViewItem container = ContainerFor(tree, servers[1]);

            // This is the state the tree deliberately leaves behind: the row is selected in the
            // view model while the native flag stays false.
            Assert.False(container.IsSelected);
            Assert.True(SelectionItemProviderOf(container).IsSelected);

            SessionTreeViewItem unselected = ContainerFor(tree, servers[0]);
            Assert.False(SelectionItemProviderOf(unselected).IsSelected);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_Select_ReplacesTheSelectionThroughTheHost()
    {
        List<ServerItemViewModel> servers = CreateServers(2);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[0]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            SelectionItemProviderOf(ContainerFor(tree, servers[1])).Select();

            Assert.Equal(["SelectOnly:srv-1"], host.Operations);
            Assert.Equal([servers[1]], host.Selected);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_AddToSelection_KeepsTheRowsAlreadySelected()
    {
        List<ServerItemViewModel> servers = CreateServers(2);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[0]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            SelectionItemProviderOf(ContainerFor(tree, servers[1])).AddToSelection();

            Assert.Equal(["Add:srv-1"], host.Operations);
            Assert.Equal([servers[0], servers[1]], host.Selected);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_RemoveFromSelection_LeavesTheOtherRowsSelected()
    {
        List<ServerItemViewModel> servers = CreateServers(2);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[0]);
        host.Selected.Add(servers[1]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            SelectionItemProviderOf(ContainerFor(tree, servers[0])).RemoveFromSelection();

            Assert.Equal(["Remove:srv-0"], host.Operations);
            Assert.Equal([servers[1]], host.Selected);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_RecycledOntoAnotherRow_ReportsTheNewItem()
    {
        List<ServerItemViewModel> servers = CreateServers(2);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[1]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            SessionTreeViewItem container = ContainerFor(tree, servers[0]);
            ISelectionItemProvider provider = SelectionItemProviderOf(container);
            Assert.False(provider.IsSelected);

            // The tree virtualizes with VirtualizationMode.Recycling, so one container - and one
            // peer - serves many rows over its life. A peer that latched onto its first item would
            // keep answering for a row that is no longer displayed.
            container.DataContext = servers[1];

            Assert.True(provider.IsSelected);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTreeItem_WithoutASelectionHost_RefusesToChangeSelection()
    {
        List<ServerItemViewModel> servers = CreateServers(1);
        SessionTreeView tree = CreateTree(selectionHost: null, servers);
        Window window = ShowTree(tree);

        try
        {
            ISelectionItemProvider provider = SelectionItemProviderOf(ContainerFor(tree, servers[0]));

            Assert.False(provider.IsSelected);
            Assert.Throws<InvalidOperationException>(() => provider.Select());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The end-to-end proof: a real UI Automation client, over a real window handle, sees the
    /// multi-selection. It is the only place the marshalled <c>GetSelection()</c> array is
    /// meaningful, because it is the only place a client is attached.
    /// </summary>
    /// <remarks>
    /// Complementary, not a substitute: everything it covers that CAN be measured without a
    /// desktop is already pinned by the blocking tests above, so a contended or headless runner
    /// skipping this lane never turns a real regression green.
    /// </remarks>
    [StaFact]
    [Trait("Category", "RequiresDesktop")]
    public void SessionTree_MultiSelection_IsVisibleThroughRealUiAutomation()
    {
        List<ServerItemViewModel> servers = CreateServers(3);
        FakeSelectionHost host = new();
        host.Selected.Add(servers[0]);
        host.Selected.Add(servers[2]);
        SessionTreeView tree = CreateTree(host, servers);
        Window window = ShowTree(tree);

        try
        {
            nint handle = new WindowInteropHelper(window).Handle;
            using UIA3Automation automation = new();
            FlaUI.Core.AutomationElements.AutomationElement root = automation.FromHandle(handle);

            FlaUI.Core.AutomationElements.AutomationElement? treeElement =
                root.FindFirstDescendant(condition => condition.ByControlType(FlaUI.Core.Definitions.ControlType.Tree));
            Assert.NotNull(treeElement);

            Assert.True(
                treeElement!.Patterns.Selection.IsSupported,
                "The session tree exposes no Selection pattern.");
            FlaUI.Core.Patterns.ISelectionPattern selection = treeElement.Patterns.Selection.Pattern;

            Assert.True(selection.CanSelectMultiple.Value);
            Assert.Equal(2, selection.Selection.Value.Length);
            Assert.Equal(
                ["srv-0", "srv-2"],
                selection.Selection.Value.Select(element => element.Name).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    private static ISelectionProvider SelectionProviderOf(SessionTreeView tree)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(tree);
        return Assert.IsAssignableFrom<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
    }

    private static ISelectionItemProvider SelectionItemProviderOf(SessionTreeViewItem container)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(container);
        return Assert.IsAssignableFrom<ISelectionItemProvider>(
            peer.GetPattern(PatternInterface.SelectionItem));
    }

    private static IReadOnlyList<SessionTreeViewItemAutomationPeer> SelectedPeersOf(SessionTreeView tree)
    {
        SessionTreeViewAutomationPeer peer =
            Assert.IsType<SessionTreeViewAutomationPeer>(UIElementAutomationPeer.CreatePeerForElement(tree));
        return [.. peer.SelectedPeers()];
    }

    private static SessionTreeViewItem ContainerFor(SessionTreeView tree, ServerItemViewModel server)
    {
        object? container = tree.ItemContainerGenerator.ContainerFromItem(server);
        return Assert.IsType<SessionTreeViewItem>(container);
    }

    private static List<ServerItemViewModel> CreateServers(int count)
        => [.. Enumerable.Range(0, count).Select(index => ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = $"id-{index}",
                DisplayName = $"srv-{index}",
                ConnectionType = "SSH",
                RemoteServer = $"host{index}.example.test"
            }))];

    private static SessionTreeView CreateTree(
        ISessionTreeSelectionHost? selectionHost,
        IEnumerable<ServerItemViewModel> servers)
        => new()
        {
            Width = 320,
            Height = 240,
            ItemsSource = servers,
            SelectionHost = selectionHost,
            ItemContainerStyle = RowNamingStyle()
        };

    /// <summary>Names each row after the server it shows, so a client can tell the rows apart.</summary>
    /// <remarks>
    /// The name goes on the CONTAINER. A row that carries no name of its own falls back to
    /// ToString() of the bound item, which here is the view model's type name - identical for every
    /// row and useless to a client - and a name set inside a data template never reaches the
    /// automation tree at all, because the elements a template produces carry no peer.
    /// <para>
    /// This is arrangement, not the thing under test: naming the rows is what lets the assertions
    /// below say WHICH rows the selection reports rather than only how many.
    /// </para>
    /// </remarks>
    private static Style RowNamingStyle()
    {
        Style style = new(typeof(SessionTreeViewItem));
        style.Setters.Add(new Setter(
            AutomationProperties.NameProperty,
            new Binding(nameof(ServerItemViewModel.DisplayName))));
        return style;
    }

    private static Window ShowTree(SessionTreeView tree)
    {
        Window window = new()
        {
            Width = 340,
            Height = 280,
            Content = tree,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        tree.UpdateLayout();
        return window;
    }

    /// <summary>
    /// Records what the peers ask of the selection model. It deliberately does NOT reuse
    /// <see cref="ServerListViewModel"/>: these tests are about what the peers report and request,
    /// and a real list view model would drag its whole dependency graph in without making a single
    /// assertion sharper.
    /// </summary>
    private sealed class FakeSelectionHost : ISessionTreeSelectionHost
    {
        public List<object> Selected { get; } = [];

        public List<string> Operations { get; } = [];

        public bool IsItemSelected(object? item) => item is not null && Selected.Contains(item);

        public void SelectOnlyItem(object? item)
        {
            Operations.Add($"SelectOnly:{Name(item)}");
            Selected.Clear();
            if (item is not null)
            {
                Selected.Add(item);
            }
        }

        public void AddItemToSelection(object? item)
        {
            Operations.Add($"Add:{Name(item)}");
            if (item is not null && !Selected.Contains(item))
            {
                Selected.Add(item);
            }
        }

        public void RemoveItemFromSelection(object? item)
        {
            Operations.Add($"Remove:{Name(item)}");
            if (item is not null)
            {
                Selected.Remove(item);
            }
        }

        private static string Name(object? item)
            => item is ServerItemViewModel server ? server.DisplayName : "<none>";
    }
}
