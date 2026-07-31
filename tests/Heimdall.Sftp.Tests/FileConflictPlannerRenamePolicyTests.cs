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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class FileConflictPlannerRenamePolicyTests
{
    [Theory]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.File,
        FileConflictResolutionActions.All)]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.Directory,
        FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.Directory,
        FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.File,
        FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename)]
    public void Analyze_RenamePolicy_MaterializesExpectedActions(
        FileConflictItemKind plannedKind,
        FileConflictItemKind existingKind,
        FileConflictResolutionActions expectedActions)
    {
        FileConflictPlanItem[] items =
        [
            new FileConflictPlanItem("source", "/target", plannedKind),
        ];

        FileConflictAnalysisItem result = FileConflictPlanner.Analyze(
            items,
            _ => existingKind,
            StringComparer.Ordinal,
            FileConflictPolicy.Rename).Single();

        Assert.True(result.HasConflict);
        Assert.Equal(expectedActions, result.AllowedActions);
    }

    [Fact]
    public void Analyze_SameDirectoryPair_ChangesBetweenTransferAndRenamePolicies()
    {
        FileConflictPlanItem[] items =
        [
            new FileConflictPlanItem("source", "/target", FileConflictItemKind.Directory),
        ];

        FileConflictAnalysisItem transfer = FileConflictPlanner.Analyze(
            items,
            _ => FileConflictItemKind.Directory,
            StringComparer.Ordinal,
            FileConflictPolicy.Transfer).Single();
        FileConflictAnalysisItem rename = FileConflictPlanner.Analyze(
            items,
            _ => FileConflictItemKind.Directory,
            StringComparer.Ordinal,
            FileConflictPolicy.Rename).Single();

        Assert.False(transfer.HasConflict);
        Assert.Equal(FileConflictResolutionActions.None, transfer.AllowedActions);
        Assert.True(rename.HasConflict);
        Assert.Equal(
            FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename,
            rename.AllowedActions);
    }
}
