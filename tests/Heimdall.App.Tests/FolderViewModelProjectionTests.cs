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
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the shape of the collection a folder hands to the tree.
/// </summary>
/// <remarks>
/// <see cref="FolderViewModel.Children"/> used to be a fresh <c>ArrayList</c> after every
/// invalidation. A new <c>ItemsSource</c> instance is a Reset to the item container generator, so
/// every filter pass discarded and regenerated every child container and threw keyboard focus out
/// of the tree. The tests below fail on that implementation: the collection instance changes, a
/// Reset is raised, and the focused container is replaced.
/// </remarks>
public sealed class FolderViewModelProjectionTests
{
    [Fact]
    public void Children_IsOneInstanceForTheFolderLife()
    {
        FolderViewModel folder = new() { Name = "Root", FullPath = "Root" };
        ObservableCollection<object> children = folder.Children;
        ServerItemViewModel first = CreateServer("first");
        ServerItemViewModel second = CreateServer("second");
        FolderViewModel child = new() { Name = "Child", FullPath = "Root/Child" };

        folder.Servers.Add(first);
        folder.SynchronizeVisibleChildren([child], [first, second]);
        folder.InvalidateChildren();
        folder.SynchronizeVisibleChildren([], [second]);

        Assert.Same(children, folder.Children);
        Assert.Equal([second], folder.Children);
    }

    [Fact]
    public void Children_ListsSubFoldersBeforeServers()
    {
        FolderViewModel folder = new() { Name = "Root", FullPath = "Root" };
        ServerItemViewModel server = CreateServer("server");
        FolderViewModel child = new() { Name = "Child", FullPath = "Root/Child" };

        folder.SynchronizeVisibleChildren([child], [server]);

        Assert.Equal([child, server], folder.Children);
        Assert.Equal(1, folder.ServerCount);
    }

    [Fact]
    public void ServersAddedToAFreshFolder_AppearInChildren()
    {
        // The collections a folder is born with bypass the generated setters, so the constructor
        // has to subscribe to them itself; a folder that missed that would show an empty branch.
        FolderViewModel folder = new() { Name = "Root", FullPath = "Root" };
        ServerItemViewModel server = CreateServer("server");

        folder.Servers.Add(server);

        Assert.Equal([server], folder.Children);
        Assert.Equal(1, folder.ServerCount);
    }

    [Fact]
    public void SynchronizeVisibleChildren_EditsInPlaceWithoutAReset()
    {
        FolderViewModel folder = new() { Name = "Root", FullPath = "Root" };
        ServerItemViewModel first = CreateServer("first");
        ServerItemViewModel second = CreateServer("second");
        ServerItemViewModel third = CreateServer("third");
        folder.SynchronizeVisibleChildren([], [first, second, third]);
        List<NotifyCollectionChangedAction> actions = [];
        folder.Children.CollectionChanged += (_, e) => actions.Add(e.Action);

        folder.SynchronizeVisibleChildren([], [third, first]);
        folder.InvalidateChildren();

        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal([third, first], folder.Children);
    }

    [Fact]
    public void TooltipText_IsNullForTheNoGroupFolder()
    {
        // WPF opens an empty tooltip for an empty string and none at all for null.
        FolderViewModel noGroup = new() { Name = "No group", FullPath = "" };
        FolderViewModel named = new() { Name = "Linux", FullPath = "Prod/Linux" };

        Assert.Null(noGroup.TooltipText);
        Assert.Equal("Prod/Linux", named.TooltipText);
    }

    [Fact]
    public void HasColor_FollowsColor()
    {
        FolderViewModel folder = new() { Name = "Linux", FullPath = "Prod/Linux" };
        List<string?> changed = [];
        folder.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(folder.HasColor);

        folder.Color = "#EF4444";

        Assert.True(folder.HasColor);
        Assert.Contains(nameof(FolderViewModel.HasColor), changed);
    }

