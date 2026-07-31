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

using System.IO;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Sftp;

namespace Heimdall.App.Tests;

public sealed class LocalPasteConflictPlanningTests
{
    [Fact]
    public void BuildConflictPlanItems_CopyFileOp_MapsToFileKind()
    {
        LocalPasteOp operation = new(
            LocalPasteOpKind.CopyFile,
            @"C:\Source\file.txt",
            @"C:\Target\file.txt");

        FileConflictPlanItem item = Assert.Single(
            LocalFileBrowserViewModel.BuildConflictPlanItems([operation]));

        Assert.Equal(FileConflictItemKind.File, item.Kind);
        Assert.Equal(operation.SourcePath, item.SourceIdentity);
        Assert.Equal(operation.TargetPath, item.TargetPath);
    }

    [Fact]
    public void BuildConflictPlanItems_CreateDirectoryOp_MapsToDirectoryKind()
    {
        LocalPasteOp operation = new(
            LocalPasteOpKind.CreateDirectory,
            @"C:\Source\Folder",
            @"C:\Target\Folder");

        FileConflictPlanItem item = Assert.Single(
            LocalFileBrowserViewModel.BuildConflictPlanItems([operation]));

        Assert.Equal(FileConflictItemKind.Directory, item.Kind);
        Assert.Equal(operation.SourcePath, item.SourceIdentity);
        Assert.Equal(operation.TargetPath, item.TargetPath);
    }

    [Fact]
    public void BuildConflictPlanItems_PreservesOrderAndSourceIdentity()
    {
        IReadOnlyList<LocalPasteOp> operations =
        [
            new(LocalPasteOpKind.CreateDirectory, @"C:\Source\Folder", @"C:\Target\Folder"),
            new(LocalPasteOpKind.CopyFile, @"C:\Source\Folder\first.txt", @"C:\Target\Folder\first.txt"),
            new(LocalPasteOpKind.CopyFile, @"C:\Source\second.txt", @"C:\Target\second.txt"),
        ];

        IReadOnlyList<FileConflictPlanItem> items =
            LocalFileBrowserViewModel.BuildConflictPlanItems(operations);

        Assert.Equal(operations.Select(operation => operation.SourcePath), items.Select(item => item.SourceIdentity));
        Assert.Equal(operations.Select(operation => operation.TargetPath), items.Select(item => item.TargetPath));
    }

    [Fact]
    public void ShouldOverwriteOnCopy_NonConflictingProceed_ReturnsFalse()
    {
        FileConflictAnalysisItem analysis = CreateAnalysis(hasConflict: false);

        bool overwrite = LocalFileBrowserViewModel.ShouldOverwriteOnCopy(
            analysis,
            FileConflictEffectiveAction.Proceed);

        Assert.False(overwrite);
    }

    [Fact]
    public void ShouldOverwriteOnCopy_ConflictingProceed_ReturnsTrue()
    {
        FileConflictAnalysisItem analysis = CreateAnalysis(hasConflict: true);

        bool overwrite = LocalFileBrowserViewModel.ShouldOverwriteOnCopy(
            analysis,
            FileConflictEffectiveAction.Proceed);

        Assert.True(overwrite);
    }

    [Fact]
    public void ShouldOverwriteOnCopy_ProceedToNewTarget_ReturnsFalse()
    {
        FileConflictAnalysisItem analysis = CreateAnalysis(hasConflict: true);

        bool overwrite = LocalFileBrowserViewModel.ShouldOverwriteOnCopy(
            analysis,
            FileConflictEffectiveAction.ProceedToNewTarget);

        Assert.False(overwrite);
    }

    [Fact]
    public void NestedFileCollision_ProducesOneConflictRowPerCollidingNestedFile()
    {
        LocalPasteEntry root = new(@"C:\Source\Project", "Project", IsDirectory: true);
        LocalPasteEntry subdirectory = new(@"C:\Source\Project\Data", "Data", IsDirectory: true);
        LocalPasteEntry firstFile = new(@"C:\Source\Project\Data\first.txt", "first.txt", IsDirectory: false);
        LocalPasteEntry secondFile = new(@"C:\Source\Project\Data\second.txt", "second.txt", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [root.FullPath] = [subdirectory],
            [subdirectory.FullPath] = [firstFile, secondFile],
        };
        string projectTarget = Path.Combine(@"C:\Target", root.Name);
        string dataTarget = Path.Combine(projectTarget, subdirectory.Name);
        HashSet<string> existingDirectories = new(StringComparer.OrdinalIgnoreCase)
        {
            projectTarget,
            dataTarget,
        };
        HashSet<string> existingFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(dataTarget, firstFile.Name),
            Path.Combine(dataTarget, secondFile.Name),
        };
        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            path => tree[path]);
        IReadOnlyList<FileConflictPlanItem> planItems =
            LocalFileBrowserViewModel.BuildConflictPlanItems(operations);

        IReadOnlyList<FileConflictAnalysisItem> analysis = FileConflictPlanner.Analyze(
            planItems,
            path => existingFiles.Contains(path)
                ? FileConflictItemKind.File
                : existingDirectories.Contains(path)
                    ? FileConflictItemKind.Directory
                    : null,
            StringComparer.OrdinalIgnoreCase,
            FileConflictPolicy.Transfer);
        IReadOnlyList<FileConflictAnalysisItem> conflicts = analysis
            .Where(item => item.HasConflict)
            .ToList();

        Assert.Equal(2, analysis.Count(item => item.PlannedKind == FileConflictItemKind.Directory));
        Assert.DoesNotContain(
            analysis,
            item => item.PlannedKind == FileConflictItemKind.Directory && item.HasConflict);
        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, item => Assert.Equal(FileConflictItemKind.File, item.PlannedKind));
        Assert.All(conflicts, item => Assert.Equal(FileConflictResolutionActions.All, item.AllowedActions));
    }

    [Fact]
    public void DirectoryOnDirectory_RaisesNoConflict()
    {
        LocalPasteOp operation = new(
            LocalPasteOpKind.CreateDirectory,
            @"C:\Source\Folder",
            @"C:\Target\Folder");
        IReadOnlyList<FileConflictPlanItem> planItems =
            LocalFileBrowserViewModel.BuildConflictPlanItems([operation]);

        FileConflictAnalysisItem analysis = Assert.Single(FileConflictPlanner.Analyze(
            planItems,
            _ => FileConflictItemKind.Directory,
            StringComparer.OrdinalIgnoreCase,
            FileConflictPolicy.Transfer));

        Assert.False(analysis.HasConflict);
        Assert.Equal(FileConflictResolutionActions.None, analysis.AllowedActions);
    }

    private static FileConflictAnalysisItem CreateAnalysis(bool hasConflict)
        => new(
            0,
            @"C:\Source\file.txt",
            @"C:\Target\file.txt",
            hasConflict,
            FileConflictItemKind.File,
            hasConflict ? FileConflictItemKind.File : null,
            hasConflict ? FileConflictResolutionActions.All : FileConflictResolutionActions.None);
}
