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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

public sealed class SessionTreeVirtualizationTests
{
    private const int LargeInventorySize = 1_200;
    private const int MaxExpectedRealizedContainers = 80;

    [StaFact]
    public void SessionTree_LargeExpandedFolder_RealizesBoundedContainers()
    {
        FolderViewModel root = CreateLargeFolder();
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            TreeViewItem? rootContainer =
                TreeInteractionState.RealizeTreeViewItemContainer(tree, [root]);
            Assert.NotNull(rootContainer);

            rootContainer!.IsExpanded = true;
            tree.UpdateLayout();

            int realized =
                TreeInteractionState.CountRealizedDirectContainers(rootContainer);
            Assert.InRange(realized, 1, MaxExpectedRealizedContainers);
            Assert.True(realized < LargeInventorySize);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTree_Realizer_MaterializesFarOffscreenNestedNode()
    {
        FolderViewModel root = CreateLargeFolder();
        ServerItemViewModel target = root.Servers[^1];
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            Assert.True(
                TreeInteractionState.TryBuildItemPath(
                    [root],
                    target,
                    out IReadOnlyList<object> path));

            TreeViewItem? targetContainer =
                TreeInteractionState.RealizeTreeViewItemContainer(tree, path);

            Assert.NotNull(targetContainer);
            Assert.Same(target, targetContainer!.DataContext);
            Assert.True(targetContainer.IsVisible);
            Assert.InRange(
                TreeInteractionState.CountRealizedDirectContainers(
                    Assert.IsType<TreeViewItem>(
                        tree.ItemContainerGenerator.ContainerFromItem(root))),
                1,
                MaxExpectedRealizedContainers);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SessionTree_Recycling_PreservesFarNodeEditAndMultiSelectionState()
    {
        FolderViewModel root = CreateLargeFolder();
        ServerItemViewModel first = root.Servers[0];
        ServerItemViewModel target = root.Servers[^1];
        first.IsSelected = true;
        target.IsSelected = true;
        target.BeginInlineEdit();
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            Assert.True(
                TreeInteractionState.TryBuildItemPath(
                    [root],
                    target,
                    out IReadOnlyList<object> path));

            TreeViewItem? targetContainer =
                TreeInteractionState.RealizeTreeViewItemContainer(tree, path);

            Assert.NotNull(targetContainer);
            Assert.Same(target, targetContainer!.DataContext);
            Assert.True(first.IsSelected);
            Assert.True(target.IsSelected);
            Assert.True(target.IsEditing);
            Assert.Equal(target.DisplayName, target.EditName);
        }
        finally
        {
            window.Close();
        }
    }

    private static FolderViewModel CreateLargeFolder()
    {
        var servers = new ObservableCollection<ServerItemViewModel>(
            Enumerable.Range(0, LargeInventorySize)
                .Select(index => ServerItemViewModel.FromDto(new ServerProfileDto
                {
                    Id = $"server-{index:D4}",
                    DisplayName = $"Server {index:D4}",
                    RemoteServer = $"server-{index:D4}.example.test"
                })));

        return new FolderViewModel
        {
            Name = "Large",
            FullPath = "Large",
            Servers = servers
        };
    }

    private static TreeView CreateTree(IEnumerable<FolderViewModel> roots)
    {
        var tree = new TreeView
        {
            Width = 320,
            Height = 240,
            ItemsSource = roots,
            ItemsPanel = CreateItemsPanelTemplate()
        };
        ScrollViewer.SetCanContentScroll(tree, true);
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(
            tree,
            VirtualizationMode.Recycling);

        var folderTemplate = new HierarchicalDataTemplate(
            typeof(FolderViewModel))
        {
            ItemsSource = new Binding(nameof(FolderViewModel.Children))
        };
        tree.Resources.Add(
            new DataTemplateKey(typeof(FolderViewModel)),
            folderTemplate);

        var itemStyle = new Style(typeof(TreeViewItem));
        itemStyle.Setters.Add(new Setter(
            ItemsControl.ItemsPanelProperty,
            CreateItemsPanelTemplate()));
        tree.Resources.Add(typeof(TreeViewItem), itemStyle);
        return tree;
    }

    private static ItemsPanelTemplate CreateItemsPanelTemplate()
    {
        return new ItemsPanelTemplate(
            new FrameworkElementFactory(typeof(VirtualizingTreePanel)));
    }

    private static Window ShowTree(TreeView tree)
    {
        var window = new Window
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
}
