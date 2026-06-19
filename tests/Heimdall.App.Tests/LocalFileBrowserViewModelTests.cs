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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

public sealed class LocalFileBrowserViewModelTests
{
    [Fact]
    public void DetermineRenameCollisionAction_NoCollision_ReturnsNone()
    {
        var action = LocalFileBrowserViewModel.DetermineRenameCollisionAction(
            isDirectory: false,
            sourceFullPath: @"C:\Temp\File.txt",
            newPath: @"C:\Temp\Renamed.txt",
            targetFileExists: false,
            targetDirectoryExists: false);

        Assert.Equal(LocalRenameCollisionAction.None, action);
    }

    [Fact]
    public void DetermineRenameCollisionAction_CaseOnlyRename_ReturnsNone()
    {
        var action = LocalFileBrowserViewModel.DetermineRenameCollisionAction(
            isDirectory: false,
            sourceFullPath: @"C:\Temp\File.txt",
            newPath: @"C:\Temp\file.txt",
            targetFileExists: true,
            targetDirectoryExists: false);

        Assert.Equal(LocalRenameCollisionAction.None, action);
    }

    [Fact]
    public void DetermineRenameCollisionAction_FileTargetExists_RequiresOverwriteConfirmation()
    {
        var action = LocalFileBrowserViewModel.DetermineRenameCollisionAction(
            isDirectory: false,
            sourceFullPath: @"C:\Temp\File.txt",
            newPath: @"C:\Temp\Existing.txt",
            targetFileExists: true,
            targetDirectoryExists: false);

        Assert.Equal(LocalRenameCollisionAction.ConfirmOverwriteFile, action);
    }

    [Fact]
    public void DetermineRenameCollisionAction_DirectoryTargetExists_BlocksRename()
    {
        var action = LocalFileBrowserViewModel.DetermineRenameCollisionAction(
            isDirectory: false,
            sourceFullPath: @"C:\Temp\File.txt",
            newPath: @"C:\Temp\Existing",
            targetFileExists: false,
            targetDirectoryExists: true);

        Assert.Equal(LocalRenameCollisionAction.BlockExistingTarget, action);
    }

    [Fact]
    public void DetermineRenameCollisionAction_DirectoryRenameTargetExists_BlocksRename()
    {
        var action = LocalFileBrowserViewModel.DetermineRenameCollisionAction(
            isDirectory: true,
            sourceFullPath: @"C:\Temp\Folder",
            newPath: @"C:\Temp\Existing",
            targetFileExists: true,
            targetDirectoryExists: false);

        Assert.Equal(LocalRenameCollisionAction.BlockExistingTarget, action);
    }
}