    [Fact]
    public void TooltipText_FollowsFullPath()
    {
        FolderViewModel folder = new() { Name = "Linux", FullPath = "Prod/Linux" };
        List<string?> changed = [];
        folder.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        folder.FullPath = "Prod/Servers";

        Assert.Contains(nameof(FolderViewModel.TooltipText), changed);
        Assert.Equal("Prod/Servers", folder.TooltipText);
    }

    [Fact]
    public void FilterPass_KeepsTheFocusedContainerAndItsFocus()
    {
        RunOnSta(() =>
        {
            FolderViewModel folder = new() { Name = "Root", FullPath = "Root", IsExpanded = true };
            ServerItemViewModel first = CreateServer("first");
            ServerItemViewModel focused = CreateServer("focused");
            ServerItemViewModel third = CreateServer("third");
            folder.SynchronizeVisibleChildren([], [first, focused, third]);

            TreeView tree = CreateTree(folder);
            Window window = new()
            {
                Content = tree,
                Width = 320,
                Height = 240,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.ToolWindow
            };
            window.Show();
            window.UpdateLayout();
            Drain(window.Dispatcher);

            try
            {
                TreeViewItem folderContainer =
                    (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(folder);
                folderContainer.IsExpanded = true;
                window.UpdateLayout();
                Drain(window.Dispatcher);
                TreeViewItem before =
                    (TreeViewItem)folderContainer.ItemContainerGenerator.ContainerFromItem(focused);
                before.Focus();
                Drain(window.Dispatcher);
                Assert.True(before.IsFocused);

                // What a filter pass does to every folder it keeps: the same membership, then an
                // unconditional invalidation.
                folder.SynchronizeVisibleChildren([], [first, focused, third]);
                folder.InvalidateChildren();
                window.UpdateLayout();
                Drain(window.Dispatcher);

                TreeViewItem? after =
                    folderContainer.ItemContainerGenerator.ContainerFromItem(focused) as TreeViewItem;
                Assert.Same(before, after);
                Assert.NotNull(VisualTreeHelper.GetParent(before));
                Assert.True(before.IsFocused);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A tree bound the way the sessions tree is: recycling virtualization, a hierarchical
    /// template on <see cref="FolderViewModel.Children"/>, and expansion bound two-way.
    /// </summary>
    private static TreeView CreateTree(FolderViewModel root)
    {
        TreeView tree = new();
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);

        HierarchicalDataTemplate folderTemplate = new(typeof(FolderViewModel))
        {
            ItemsSource = new Binding(nameof(FolderViewModel.Children))
        };
        FrameworkElementFactory folderText = new(typeof(TextBlock));
        folderText.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderViewModel.Name)));
        folderTemplate.VisualTree = folderText;

        DataTemplate serverTemplate = new(typeof(ServerItemViewModel));
        FrameworkElementFactory serverText = new(typeof(TextBlock));
        serverText.SetBinding(
            TextBlock.TextProperty,
            new Binding(nameof(ServerItemViewModel.DisplayName)));
        serverTemplate.VisualTree = serverText;

        tree.Resources.Add(new DataTemplateKey(typeof(FolderViewModel)), folderTemplate);
        tree.Resources.Add(new DataTemplateKey(typeof(ServerItemViewModel)), serverTemplate);

        Style containerStyle = new(typeof(TreeViewItem));
        containerStyle.Setters.Add(new Setter(
            TreeViewItem.IsExpandedProperty,
            new Binding(nameof(FolderViewModel.IsExpanded)) { Mode = BindingMode.TwoWay }));
        tree.ItemContainerStyle = containerStyle;
        tree.ItemsSource = new ObservableCollection<FolderViewModel> { root };
        return tree;
    }

    private static ServerItemViewModel CreateServer(string id) =>
        ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH"
        });

    private static void Drain(Dispatcher dispatcher) =>
        dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
