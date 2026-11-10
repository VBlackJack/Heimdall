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

using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.UiTests;

/// <summary>
/// Guards the folder collapse contract of the session tree: a row stays bound to its
/// folder view model, and pressing the chevron hides the sessions even when one of them
/// holds selection and keyboard focus.
/// </summary>
public sealed class SessionTreeFolderCollapseTests
{
    private const int ServerCount = 5;
    private const string ExpanderPartName = "Expander";
    private const string ItemsHostPartName = "ItemsHost";

    private const string ItemTemplateXaml =
        "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
        "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" TargetType=\"TreeViewItem\">" +
        "<StackPanel>" +
        "<StackPanel Orientation=\"Horizontal\">" +
        "<ToggleButton x:Name=\"Expander\" Width=\"16\" Height=\"16\" Focusable=\"False\" ClickMode=\"Press\" " +
        "IsChecked=\"{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}\"/>" +
        "<ContentPresenter ContentSource=\"Header\"/>" +
        "</StackPanel>" +
        "<ItemsPresenter x:Name=\"ItemsHost\"/>" +
        "</StackPanel>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property=\"IsExpanded\" Value=\"False\">" +
        "<Setter TargetName=\"ItemsHost\" Property=\"Visibility\" Value=\"Collapsed\"/>" +
        "</Trigger>" +
        "</ControlTemplate.Triggers>" +
        "</ControlTemplate>";

