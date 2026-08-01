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
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Unit tests for the pure <see cref="RemoteCopyPlanner"/> orchestration: non-overwrite protection,
/// the directory-requires-recursive rule, file-vs-directory dispatch, recursive fan-out, and
/// child-name validation. Transport is faked entirely in memory through <see cref="RemoteCopyOps"/>.
/// </summary>
public sealed class RemoteCopyPlannerTests
{
    [Fact]
    public async Task CopyAsync_DestinationExists_ThrowsIOExceptionAndCopiesNothing()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.ExistingDestinations.Add("/srv/dst.txt");

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync("/srv/src.txt", "/srv/dst.txt", recursive: false, tree.BuildOps()));

        Assert.Empty(tree.CopiedFiles);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_DestinationEqualsSource_ThrowsIOExceptionAndCopiesNothing()
    {
        FakeRemoteTree tree = new FakeRemoteTree();

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync("/srv/data", "/srv/data", recursive: true, tree.BuildOps()));

        Assert.Empty(tree.CopiedFiles);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_DestinationIsDescendantOfSource_ThrowsIOExceptionAndCopiesNothing()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.Directories.Add("/srv/data");

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync(
                "/srv/data",
                "/srv/data/backup",
                recursive: true,
                tree.BuildOps()));

        Assert.Empty(tree.CopiedFiles);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_DestinationSharesPrefixButIsNotDescendant_Succeeds()
    {
        FakeRemoteTree tree = new FakeRemoteTree();

        await RemoteCopyPlanner.CopyAsync(
            "/srv/data",
            "/srv/database",
            recursive: false,
            tree.BuildOps());

        Assert.Equal(
            ("/srv/data", "/srv/database"),
            Assert.Single(tree.CopiedFiles));
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_DestinationIsAncestorOfSource_Succeeds()
    {
        FakeRemoteTree tree = new FakeRemoteTree();

        await RemoteCopyPlanner.CopyAsync(
            "/srv/data/sub",
            "/srv/out",
            recursive: false,
            tree.BuildOps());

        Assert.Equal(
            ("/srv/data/sub", "/srv/out"),
            Assert.Single(tree.CopiedFiles));
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_SourceHasTrailingSlashAndDestinationIsDescendant_ThrowsIOException()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.Directories.Add("/srv/data/");

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync(
                "/srv/data/",
                "/srv/data/backup",
                recursive: true,
                tree.BuildOps()));

        Assert.Empty(tree.CopiedFiles);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public void IsSameOrDescendantPath_RootSource_RejectsEveryDestination()
    {
        Assert.True(RemoteCopyPlanner.IsSameOrDescendantPath("/", "/srv/data"));
        Assert.True(RemoteCopyPlanner.IsSameOrDescendantPath("/", "/"));
        Assert.True(RemoteCopyPlanner.IsSameOrDescendantPath("/", "/a/b/c"));
    }

    [Fact]
    public async Task CopyAsync_DirectorySourceWithoutRecursive_ThrowsIOException()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.Directories.Add("/srv/data");

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync("/srv/data", "/srv/data-copy", recursive: false, tree.BuildOps()));

        Assert.Empty(tree.CopiedFiles);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_FileSource_CopiesSingleFileWithoutCreatingDirectory()
    {
        FakeRemoteTree tree = new FakeRemoteTree();

        await RemoteCopyPlanner.CopyAsync("/srv/a.txt", "/srv/b.txt", recursive: false, tree.BuildOps());

        (string Source, string Destination) copied = Assert.Single(tree.CopiedFiles);
        Assert.Equal(("/srv/a.txt", "/srv/b.txt"), copied);
        Assert.Empty(tree.CreatedDirectories);
    }

    [Fact]
    public async Task CopyAsync_DirectoryRecursive_CreatesDestinationTreeAndCopiesFiles()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.Directories.Add("/srv/data");
        tree.Directories.Add("/srv/data/sub");
        tree.Children["/srv/data"] = ["file1.txt", "sub"];
        tree.Children["/srv/data/sub"] = ["file2.txt"];

        await RemoteCopyPlanner.CopyAsync("/srv/data", "/srv/copy", recursive: true, tree.BuildOps());

        Assert.Equal(["/srv/copy", "/srv/copy/sub"], tree.CreatedDirectories);
        Assert.Contains(("/srv/data/file1.txt", "/srv/copy/file1.txt"), tree.CopiedFiles);
        Assert.Contains(("/srv/data/sub/file2.txt", "/srv/copy/sub/file2.txt"), tree.CopiedFiles);
        Assert.Equal(2, tree.CopiedFiles.Count);
    }

    [Fact]
    public async Task CopyAsync_UnsafeChildName_ThrowsIOException()
    {
        FakeRemoteTree tree = new FakeRemoteTree();
        tree.Directories.Add("/srv/data");
        tree.Children["/srv/data"] = ["../escape"];

        await Assert.ThrowsAsync<IOException>(() =>
            RemoteCopyPlanner.CopyAsync("/srv/data", "/srv/copy", recursive: true, tree.BuildOps()));
    }

    [Fact]
    public void JoinRemote_TrimsTrailingSlashAndUsesForwardSlash()
    {
        Assert.Equal("/srv/data/file.txt", RemoteCopyPlanner.JoinRemote("/srv/data/", "file.txt"));
        Assert.Equal("/srv/data/file.txt", RemoteCopyPlanner.JoinRemote("/srv/data", "file.txt"));
    }

    [Fact]
    public void SftpBrowserRoundtripFileCopy_UsesPublishIfAbsentCommitMode()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Heimdall.Sftp",
            "SftpBrowser.cs"));
        string roundtripMethod = ExtractMethod(source, "private async Task CopyFileViaRoundtripAsync(");
        string commitMethod = ExtractMethod(source, "private static void CommitUploadedTemp(");
        int publishBranchStart = commitMethod.IndexOf(
            "if (commitMode == UploadCommitMode.PublishIfAbsent)",
            StringComparison.Ordinal);
        int replaceBranchStart = commitMethod.IndexOf(
            "if (commitMode != UploadCommitMode.ReplaceExisting)",
            StringComparison.Ordinal);

        Assert.True(publishBranchStart >= 0);
        Assert.True(replaceBranchStart > publishBranchStart);

        string publishBranch = commitMethod[publishBranchStart..replaceBranchStart];

        Assert.Contains("UploadCommitMode.PublishIfAbsent", roundtripMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("UploadCommitMode.ReplaceExisting", roundtripMethod, StringComparison.Ordinal);
        Assert.Contains("SftpAtomicUpload.CommitPublishIfAbsent(", publishBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("SftpAtomicUpload.CommitRename(", publishBranch, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        int methodStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Method signature was not found: {signature}");

        int openingBrace = source.IndexOf('{', methodStart);
        Assert.True(openingBrace >= 0, $"Method opening brace was not found: {signature}");

        int depth = 0;
        for (int index = openingBrace; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[methodStart..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Method closing brace was not found: {signature}");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class FakeRemoteTree
    {
        public HashSet<string> ExistingDestinations { get; } = new HashSet<string>(StringComparer.Ordinal);

        public HashSet<string> Directories { get; } = new HashSet<string>(StringComparer.Ordinal);

        public Dictionary<string, List<string>> Children { get; } =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public List<(string Source, string Destination)> CopiedFiles { get; } = [];

        public List<string> CreatedDirectories { get; } = [];

        public RemoteCopyOps BuildOps() => new RemoteCopyOps(
            DestinationExistsAsync: (path, _) => Task.FromResult(ExistingDestinations.Contains(path)),
            SourceIsDirectoryAsync: (path, _) => Task.FromResult(Directories.Contains(path)),
            ListChildNamesAsync: (path, _) => Task.FromResult<IReadOnlyList<string>>(
                Children.TryGetValue(path, out List<string>? names) ? names : []),
            CopyFileAsync: (source, destination, _) =>
            {
                CopiedFiles.Add((source, destination));
                return Task.CompletedTask;
            },
            CreateDirectoryAsync: (path, _) =>
            {
                CreatedDirectories.Add(path);
                return Task.CompletedTask;
            });
    }
}
