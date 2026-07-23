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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class SessionTreeVirtualizationInteractionTests
{
    [Fact]
    public void RevealPath_IncludesEveryAncestorAndTargetServer()
    {
        var server = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "server-1",
            DisplayName = "Server",
            RemoteServer = "server.example.test"
        });
        var leaf = new FolderViewModel
        {
            Name = "Leaf",
            FullPath = "Root/Leaf"
        };
        leaf.Servers.Add(server);
        var root = new FolderViewModel
        {
            Name = "Root",
            FullPath = "Root"
        };
        root.SubFolders.Add(leaf);

        bool found = TreeInteractionState.TryBuildItemPath(
            [root],
            server,
            out IReadOnlyList<object> path);

        Assert.True(found);
        Assert.Equal([root, leaf, server], path);
    }

    [Fact]
    public void FolderRevealPath_StopsAtRequestedFolder()
    {
        var child = new FolderViewModel
        {
            Name = "Child",
            FullPath = "Root/Child"
        };
        var root = new FolderViewModel
        {
            Name = "Root",
            FullPath = "Root"
        };
        root.SubFolders.Add(child);

        bool found = TreeInteractionState.TryBuildItemPath(
            [root],
            child,
            out IReadOnlyList<object> path);

        Assert.True(found);
        Assert.Equal([root, child], path);
    }

    [Fact]
    public void DragTargetResolution_UsesFolderOrExplicitRootZone()
    {
        var folder = new FolderViewModel
        {
            Name = "Production",
            FullPath = "Root/Production"
        };

        bool folderResolved = TreeInteractionState.TryResolveGroupDropTarget(
            folder,
            acceptsRootTarget: false,
            out string? folderGroup);
        bool rootResolved = TreeInteractionState.TryResolveGroupDropTarget(
            folder: null,
            acceptsRootTarget: true,
            out string? rootGroup);
        bool emptyRejected = TreeInteractionState.TryResolveGroupDropTarget(
            folder: null,
            acceptsRootTarget: false,
            out _);

        Assert.True(folderResolved);
        Assert.Equal("Root/Production", folderGroup);
        Assert.True(rootResolved);
        Assert.Null(rootGroup);
        Assert.False(emptyRejected);
    }
}
