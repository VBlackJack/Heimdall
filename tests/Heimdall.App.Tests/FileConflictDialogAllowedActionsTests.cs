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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class FileConflictDialogAllowedActionsTests
{
    [Fact]
    public void Rows_ExposeOnlyAllowedActions_AndSignalDirectorySubtreeSkip()
    {
        FileConflictDialogViewModel viewModel = CreateViewModel();

        Assert.Equal(3, viewModel.Rows[0].ConflictOptions.Count);
        Assert.Equal(
            [FileConflictResolutionChoice.Skip, FileConflictResolutionChoice.AutoRename],
            viewModel.Rows[1].ConflictOptions.Select(option => option.Value));
        Assert.Equal(
            [FileConflictResolutionChoice.Skip],
            viewModel.Rows[2].ConflictOptions.Select(option => option.Value));
        Assert.True(viewModel.Rows[2].HasDetail);
        Assert.Equal(FileConflictResolutionChoice.Skip, viewModel.Rows[2].Resolution);
    }

    [Fact]
    public void ApplyToAll_ChangesOnlyRowsThatAllowTheAction()
    {
        FileConflictDialogViewModel viewModel = CreateViewModel();

        viewModel.ApplyAllReplaceCommand.Execute(null);

        Assert.Equal(FileConflictResolutionChoice.Replace, viewModel.Rows[0].Resolution);
        Assert.Equal(FileConflictResolutionChoice.AutoRename, viewModel.Rows[1].Resolution);
        Assert.Equal(FileConflictResolutionChoice.Skip, viewModel.Rows[2].Resolution);

        viewModel.ApplyAllAutoRenameCommand.Execute(null);

        Assert.Equal(FileConflictResolutionChoice.AutoRename, viewModel.Rows[0].Resolution);
        Assert.Equal(FileConflictResolutionChoice.AutoRename, viewModel.Rows[1].Resolution);
        Assert.Equal(FileConflictResolutionChoice.Skip, viewModel.Rows[2].Resolution);
    }

    private static FileConflictDialogViewModel CreateViewModel()
        => new(
        [
            new FileConflictAnalysisItem(
                0,
                "file-file",
                "/target/a",
                true,
                FileConflictItemKind.File,
                FileConflictItemKind.File,
                FileConflictResolutionActions.All),
            new FileConflictAnalysisItem(
                1,
                "file-directory",
                "/target/b",
                true,
                FileConflictItemKind.File,
                FileConflictItemKind.Directory,
                FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename),
            new FileConflictAnalysisItem(
                2,
                "directory-file",
                "/target/c",
                true,
                FileConflictItemKind.Directory,
                FileConflictItemKind.File,
                FileConflictResolutionActions.Skip),
        ],
        localizer: null);
}
