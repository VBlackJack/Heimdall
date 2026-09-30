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
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Heimdall.App.Services;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests;

/// <summary>
/// The shipped tree styles, loaded from the application's own theme dictionary rather than
/// rebuilt in the test: the virtualization test next door builds a bare TreeView, which is why
/// the themed template could drop the scroll settings and nobody noticed.
/// </summary>
/// <remarks>
/// Everything runs on the shared application thread. A window shown on a thread of its own,
/// once that application exists, resolves its implicit styles through application resources
/// another thread owns and fails.
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class SessionTreeThemedStyleTests
{
    private const int InventorySize = 600;
    private const int MaxExpectedRealizedContainers = 80;

    /// <summary>Loads the shipped dictionary the application merges, not a copy of it.</summary>
    private static ResourceDictionary LoadTheme() =>
        Assert.IsType<ResourceDictionary>(Application.LoadComponent(
            new Uri("/Heimdall;component/Themes/CommonControls.xaml", UriKind.Relative)));

    /// <summary>
    /// A guard, not a red test: finding T-02 said the themed template dropped CanContentScroll and
    /// so realized every row. Measured on the unchanged template, the scroll settings set on the
    /// tree reach its ScrollViewer and twelve of six hundred rows are realized; this keeps it so.
    /// </summary>
    [Fact]
    public Task ThemedTreeViewStyle_KeepsTheTreeVirtualized() => OnUiThread(() =>
    {
        ResourceDictionary theme = LoadTheme();
        FolderViewModel root = CreateFolder();
        TreeView tree = CreateTree(theme, root);
        Window window = Show(tree);

        try
        {
            TreeViewItem rootContainer = Assert.IsType<TreeViewItem>(
                tree.ItemContainerGenerator.ContainerFromItem(root));
            rootContainer.IsExpanded = true;
            tree.UpdateLayout();

            int realized = TreeInteractionState.CountRealizedDirectContainers(rootContainer);
            Assert.InRange(realized, 1, MaxExpectedRealizedContainers);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ThemedTreeViewItemStyle_DrawsNoFocusAdornerAroundTheSubtree() => OnUiThread(() =>
    {
        ResourceDictionary theme = LoadTheme();
        Style itemStyle = Assert.IsType<Style>(theme["ThemedTreeViewItemStyle"]);

        Setter focusVisual = Assert.Single(
            itemStyle.Setters.OfType<Setter>(),
            setter => setter.Property == FrameworkElement.FocusVisualStyleProperty);
        Assert.Null(focusVisual.Value);
    });

    [Fact]
    public Task TreeExpander_TakesAClickBesideItsTwelvePixelGlyph() => OnUiThread(() =>
    {
        ResourceDictionary theme = LoadTheme();
        FolderViewModel root = CreateFolder();
        TreeView tree = CreateTree(theme, root);
        Window window = Show(tree);

        try
        {
            TreeViewItem rootContainer = Assert.IsType<TreeViewItem>(
                tree.ItemContainerGenerator.ContainerFromItem(root));
            ToggleButton expander = FindDescendant<ToggleButton>(rootContainer)
                ?? throw new InvalidOperationException("the themed item has no expander");
            Assert.Equal(12, expander.ActualWidth);

            // Five pixels below the glyph box: outside the 12 px button, inside a 24 px target.
            Point below = expander.TranslatePoint(
                new Point(expander.ActualWidth / 2, expander.ActualHeight + 5),
                window);
            DependencyObject? hit = window.InputHitTest(below) as DependencyObject;

            Assert.Same(expander, FindAncestor<ToggleButton>(hit));
        }
        finally
        {
            window.Close();
        }
    });

    private static Task OnUiThread(Action action) => WpfTestHost.Dispatcher.InvokeAsync(action).Task;

    private static FolderViewModel CreateFolder()
    {
        var servers = new ObservableCollection<ServerItemViewModel>(
            Enumerable.Range(0, InventorySize)
                .Select(index => ServerItemViewModel.FromDto(new ServerProfileDto
                {
                    Id = $"server-{index:D4}",
                    DisplayName = $"Server {index:D4}",
                    RemoteServer = $"server-{index:D4}.example.test"
                })));

        return new FolderViewModel { Name = "Large", FullPath = "Large", Servers = servers };
    }

    private static TreeView CreateTree(ResourceDictionary theme, FolderViewModel root)
    {
        var tree = new TreeView
        {
            Width = 320,
            Height = 240,
            ItemsSource = new[] { root },
            Style = Assert.IsType<Style>(theme["ThemedTreeViewStyle"]),
            ItemsPanel = CreateItemsPanelTemplate()
        };
        tree.Resources.MergedDictionaries.Add(theme);

        // What the sessions tree sets on itself; the style has to pass it through.
        ScrollViewer.SetCanContentScroll(tree, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);

        tree.Resources.Add(
            new DataTemplateKey(typeof(FolderViewModel)),
            new HierarchicalDataTemplate(typeof(FolderViewModel))
            {
                ItemsSource = new Binding(nameof(FolderViewModel.Children))
            });

        var itemStyle = new Style(typeof(TreeViewItem), Assert.IsType<Style>(theme["ThemedTreeViewItemStyle"]));
        itemStyle.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty, CreateItemsPanelTemplate()));
        tree.ItemContainerStyle = itemStyle;
        return tree;
    }

    private static ItemsPanelTemplate CreateItemsPanelTemplate() =>
        new(new FrameworkElementFactory(typeof(VirtualizingTreePanel)));

    private static Window Show(TreeView tree)
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

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null and not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as T;
    }
}

