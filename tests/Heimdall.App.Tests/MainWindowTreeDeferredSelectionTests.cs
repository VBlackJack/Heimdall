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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the press half of the multi-selection drag defect: a press inside a selection of several
/// sessions must not narrow it, the release must select exactly as an undeferred press did, and a
/// drag must swallow the deferral rather than let a later release undo the move's selection.
/// </summary>
public sealed class MainWindowTreeDeferredSelectionTests
{
    [Fact]
    public void PlainPress_InsideAMultiSelection_DefersInsteadOfNarrowingTheSelection()
    {
        TreeInteractionState state = new();
        ServerItemViewModel pressed = CreateServer("pressed");
        List<ServerItemViewModel> selected = [];

        bool consumed = MainWindow.ApplyTreePlainPress(
            state,
            pressed,
            deferSelection: true,
            selected.Add);

        Assert.True(consumed);
        Assert.Empty(selected);
        Assert.True(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? deferred));
        Assert.Same(pressed, deferred);
    }

    [Fact]
    public void PlainPress_OutsideAMultiSelection_StillSelectsOnTheWayDown()
    {
        TreeInteractionState state = new();
        ServerItemViewModel pressed = CreateServer("pressed");
        List<ServerItemViewModel> selected = [];

        bool consumed = MainWindow.ApplyTreePlainPress(
            state,
            pressed,
            deferSelection: false,
            selected.Add);

        Assert.False(consumed);
        Assert.Equal([pressed], selected);
        Assert.False(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? deferred));
        Assert.Null(deferred);
    }

    /// <summary>
    /// The release applies the deferral once. A second up event - the release that ends a
    /// double-click, for instance - must not re-apply a selection the first one already made.
    /// </summary>
    [Fact]
    public void DeferredSelection_IsHandedOutOnlyOnce()
    {
        TreeInteractionState state = new();
        ServerItemViewModel pressed = CreateServer("pressed");

        state.DeferSingleSelection(pressed);

        Assert.True(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? first));
        Assert.Same(pressed, first);
        Assert.False(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? second));
        Assert.Null(second);
    }

    /// <summary>
    /// A drag cancelled mid-flight must leave the selection as the user built it. DoDragDrop
    /// swallows the release that would otherwise have applied the deferral, so the deferral has to
    /// die when the drag returns - whether it dropped, was refused, or was cancelled with Escape.
    /// </summary>
    [Fact]
    public void CancelledDrag_DiscardsTheDeferredSelection()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            ServerItemViewModel pressedServer = Assert.IsType<ServerItemViewModel>(
                pressedContainer.DataContext);
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            state.DeferSingleSelection(pressedServer);
            Assert.True(state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? sourceContainer,
                out object? sourceServer));

            // An executor that returns without dropping is what a cancelled DoDragDrop looks like.
            MainWindow.ExecuteTreeDrag(
                state,
                Assert.IsType<TreeViewItem>(sourceContainer),
                new TreeServerDragPayload(
                    Assert.IsType<ServerItemViewModel>(sourceServer),
                    [Assert.IsType<ServerItemViewModel>(sourceServer)]),
                static (_, _) => { });

            Assert.False(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? deferred));
            Assert.Null(deferred);
        });
    }

    /// <summary>
    /// A deferral that no release ever consumed - the pointer left the tree, or the window lost
    /// activation - must not be applied by the next gesture that happens to come along.
    /// </summary>
    [Fact]
    public void StaleDeferral_DoesNotSurviveTheNextPress()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem firstContainer = Container("first");
            TreeViewItem secondContainer = Container("second");

            state.DeferSingleSelection(
                Assert.IsType<ServerItemViewModel>(firstContainer.DataContext));
            state.CaptureDragCandidate(new Point(10, 10), secondContainer);

            Assert.False(state.TryTakeDeferredSingleSelection(out ServerItemViewModel? deferred));
            Assert.Null(deferred);
        });
    }

    private static Point AboveThreshold(Point startPoint) =>
        new(
            startPoint.X + SystemParameters.MinimumHorizontalDragDistance + 1,
            startPoint.Y + SystemParameters.MinimumVerticalDragDistance + 1);

    private static TreeViewItem Container(string id) =>
        new()
        {
            Header = id,
            DataContext = CreateServer(id)
        };

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
