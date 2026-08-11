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

using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class MainWindowTreeNativeSelectionTests
{
    [Fact]
    public void CtrlToggleOff_ClearsNativeSelectionButKeepsPointerFocus()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            TreeViewItem pointerContainer = new() { Header = "Pointer server" };
            (Window window, TreeView tree) = ShowTree(pointerContainer);

            try
            {
                MainWindow.SynchronizeNativeTreeSelection(treeState, pointerContainer);
                DrainDispatcher(window.Dispatcher);

                Assert.True(pointerContainer.IsKeyboardFocused);
                Assert.False(pointerContainer.IsSelected);
                Assert.Null(tree.SelectedItem);
                Assert.False(treeState.SuppressSelectedItemSync);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CtrlToggleOff_KeepsLogicalPrimaryInViewModelAndPointerFocus()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            ServerItemViewModel logicalPrimary = CreateServer("logical-primary");
            ServerItemViewModel pointerServer = CreateServer("pointer-server");
            logicalPrimary.IsSelected = true;
            TreeViewItem logicalPrimaryContainer = new()
            {
                Header = "Logical primary",
                DataContext = logicalPrimary
            };
            TreeViewItem pointerContainer = new()
            {
                Header = "Pointer server",
                DataContext = pointerServer
            };
            (Window window, TreeView tree) = ShowTree(logicalPrimaryContainer, pointerContainer);

            try
            {
                MainWindow.SynchronizeNativeTreeSelection(treeState, pointerContainer);
                DrainDispatcher(window.Dispatcher);

                Assert.True(pointerContainer.IsKeyboardFocused);
                Assert.False(pointerContainer.IsSelected);
                Assert.False(logicalPrimaryContainer.IsSelected);
                Assert.Null(tree.SelectedItem);
                Assert.True(logicalPrimary.IsSelected);
                Assert.False(pointerServer.IsSelected);
                Assert.False(treeState.SuppressSelectedItemSync);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static ServerItemViewModel CreateServer(string id)
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH"
        });
    }

    private static (Window Window, TreeView Tree) ShowTree(params TreeViewItem[] items)
    {
        TreeView tree = new();
        foreach (TreeViewItem item in items)
        {
            tree.Items.Add(item);
        }

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
        return (window, tree);
    }

    private static void DrainDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke(
            static () => { },
            DispatcherPriority.ApplicationIdle);
    }

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
