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

namespace Heimdall.Sftp.Tests;

public sealed class FileConflictPlannerKindTests
{
    [Theory]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.File,
        true,
        FileConflictResolutionActions.All)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.Directory,
        false,
        FileConflictResolutionActions.None)]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.Directory,
        true,
        FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.File,
        true,
        FileConflictResolutionActions.Skip)]
    public void Analyze_ExistingTarget_AppliesKindMatrix(
        FileConflictItemKind plannedKind,
        FileConflictItemKind existingKind,
        bool expectedConflict,
        FileConflictResolutionActions expectedActions)
    {
        FileConflictAnalysisItem item = Assert.Single(FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("source", "/target/item", plannedKind)],
            _ => existingKind,
            StringComparer.Ordinal));

        Assert.Equal(expectedConflict, item.HasConflict);
        Assert.Equal(existingKind, item.ExistingTargetKind);
        Assert.Equal(expectedActions, item.AllowedActions);
    }

    [Theory]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.File,
        true,
        FileConflictResolutionActions.All)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.Directory,
        false,
        FileConflictResolutionActions.None)]
    [InlineData(
        FileConflictItemKind.Directory,
        FileConflictItemKind.File,
        true,
        FileConflictResolutionActions.Skip | FileConflictResolutionActions.AutoRename)]
    [InlineData(
        FileConflictItemKind.File,
        FileConflictItemKind.Directory,
        true,
        FileConflictResolutionActions.Skip)]
    public void Analyze_IntraBatchTarget_AppliesKindMatrix(
        FileConflictItemKind firstKind,
        FileConflictItemKind secondKind,
        bool expectedConflict,
        FileConflictResolutionActions expectedActions)
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("first", "/target/item", firstKind),
                new FileConflictPlanItem("second", "/target/item", secondKind),
            ],
            _ => (FileConflictItemKind?)null,
            StringComparer.Ordinal);

        Assert.False(analysis[0].HasConflict);
        Assert.Equal(expectedConflict, analysis[1].HasConflict);
        Assert.Equal(firstKind, analysis[1].ExistingTargetKind);
        Assert.Equal(expectedActions, analysis[1].AllowedActions);
    }

    [Fact]
    public void Resolve_SkippedDirectory_ExcludesDescendantsButNotTextualPrefixPeer()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("directory-a", "/a", FileConflictItemKind.Directory),
                new FileConflictPlanItem("child-a", "/a/child.txt"),
                new FileConflictPlanItem("peer-ab", "/ab/child.txt"),
            ],
            path => path == "/a" ? FileConflictItemKind.File : null,
            StringComparer.Ordinal);

        IReadOnlyList<FileConflictResolvedItem> resolved = FileConflictPlanner.Resolve(
            analysis,
            [new FileConflictDecision(0, FileConflictResolutionChoice.Skip)],
            path => path == "/a",
            StringComparer.Ordinal);

        Assert.Equal(FileConflictEffectiveAction.Skip, resolved[0].Action);
        Assert.Equal(FileConflictEffectiveAction.Skip, resolved[1].Action);
        Assert.Equal(FileConflictEffectiveAction.Proceed, resolved[2].Action);
    }

    [Fact]
    public void Resolve_ForbiddenCrossKindReplace_Throws()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("file", "/target/item", FileConflictItemKind.File)],
            _ => FileConflictItemKind.Directory,
            StringComparer.Ordinal);

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            FileConflictPlanner.Resolve(
                analysis,
                [new FileConflictDecision(0, FileConflictResolutionChoice.Replace)],
                _ => true,
                StringComparer.Ordinal));

        Assert.Contains("not allowed", exception.Message, StringComparison.Ordinal);
    }
}
