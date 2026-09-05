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
using System.Windows.Controls;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// A named folder can be the source of a drag; the "no group" folder cannot.
/// </summary>
public sealed partial class TreeDragSourceTests
{
    [Fact]
    public void NamedFolderPress_StartsADragCarryingTheFolder()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            FolderViewModel folder = new() { Name = "Linux", FullPath = "Prod/Linux" };
            TreeViewItem folderContainer = new() { Header = folder.Name, DataContext = folder };
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, folderContainer);
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? sourceContainer,
                out object? sourceItem);

            Assert.True(started);
            Assert.Same(folderContainer, sourceContainer);
            Assert.Same(folder, sourceItem);
        });
    }

    [Fact]
    public void NoGroupFolderPress_LeavesNoCandidate()
    {
        RunOnSta(() =>
        {
            TreeInteractionState state = new();
            FolderViewModel noGroup = new() { Name = "No group", FullPath = "" };
            TreeViewItem folderContainer = new() { Header = noGroup.Name, DataContext = noGroup };
            Point startPoint = new(10, 10);

            state.CaptureDragCandidate(startPoint, folderContainer);
            bool started = state.TryStartDrag(
                AboveThreshold(startPoint),
                isLeftButtonPressed: true,
                hasDisallowedModifiers: false,
                out TreeViewItem? sourceContainer,
                out object? sourceItem);

            Assert.False(started);
            Assert.Null(sourceContainer);
            Assert.Null(sourceItem);
        });
    }

    [Fact]
    public void IsDragSource_AcceptsSessionsAndNamedFoldersOnly()
    {
        Assert.True(TreeInteractionState.IsDragSource(new FolderViewModel { Name = "Prod", FullPath = "Prod" }));
        Assert.False(TreeInteractionState.IsDragSource(new FolderViewModel { Name = "No group", FullPath = "" }));
        Assert.False(TreeInteractionState.IsDragSource(null));
        Assert.False(TreeInteractionState.IsDragSource("a string"));
    }
}
