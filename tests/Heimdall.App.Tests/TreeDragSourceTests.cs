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

public sealed partial class TreeDragSourceTests
{
    [Fact]
    public void DragThreshold_UsesPressedServerInsteadOfHoveredServer()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            TreeViewItem hoveredContainer = Container("hovered");
            ServerItemViewModel pressedServer = Assert.IsType<ServerItemViewModel>(
                pressedContainer.DataContext);
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? resolvedContainer,
                out object? resolvedServer);

            Assert.True(started);
            Assert.Same(pressedContainer, resolvedContainer);
            Assert.Same(pressedServer, resolvedServer);
            Assert.NotSame(hoveredContainer, resolvedContainer);
        });
    }

    [Fact]
    public void InvalidPress_ClearsPreviousCandidateAndCannotStartFromHoveredServer()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            TreeViewItem folderContainer = new()
            {
                Header = "folder",
                DataContext = new FolderViewModel
                {
                    Name = "No group",
                    FullPath = ""
                }
            };
            TreeViewItem hoveredContainer = Container("hovered");
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            state.CaptureDragCandidate(startPoint, folderContainer);
            bool folderStarted = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? folderSource,
                out object? folderServer);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            state.CaptureDragCandidate(startPoint, pressedContainer: null);
            bool blankStarted = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? blankSource,
                out object? blankServer);

            Assert.False(folderStarted);
            Assert.Null(folderSource);
            Assert.Null(folderServer);
            Assert.False(blankStarted);
            Assert.Null(blankSource);
            Assert.Null(blankServer);
            Assert.NotSame(hoveredContainer, folderSource);
            Assert.NotSame(hoveredContainer, blankSource);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void InvalidMove_FailsClosedAndClearsCandidate(
        bool isLeftButtonPressed,
        bool hasDisallowedModifiers)
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed,
                hasDisallowedModifiers,
                out TreeViewItem? resolvedContainer,
                out object? resolvedServer);

            Assert.False(started);
            Assert.Null(resolvedContainer);
            Assert.Null(resolvedServer);
            Assert.False(state.DragInProgress);
        });
    }

    [Fact]
    public void ContainerDataContextChangedAfterPress_FailsClosed()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            TreeViewItem replacementContainer = Container("replacement");
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            pressedContainer.DataContext = replacementContainer.DataContext;
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? resolvedContainer,
                out object? resolvedServer);

            Assert.False(started);
            Assert.Null(resolvedContainer);
            Assert.Null(resolvedServer);
            Assert.False(state.DragInProgress);
        });
    }

    [Fact]
    public void ExecuteTreeDrag_WhenExecutorThrows_ResetsStateInFinally()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            ServerItemViewModel pressedServer = Assert.IsType<ServerItemViewModel>(
                pressedContainer.DataContext);
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, pressedContainer);
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? resolvedContainer,
                out object? resolvedServer);

            Assert.True(started);
            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
                MainWindow.ExecuteTreeDrag(
                    state,
                    Assert.IsType<TreeViewItem>(resolvedContainer),
                    new TreeServerDragPayload(
                        Assert.IsType<ServerItemViewModel>(resolvedServer),
                        [Assert.IsType<ServerItemViewModel>(resolvedServer)]),
                    (source, data) =>
                    {
                        Assert.Same(pressedContainer, source);
                        TreeServerDragPayload payload = Assert.IsType<TreeServerDragPayload>(
                            data.GetData(TreeServerDragPayload.DataFormat));
                        Assert.Same(pressedServer, payload.Source);
                        throw new InvalidOperationException("drag failed");
                    }));

            Assert.Equal("drag failed", failure.Message);
            Assert.False(state.DragInProgress);

            bool staleStarted = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out _,
                out _);
            Assert.False(staleStarted);
        });
    }

    /// <summary>
    /// A drag that starts on one of eight selected rows has to hand the drop all eight. The payload
    /// used to be the pressed session on its own, so the drop moved one row and the user was told
    /// nothing about the seven that stayed.
    /// </summary>
    [Fact]
    public void ExecuteTreeDrag_CarriesEverySessionTheDragWasGiven()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            TreeViewItem pressedContainer = Container("pressed");
            ServerItemViewModel pressedServer = Assert.IsType<ServerItemViewModel>(
                pressedContainer.DataContext);
            ServerItemViewModel second = Assert.IsType<ServerItemViewModel>(
                Container("second").DataContext);
            ServerItemViewModel third = Assert.IsType<ServerItemViewModel>(
                Container("third").DataContext);
            TreeServerDragPayload? delivered = null;

            MainWindow.ExecuteTreeDrag(
                state,
                pressedContainer,
                new TreeServerDragPayload(pressedServer, [second, pressedServer, third]),
                (_, data) => delivered =
                    data.GetData(TreeServerDragPayload.DataFormat) as TreeServerDragPayload);

            Assert.NotNull(delivered);
            Assert.Same(pressedServer, delivered.Source);
            Assert.Equal(
                ["second", "pressed", "third"],
                delivered.Servers.Select(server => server.Id).ToArray());
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
            DataContext = ServerItemViewModel.FromDto(new ServerProfileDto
            {
                Id = id,
                DisplayName = id,
                RemoteServer = $"{id}.example.test",
                ConnectionType = "SSH"
            })
        };

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
