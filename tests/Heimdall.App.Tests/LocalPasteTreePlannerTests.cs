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

#nullable enable

using System.IO;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class LocalPasteTreePlannerTests
{
    [Theory]
    [InlineData(@"C:\data", @"C:\data", true)]
    [InlineData(@"C:\data", @"C:\DATA", true)]
    [InlineData(@"C:\data\", @"C:\data", true)]
    [InlineData(@"C:\data", @"C:\data\backup", true)]
    [InlineData(@"C:\data", @"C:\data\a\b\c", true)]
    [InlineData(@"C:\data", @"C:\database", false)]
    [InlineData(@"C:\data\sub", @"C:\data", false)]
    [InlineData(@"C:\data", @"C:\elsewhere", false)]
    [InlineData("C:/data", @"C:\data\backup", true)]
    [InlineData(@"C:\", @"C:\anything", true)]
    public void IsSameOrDescendantPath_ReturnsExpected(
        string sourcePath,
        string targetDirectory,
        bool expected)
    {
        bool result = LocalPasteTreePlanner.IsSameOrDescendantPath(sourcePath, targetDirectory);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Plan_FileRoot_EmitsSingleCopyOp()
    {
        LocalPasteEntry root = new(@"C:\Source\report.txt", "report.txt", IsDirectory: false);

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            _ => throw new InvalidOperationException("Files must not be enumerated."));

        LocalPasteOp operation = Assert.Single(operations);
        Assert.Equal(LocalPasteOpKind.CopyFile, operation.Kind);
        Assert.Equal(root.FullPath, operation.SourcePath);
        Assert.Equal(Path.Combine(@"C:\Target", root.Name), operation.TargetPath);
    }

    [Fact]
    public void Plan_DirectoryRoot_EmitsCreateDirectoryBeforeChildren()
    {
        LocalPasteEntry root = new(@"C:\Source\Docs", "Docs", IsDirectory: true);
        LocalPasteEntry child = new(@"C:\Source\Docs\readme.txt", "readme.txt", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [root.FullPath] = [child],
        };

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            path => tree[path]);

        Assert.Collection(
            operations,
            operation => Assert.Equal(LocalPasteOpKind.CreateDirectory, operation.Kind),
            operation => Assert.Equal(LocalPasteOpKind.CopyFile, operation.Kind));
    }

    [Fact]
    public void Plan_NestedTree_EmitsEveryDescendantUnderItsRelativePath()
    {
        LocalPasteEntry root = new(@"C:\Source\Project", "Project", IsDirectory: true);
        LocalPasteEntry rootFile = new(@"C:\Source\Project\root.txt", "root.txt", IsDirectory: false);
        LocalPasteEntry subdirectory = new(@"C:\Source\Project\Assets", "Assets", IsDirectory: true);
        LocalPasteEntry nestedFile = new(@"C:\Source\Project\Assets\logo.png", "logo.png", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [root.FullPath] = [rootFile, subdirectory],
            [subdirectory.FullPath] = [nestedFile],
        };

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            path => tree[path]);

        string projectTarget = Path.Combine(@"C:\Target", root.Name);
        string assetsTarget = Path.Combine(projectTarget, subdirectory.Name);
        Assert.Equal(
            [
                new LocalPasteOp(LocalPasteOpKind.CreateDirectory, root.FullPath, projectTarget),
                new LocalPasteOp(LocalPasteOpKind.CopyFile, rootFile.FullPath, Path.Combine(projectTarget, rootFile.Name)),
                new LocalPasteOp(LocalPasteOpKind.CreateDirectory, subdirectory.FullPath, assetsTarget),
                new LocalPasteOp(LocalPasteOpKind.CopyFile, nestedFile.FullPath, Path.Combine(assetsTarget, nestedFile.Name)),
            ],
            operations);
    }

    [Fact]
    public void Plan_ChildReparsePointDirectory_IsSkippedWithItsSubtree()
    {
        LocalPasteEntry root = new(@"C:\Source\Root", "Root", IsDirectory: true);
        LocalPasteEntry link = new(@"C:\Source\Root\Link", "Link", IsDirectory: true, IsReparsePoint: true);
        LocalPasteEntry linkedFile = new(@"C:\Source\Root\Link\hidden.txt", "hidden.txt", IsDirectory: false);
        LocalPasteEntry visibleFile = new(@"C:\Source\Root\visible.txt", "visible.txt", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [root.FullPath] = [link, visibleFile],
            [link.FullPath] = [linkedFile],
        };
        int enumerationCount = 0;

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            path =>
            {
                enumerationCount++;
                return tree[path];
            });

        Assert.Equal(1, enumerationCount);
        Assert.Equal(2, operations.Count);
        Assert.DoesNotContain(operations, operation => operation.SourcePath == link.FullPath);
        Assert.DoesNotContain(operations, operation => operation.SourcePath == linkedFile.FullPath);
    }

    /// <remarks>A file link inside a pasted tree was copied by value, as its target's bytes.</remarks>
    [Fact]
    public void Plan_ChildReparsePointFile_IsSkipped()
    {
        LocalPasteEntry root = new(@"C:\Source\Root", "Root", IsDirectory: true, IsReparsePoint: false);
        LocalPasteEntry link = new(@"C:\Source\Root\link.txt", "link.txt", IsDirectory: false, IsReparsePoint: true);
        LocalPasteEntry file = new(@"C:\Source\Root\real.txt", "real.txt", IsDirectory: false, IsReparsePoint: false);

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            _ => [link, file]);

        Assert.DoesNotContain(operations, op => op.SourcePath.EndsWith("link.txt", StringComparison.Ordinal));
        Assert.Contains(operations, op => op.SourcePath.EndsWith("real.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Plan_RootReparsePointDirectory_IsPlanned()
    {
        LocalPasteEntry root = new(@"C:\Source\Link", "Link", IsDirectory: true, IsReparsePoint: true);
        LocalPasteEntry child = new(@"C:\Source\Link\child.txt", "child.txt", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [root.FullPath] = [child],
        };

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            path => tree[path]);

        Assert.Collection(
            operations,
            operation => Assert.Equal(LocalPasteOpKind.CreateDirectory, operation.Kind),
            operation => Assert.Equal(LocalPasteOpKind.CopyFile, operation.Kind));
    }

    [Fact]
    public void Plan_DepthReachingMaxCopyDepth_ThrowsIOException()
    {
        LocalPasteEntry root = new(@"C:\Source\Root", "Root", IsDirectory: true);
        int enumerationCount = 0;

        IOException exception = Assert.Throws<IOException>(() => LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            _ =>
            {
                enumerationCount++;
                string childName = $"Level{enumerationCount}";
                return [new LocalPasteEntry($@"C:\Source\{childName}", childName, IsDirectory: true)];
            }));

        Assert.Equal(LocalPasteTreePlanner.MaxCopyDepth, enumerationCount);
        Assert.Contains(LocalPasteTreePlanner.MaxCopyDepth.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_MultipleRoots_PreservesRootOrder()
    {
        LocalPasteEntry first = new(@"C:\Source\first.txt", "first.txt", IsDirectory: false);
        LocalPasteEntry second = new(@"C:\Source\Second", "Second", IsDirectory: true);
        LocalPasteEntry third = new(@"C:\Source\third.txt", "third.txt", IsDirectory: false);
        Dictionary<string, IReadOnlyList<LocalPasteEntry>> tree = new()
        {
            [second.FullPath] = [],
        };

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [first, second, third],
            @"C:\Target",
            path => tree[path]);

        Assert.Equal([first.FullPath, second.FullPath, third.FullPath], operations.Select(operation => operation.SourcePath));
    }

    [Fact]
    public void Plan_EmptyDirectory_EmitsCreateDirectoryOnly()
    {
        LocalPasteEntry root = new(@"C:\Source\Empty", "Empty", IsDirectory: true);

        IReadOnlyList<LocalPasteOp> operations = LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            _ => []);

        LocalPasteOp operation = Assert.Single(operations);
        Assert.Equal(LocalPasteOpKind.CreateDirectory, operation.Kind);
        Assert.Equal(root.FullPath, operation.SourcePath);
    }

    [Fact]
    public void Plan_FileEntry_DoesNotCallEnumerateChildren()
    {
        LocalPasteEntry root = new(@"C:\Source\file.txt", "file.txt", IsDirectory: false);
        int enumerationCount = 0;

        LocalPasteTreePlanner.Plan(
            [root],
            @"C:\Target",
            _ =>
            {
                enumerationCount++;
                return [];
            });

        Assert.Equal(0, enumerationCount);
    }

    [Fact]
    public void Plan_NullOrBlankArguments_Throw()
    {
        IReadOnlyList<LocalPasteEntry> roots = [];
        Func<string, IReadOnlyList<LocalPasteEntry>> enumerateChildren = _ => [];

        Assert.Throws<ArgumentNullException>(() => LocalPasteTreePlanner.Plan(null!, @"C:\Target", enumerateChildren));
        Assert.Throws<ArgumentException>(() => LocalPasteTreePlanner.Plan(roots, " ", enumerateChildren));
        Assert.Throws<ArgumentNullException>(() => LocalPasteTreePlanner.Plan(roots, @"C:\Target", null!));
    }
}
