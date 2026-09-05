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

/// <summary>
/// When WPF's own row selection is cleared, and how a row is selected from code.
/// </summary>
/// <remarks>
/// Hypotheses H2 and H3 of the 2026-09-05 tree audit. WPF selects whatever it focuses, so a
/// modifier gesture that walks the focus leaves the native flag on a row the view model does not
/// hold; and a selection made by setting the native flag alone routes through the handler that
/// reads the modifier keys, so a deferred reveal under a still-held Ctrl was taken for a
/// multi-selection gesture.
/// </remarks>
public sealed class MainWindowTreeNativeSelectionRuleTests
{
    [Fact]
    public void AFolder_AlwaysHasItsNativeSelectionCleared()
    {
        FolderViewModel folder = new() { Name = "Production", FullPath = "Production" };

        Assert.True(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: false, folder, _ => true));
        Assert.True(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: true, folder, _ => true));
    }

    [Fact]
    public void ARowWpfSelectedUnderAModifier_IsClearedWhenTheViewModelDoesNotHoldIt()
    {
        ServerItemViewModel walkedOnto = CreateServer("walked");

        Assert.True(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: true, walkedOnto, _ => false));
    }

    [Fact]
    public void ARowInTheSelection_KeepsItsNativeFlagUnderAModifier()
    {
        // The primary of a Shift range is both focused and selected; it keeps the selected look.
        ServerItemViewModel primary = CreateServer("primary");

        Assert.False(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: true, primary, _ => true));
    }

    [Fact]
    public void APlainSelection_IsNeverCleared()
    {
        // Without a modifier the handler adopts the row into the view model instead.
        ServerItemViewModel plain = CreateServer("plain");

        Assert.False(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: false, plain, _ => false));
        Assert.False(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: false, null, _ => false));
        Assert.False(MainWindow.ShouldClearNativeSelection(preserveMultiSelection: true, null, _ => false));
    }

    [Fact]
    public void SelectRowProgrammatically_SelectsTheViewModelFirst_ThenTheRowUnderASuppressedSync()
    {
        RunOnSta(() =>
        {
            TreeInteractionState treeState = new();
            ServerItemViewModel server = CreateServer("revealed");
            TreeViewItem container = new() { Header = server.DisplayName, DataContext = server };
            TreeView tree = new();
            tree.Items.Add(container);
            List<string> order = [];
            container.Selected += (_, _) => order.Add($"native:{treeState.SuppressSelectedItemSync}");

            MainWindow.SelectRowProgrammatically(
                treeState,
                container,
                server,
                selected =>
                {
                    Assert.Same(server, selected);
                    order.Add($"viewmodel:{container.IsSelected}");
                });

            Assert.Equal(["viewmodel:False", "native:True"], order);
            Assert.True(container.IsSelected);
            Assert.False(treeState.SuppressSelectedItemSync);
        });
    }

    private static ServerItemViewModel CreateServer(string id) =>
        ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH"
        });

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
