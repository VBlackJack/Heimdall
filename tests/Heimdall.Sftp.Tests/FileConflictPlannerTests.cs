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

public sealed class FileConflictPlannerTests
{
    [Fact]
    public void Analyze_NoExistingOrBatchConflict_ReportsNoConflict()
    {
        FileConflictAnalysisItem item = Assert.Single(FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("source-a", "/target/a.txt")],
            _ => false,
            StringComparer.Ordinal));

        Assert.False(item.HasConflict);
    }

    [Fact]
    public void Analyze_ExistingTarget_ReportsConflict()
    {
        FileConflictAnalysisItem item = Assert.Single(FileConflictPlanner.Analyze(
            [new FileConflictPlanItem("source-a", "/target/a.txt")],
            target => target == "/target/a.txt",
            StringComparer.Ordinal));

        Assert.True(item.HasConflict);
    }

    [Fact]
    public void Analyze_DuplicateTargetInsideBatch_ReportsSecondItemAsConflict()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("source-a", "/target/shared.txt"),
                new FileConflictPlanItem("source-b", "/target/shared.txt"),
            ],
            _ => false,
            StringComparer.Ordinal);

        Assert.False(analysis[0].HasConflict);
        Assert.True(analysis[1].HasConflict);
    }

    [Fact]
    public void Analyze_CaseEquivalentTargetsWithOrdinalIgnoreCase_ReportsConflict()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("source-a", @"C:\\target\\A.txt"),
                new FileConflictPlanItem("source-b", @"C:\\target\\a.txt"),
            ],
            _ => false,
            StringComparer.OrdinalIgnoreCase);

        Assert.False(analysis[0].HasConflict);
        Assert.True(analysis[1].HasConflict);
    }

    [Fact]
    public void Analyze_CaseEquivalentTargetsWithOrdinal_ReportsNoConflict()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("source-a", "/target/A.txt"),
                new FileConflictPlanItem("source-b", "/target/a.txt"),
            ],
            _ => false,
            StringComparer.Ordinal);

        Assert.All(analysis, item => Assert.False(item.HasConflict));
    }

    [Fact]
    public void Resolve_AutoRenameSkipsExistingAndBatchReservedDerivedTargets()
    {
        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            [
                new FileConflictPlanItem("source-report", "/target/report.txt"),
                new FileConflictPlanItem("source-reserved", "/target/report (copy 2).txt"),
            ],
            target => target is "/target/report.txt" or "/target/report (copy).txt",
            StringComparer.Ordinal);

        IReadOnlyList<FileConflictResolvedItem> resolved = FileConflictPlanner.Resolve(
            analysis,
            [new FileConflictDecision(0, FileConflictResolutionChoice.AutoRename)],
            target => target is "/target/report.txt" or "/target/report (copy).txt",
            StringComparer.Ordinal);

        Assert.Equal(FileConflictEffectiveAction.ProceedToNewTarget, resolved[0].Action);
        Assert.Equal("/target/report (copy 3).txt", resolved[0].EffectiveTargetPath);
    }
}
