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
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.App.Automation;
using Heimdall.App.Controls;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

/// <summary>
/// A row's automation peer listens to its item only while the row is on screen.
/// </summary>
/// <remarks>
/// <para>Hypothesis H1 of the 2026-09-05 tree audit, confirmed on the day. The peer observes the
/// bound item's <c>PropertyChanged</c> so a selection change reaches UI Automation, and dropped
/// the subscription on <c>DataContextChanged</c> alone. When the tree discards a row, WPF puts
/// its <c>DisconnectedItem</c> sentinel into the row's DataContext WITHOUT raising
/// <c>DataContextChanged</c>, so the peer never heard of the discard and stayed on the item's
/// invocation list. The item lives as long as the inventory, so every discarded row - its visual
/// subtree included - lived as long, once a UI Automation client had touched the tree. Rows are
/// discarded on every structural rebuild.</para>
/// <para>The measurement reads the item's invocation list, not the collector: in this host a
/// discarded row stays reachable for other reasons for a while, a stock TreeViewItem included, so
/// a weak reference cannot attribute anything. The list is exact: the peer is on it or it is not.</para>
/// </remarks>
public sealed class SessionTreePeerRetentionTests
{
    [StaFact]
    public void DiscardedRow_ItsPeerStopsListeningToTheItem()
    {
        ObservableCollection<ServerItemViewModel> servers = new(CreateServers(3));
        SessionTreeView tree = CreateTree(servers);
        Window window = ShowWindow(tree);

        try
        {
            ServerItemViewModel item = servers[1];
            SessionTreeViewItem container =
                Assert.IsType<SessionTreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(item));
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(container);
            Assert.IsType<SessionTreeViewItemAutomationPeer>(peer);
            Assert.Contains(peer, PropertyChangedListenersOf(item));

            servers.Remove(item);
            tree.UpdateLayout();
            Drain(window.Dispatcher);

            Assert.DoesNotContain(peer, PropertyChangedListenersOf(item));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void RowStillOnScreen_ItsPeerKeepsListening()
    {
        // The other half of the contract: dropping the subscription is tied to the row leaving
        // the screen, not to any layout pass.
        ObservableCollection<ServerItemViewModel> servers = new(CreateServers(3));
        SessionTreeView tree = CreateTree(servers);
        Window window = ShowWindow(tree);

        try
        {
            ServerItemViewModel item = servers[1];
            SessionTreeViewItem container =
                Assert.IsType<SessionTreeViewItem>(tree.ItemContainerGenerator.ContainerFromItem(item));
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(container);

            servers.Remove(servers[0]);
            tree.UpdateLayout();
            Drain(window.Dispatcher);

            Assert.Contains(peer, PropertyChangedListenersOf(item));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every object subscribed to the item's <c>PropertyChanged</c>.</summary>
    private static object?[] PropertyChangedListenersOf(ServerItemViewModel item)
    {
        FieldInfo? field = typeof(ObservableObject).GetField(
            "PropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(item) is PropertyChangedEventHandler handler
            ? [.. handler.GetInvocationList().Select(entry => entry.Target)]
            : [];
    }

    private static SessionTreeView CreateTree(ObservableCollection<ServerItemViewModel> servers) =>
        new()
        {
            Width = 320,
            Height = 240,
            ItemsSource = servers,
            SelectionHost = new EmptySelectionHost()
        };

    private static Window ShowWindow(UIElement content)
    {
        Window window = new()
        {
            Content = content,
            Width = 320,
            Height = 240,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Left = -10000,
            Top = -10000
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void Drain(Dispatcher dispatcher) =>
        dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static List<ServerItemViewModel> CreateServers(int count)
        => [.. Enumerable.Range(0, count).Select(index => ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = $"id-{index}",
                DisplayName = $"srv-{index}",
                ConnectionType = "SSH",
                RemoteServer = $"host{index}.example.test"
            }))];

    private sealed class EmptySelectionHost : ISessionTreeSelectionHost
    {
        public bool IsItemSelected(object? item) => false;

        public void SelectOnlyItem(object? item)
        {
        }

        public void AddItemToSelection(object? item)
        {
        }

        public void RemoveItemFromSelection(object? item)
        {
        }
    }
}
