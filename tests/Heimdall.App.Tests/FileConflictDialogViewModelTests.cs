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

public sealed class FileConflictDialogViewModelTests
{
    [Fact]
    public void ApplyAllCommands_UpdateEveryRow_AndApplyReturnsEveryDecision()
    {
        FileConflictDialogViewModel viewModel = CreateViewModel();
        bool? accepted = null;
        viewModel.CloseRequested += result => accepted = result;

        viewModel.ApplyAllSkipCommand.Execute(null);
        Assert.All(viewModel.Rows, row => Assert.Equal(FileConflictResolutionChoice.Skip, row.Resolution));
        viewModel.ApplyAllReplaceCommand.Execute(null);
        Assert.All(viewModel.Rows, row => Assert.Equal(FileConflictResolutionChoice.Replace, row.Resolution));
        viewModel.ApplyAllAutoRenameCommand.Execute(null);
        Assert.All(viewModel.Rows, row => Assert.Equal(FileConflictResolutionChoice.AutoRename, row.Resolution));

        viewModel.ApplyCommand.Execute(null);

        Assert.True(accepted);
        Assert.NotNull(viewModel.Result);
        Assert.Equal(2, viewModel.Result!.Decisions.Count);
        Assert.All(
            viewModel.Result.Decisions,
            decision => Assert.Equal(FileConflictResolutionChoice.AutoRename, decision.Choice));
    }

    [Fact]
    public void Cancel_ClearsResult_AndRequestsWholeDialogClose()
    {
        FileConflictDialogViewModel viewModel = CreateViewModel();
        bool? accepted = null;
        viewModel.CloseRequested += result => accepted = result;

        viewModel.ApplyCommand.Execute(null);
        viewModel.CancelCommand.Execute(null);

        Assert.False(accepted);
        Assert.Null(viewModel.Result);
    }

    private static FileConflictDialogViewModel CreateViewModel()
        => new(
        [
            new FileConflictAnalysisItem(2, "/remote/a.txt", "C:\\target\\a.txt", true),
            new FileConflictAnalysisItem(5, "/remote/b.txt", "C:\\target\\b.txt", true),
        ],
        localizer: null);
}