    /// <summary>
    /// Pins plain WPF behaviour: this tree attaches no SelectedItemChanged handler, so the test
    /// passes with or without the native selection fix.
    /// </summary>
    [StaFact]
    public void RealizingAnOffscreenSession_KeepsTheFolderRowBoundToItsViewModel()
    {
        FolderViewModel root = CreateFolder("Root");
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            RealizeServer(tree, root, root.Servers[^1]);
            TreeViewItem row = RowOf(tree, root);

            BaseValueSource source = DependencyPropertyHelper
                .GetValueSource(row, TreeViewItem.IsExpandedProperty)
                .BaseValueSource;
            Assert.NotEqual(BaseValueSource.Local, source);
            Assert.True(root.IsExpanded);

            ToggleChevron(row);

            Assert.False(root.IsExpanded);
            Assert.False(row.IsExpanded);
            Assert.False(ChildrenAreVisible(row));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Pins plain WPF behaviour: this tree attaches no SelectedItemChanged handler, so the test
    /// passes with or without the native selection fix.
    /// </summary>
    [StaFact]
    public void CollapsingAFolderWhoseSessionHasTheFocus_HidesItsSessions()
    {
        FolderViewModel root = CreateFolder("Root");
        root.IsExpanded = true;
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            TreeViewItem row = RowOf(tree, root);
            FocusServer(tree, root, root.Servers[1]);

            PressChevron(row);

            AssertCollapsed(root, row);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Pins plain WPF behaviour: this tree attaches no SelectedItemChanged handler, so the test
    /// passes with or without the native selection fix.
    /// </summary>
    [StaFact]
    public void CollapsingANestedFolderWhoseSessionHasTheFocus_HidesItsSessions()
    {
        FolderViewModel root = CreateFolder("Root");
        FolderViewModel child = CreateFolder("Root/Child");
        root.SubFolders.Add(child);
        root.IsExpanded = true;
        child.IsExpanded = true;
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            TreeViewItem childRow = FocusServer(tree, child, child.Servers[2]);

            PressChevron(childRow);

            AssertCollapsed(child, childRow);
            Assert.True(root.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Pins plain WPF behaviour: this tree attaches no SelectedItemChanged handler, so the test
    /// passes with or without the native selection fix.
    /// </summary>
    [StaFact]
    public void CollapsingTheOuterFolderOfAFocusedNestedSession_HidesEverythingBelow()
    {
        FolderViewModel root = CreateFolder("Root");
        FolderViewModel child = CreateFolder("Root/Child");
        root.SubFolders.Add(child);
        root.IsExpanded = true;
        child.IsExpanded = true;
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            FocusServer(tree, child, child.Servers[2]);
            TreeViewItem rootRow = RowOf(tree, root);

            PressChevron(rootRow);

            AssertCollapsed(root, rootRow);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Pins plain WPF behaviour: this tree attaches no SelectedItemChanged handler, so the test
    /// passes with or without the native selection fix.
    /// </summary>
    [StaFact]
    public void CollapsingAFolderAfterARealize_HidesItsFocusedSessions()
    {
        FolderViewModel root = CreateFolder("Root");
        TreeView tree = CreateTree([root]);
        Window window = ShowTree(tree);

        try
        {
            TreeViewItem server = RealizeServer(tree, root, root.Servers[^1]);
            server.IsSelected = true;
            Keyboard.Focus(server);
            TreeViewItem row = RowOf(tree, root);

            PressChevron(row);

            AssertCollapsed(root, row);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void CollapsingTheFolderOfTheSelectedSession_WithTheRealViewModel_LeavesItCollapsed()
    {
        string root = Path.Combine(Path.GetTempPath(), "heimdall-collapse-ui", Guid.NewGuid().ToString("N"));
        var config = new ConfigManager(root);
        var localizer = new LocalizationManager();
        Task.Run(async () =>
        {
            await config.InitializeAsync();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        }).GetAwaiter().GetResult();
        var list = new ServerListViewModel(
            config,
            localizer,
            Stub<IUiDispatcher>(),
            new ConnectionStateMachine(),
            new ConnectionService(config, localizer, Stub<ITunnelService>(), []),
            Stub<IDialogService>(),
            Stub<IRdpImportService>(),
            new PuttySessionImporter(Stub<IPuttySessionRegistrySource>(), config),
            new KnownHostsImporter(config, new HostKeyStore()));
        var settings = new AppSettings();
        settings.TreeExpandedNodes.Add("ops");
        settings.TreeExpandedNodes.Add("lab");
        list.LoadServers(
            [
                ViewModelServer("alpha", "ops"),
                ViewModelServer("beta", "lab"),
                ViewModelServer("gamma", "lab")
            ],
            settings);
        FolderViewModel lab = list.GroupedServers.Single(folder => folder.Name == "lab");
        ServerItemViewModel beta = lab.Servers.Single(server => server.Id == "beta");
        var restored = new List<ServerItemViewModel>();
        list.HiddenSelectionRestored += restored.Add;
        TreeView tree = CreateTree(list.GroupedServers);
        tree.SelectedItemChanged += (_, e) =>
        {
            if (MainWindow.ShouldClearNativeSelection(false, e.NewValue, list.SelectedItems.Contains)
                && TreeInteractionState.FindTreeViewItemContainer(tree, e.NewValue) is { } folderRow)
            {
                MainWindow.ClearNativeSelection(folderRow, e.NewValue);
            }
        };
        Window window = ShowTree(tree);

        try
        {
            list.SelectSingle(beta);
            TreeViewItem server = RealizeServerIn(tree, list.GroupedServers, beta);
            server.IsSelected = true;
            Keyboard.Focus(server);
            TreeViewItem row = RowOf(tree, lab);
            Assert.True(server.IsKeyboardFocused, "the session row must hold keyboard focus before the press");

            PressChevron(row);
            PumpDispatcher();

            Assert.Empty(restored);
            AssertCollapsed(lab, row);
            Assert.False(row.IsSelected);
            Assert.Null(tree.SelectedItem);
        }
        finally
        {
            window.Close();
            list.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [StaFact]
    public void ClearNativeSelection_OfAFolder_WaitsForTheNotificationToReturn()
    {
        var folder = new FolderViewModel { Name = "Root", FullPath = "Root" };
        var row = new TreeViewItem { DataContext = folder, IsSelected = true };

        MainWindow.ClearNativeSelection(row, folder);

        Assert.True(row.IsSelected);
        PumpDispatcher();
        Assert.False(row.IsSelected);
    }

    [StaFact]
    public void ClearNativeSelection_OfAFolder_SkipsARowReboundToAnotherItem()
    {
        var folder = new FolderViewModel { Name = "Root", FullPath = "Root" };
        var other = new FolderViewModel { Name = "Other", FullPath = "Other" };
        var row = new TreeViewItem { DataContext = folder, IsSelected = true };

        MainWindow.ClearNativeSelection(row, folder);
        row.DataContext = other;
        PumpDispatcher();

        Assert.True(row.IsSelected);
    }

    private static void PumpDispatcher()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static ServerProfileDto ViewModelServer(string id, string group) => new()
    {
        Id = id,
        DisplayName = id,
        RemoteServer = id + ".example.test",
        Group = group
    };

    private static TreeViewItem RealizeServerIn(
        TreeView tree,
        IEnumerable<FolderViewModel> roots,
        ServerItemViewModel server)
    {
        Assert.True(TreeInteractionState.TryBuildItemPath(roots, server, out IReadOnlyList<object> path));
        TreeViewItem? container = TreeInteractionState.RealizeTreeViewItemContainer(tree, path);
        Assert.NotNull(container);
        return container!;
    }

    private static T Stub<T>()
        where T : class => DispatchProxy.Create<T, DefaultReturningProxy>();

    /// <summary>Answers every call with a completed task or a default value, running delegates inline.</summary>
    public class DefaultReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            foreach (object? argument in args ?? [])
            {
                if (argument is Action action)
                {
                    action();
                }
            }

            Type returnType = targetMethod!.ReturnType;
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type inner = returnType.GetGenericArguments()[0];
                object? value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    private static void AssertCollapsed(FolderViewModel folder, TreeViewItem row)
    {
        ToggleButton chevron = ChevronOf(row);
        Assert.False(chevron.IsChecked);
        Assert.False(row.IsExpanded);
        Assert.False(folder.IsExpanded);
        Assert.False(ChildrenAreVisible(row));
    }

    private static bool ChildrenAreVisible(TreeViewItem row)
    {
        var host = (FrameworkElement)row.Template.FindName(ItemsHostPartName, row);
        return host.Visibility == Visibility.Visible && host.IsVisible;
    }

    private static ToggleButton ChevronOf(TreeViewItem row) =>
        (ToggleButton)row.Template.FindName(ExpanderPartName, row);

    private static void ToggleChevron(TreeViewItem row)
    {
        ToggleButton chevron = ChevronOf(row);
        chevron.IsChecked = !chevron.IsChecked;
        row.UpdateLayout();
    }

    private static void PressChevron(TreeViewItem row)
    {
        ToggleButton chevron = ChevronOf(row);
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = chevron
        };
        chevron.RaiseEvent(press);
        row.UpdateLayout();
    }

    private static TreeViewItem RowOf(TreeView tree, FolderViewModel folder)
    {
        Assert.True(TreeInteractionState.TryBuildItemPath([(FolderViewModel)tree.Items[0]], folder, out IReadOnlyList<object> path));
        ItemsControl parent = tree;
        TreeViewItem? row = null;
        foreach (object item in path)
        {
            row = parent.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
            Assert.NotNull(row);
            parent = row!;
        }

        return row!;
    }

    private static TreeViewItem RealizeServer(TreeView tree, FolderViewModel owner, ServerItemViewModel server)
    {
        Assert.True(TreeInteractionState.TryBuildItemPath([(FolderViewModel)tree.Items[0]], server, out IReadOnlyList<object> path));
        TreeViewItem? container = TreeInteractionState.RealizeTreeViewItemContainer(tree, path);
        Assert.NotNull(container);
        Assert.Contains(server, owner.Servers);
        return container!;
    }

    private static TreeViewItem FocusServer(
        TreeView tree,
        FolderViewModel owner,
        ServerItemViewModel server)
    {
        TreeViewItem container = RealizeServer(tree, owner, server);
        container.IsSelected = true;
        Keyboard.Focus(container);
        Assert.True(container.IsSelected);
        Assert.True(container.IsKeyboardFocused, "focus did not land");
        return RowOf(tree, owner);
    }

    private static FolderViewModel CreateFolder(string path)
    {
        var folder = new FolderViewModel { Name = path, FullPath = path };
        for (var index = 0; index < ServerCount; index++)
        {
            folder.Servers.Add(ServerItemViewModel.FromDto(new ServerProfileDto
            {
                Id = $"{path}-server-{index}",
                DisplayName = $"Server {path} {index}",
                RemoteServer = $"server-{index}.example.test"
            }));
        }

        return folder;
    }

    private static TreeView CreateTree(IEnumerable<FolderViewModel> roots)
    {
        var tree = new TreeView
        {
            Width = 320,
            Height = 400,
            ItemsSource = roots,
            ItemsPanel = CreateItemsPanelTemplate()
        };
        ScrollViewer.SetCanContentScroll(tree, true);
        VirtualizingPanel.SetIsVirtualizing(tree, true);
        VirtualizingPanel.SetVirtualizationMode(tree, VirtualizationMode.Recycling);

        tree.Resources.Add(
            new DataTemplateKey(typeof(FolderViewModel)),
            new HierarchicalDataTemplate(typeof(FolderViewModel))
            {
                ItemsSource = new Binding(nameof(FolderViewModel.Children))
            });

        var itemStyle = new Style(typeof(TreeViewItem));
        itemStyle.Setters.Add(new Setter(
            TreeViewItem.IsExpandedProperty,
            new Binding(nameof(FolderViewModel.IsExpanded)) { Mode = BindingMode.TwoWay }));
        itemStyle.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty, CreateItemsPanelTemplate()));
        itemStyle.Setters.Add(new Setter(
            Control.TemplateProperty,
            (ControlTemplate)XamlReader.Parse(ItemTemplateXaml)));
        tree.ItemContainerStyle = itemStyle;
        return tree;
    }

    private static ItemsPanelTemplate CreateItemsPanelTemplate() =>
        new(new FrameworkElementFactory(typeof(VirtualizingTreePanel)));

    private static Window ShowTree(TreeView tree)
    {
        var window = new Window
        {
            Width = 340,
            Height = 440,
            Content = tree,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        tree.UpdateLayout();
        return window;
    }
}
