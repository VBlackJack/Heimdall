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
/// Unit tests for the pure <see cref="RemoteUploadTreePlanner"/>: file-vs-directory dispatch, recursive
/// fan-out with parents emitted before children, relative remote path construction into a target
/// directory, and child-name validation. The local tree is faked entirely in memory through the
/// injected enumerate delegate (mirrors <see cref="RemoteCopyPlannerTests"/>).
/// </summary>
public sealed class RemoteUploadTreePlannerTests
{
    [Fact]
    public async Task Plan_LooseFiles_EmitsUploadsIntoTargetDirectory()
    {
        FakeLocalTree tree = new FakeLocalTree();

        IReadOnlyList<RemoteUploadOp> ops = await RemoteUploadTreePlanner.PlanAsync(
            [Entry(@"C:\src\a.txt"), Entry(@"C:\src\b.log")],
            "/srv/dst",
            tree.EnumerateChildrenAsync);

        Assert.Collection(
            ops,
            op => AssertUpload(op, @"C:\src\a.txt", "/srv/dst/a.txt"),
            op => AssertUpload(op, @"C:\src\b.log", "/srv/dst/b.log"));
    }

    [Fact]
    public async Task Plan_NestedDirectory_EmitsMkDirsBeforeChildrenWithRelativeRemotePaths()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\proj", Entry(@"C:\src\proj\readme.txt"), Dir(@"C:\src\proj\sub"));
        tree.AddDirectory(@"C:\src\proj\sub", Entry(@"C:\src\proj\sub\a.txt"), Entry(@"C:\src\proj\sub\b.log"));

        IReadOnlyList<RemoteUploadOp> ops = await RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\proj")],
            "/srv/T",
            tree.EnumerateChildrenAsync);

        Assert.Collection(
            ops,
            op => AssertMkDir(op, "/srv/T/proj"),
            op => AssertUpload(op, @"C:\src\proj\readme.txt", "/srv/T/proj/readme.txt"),
            op => AssertMkDir(op, "/srv/T/proj/sub"),
            op => AssertUpload(op, @"C:\src\proj\sub\a.txt", "/srv/T/proj/sub/a.txt"),
            op => AssertUpload(op, @"C:\src\proj\sub\b.log", "/srv/T/proj/sub/b.log"));
    }

    [Fact]
    public async Task Plan_EmptyDirectory_EmitsOnlyMkDir()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\empty");

        IReadOnlyList<RemoteUploadOp> ops = await RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\empty")],
            "/srv/dst",
            tree.EnumerateChildrenAsync);

        RemoteUploadOp op = Assert.Single(ops);
        AssertMkDir(op, "/srv/dst/empty");
    }

    [Fact]
    public async Task Plan_UnsafeChildName_ThrowsIOException()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\proj", new LocalUploadEntry(@"C:\src\proj\..", "..", IsDirectory: true));

        await Assert.ThrowsAsync<IOException>(() => RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\proj")],
            "/srv/dst",
            tree.EnumerateChildrenAsync));
    }

    [Fact]
    public async Task PlanAsync_AlreadyCancelledToken_ThrowsBeforeEnumeratingAnything()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\proj", Entry(@"C:\src\proj\readme.txt"));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\proj")],
            "/srv/dst",
            tree.EnumerateChildrenAsync,
            cts.Token));

        Assert.Equal(0, tree.EnumerateCallCount);
    }

    [Fact]
    public async Task PlanAsync_CancelledPartWayThroughTheTree_StopsTheWalkInsteadOfReturningAPartialPlan()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\proj", Dir(@"C:\src\proj\one"), Dir(@"C:\src\proj\two"));
        tree.AddDirectory(@"C:\src\proj\one", Entry(@"C:\src\proj\one\a.txt"));
        tree.AddDirectory(@"C:\src\proj\two", Entry(@"C:\src\proj\two\b.txt"));
        using CancellationTokenSource cts = new();
        tree.OnEnumerate = _ =>
        {
            if (tree.EnumerateCallCount >= 2)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\proj")],
            "/srv/dst",
            tree.EnumerateChildrenAsync,
            cts.Token));
    }

    [Fact]
    public async Task PlanAsync_DepthCapExceeded_StillThrowsIOException()
    {
        FakeLocalTree tree = new FakeLocalTree();
        for (int depth = 0; depth <= RemoteUploadTreePlanner.MaxUploadDepth + 1; depth++)
        {
            string current = @"C:\src\" + string.Join('\\', Enumerable.Repeat("d", depth + 1));
            string child = current + @"\d";
            tree.AddDirectory(current, new LocalUploadEntry(child, "d", IsDirectory: true));
        }

        await Assert.ThrowsAsync<IOException>(() => RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\d")],
            "/srv/dst",
            tree.EnumerateChildrenAsync));
    }

    [Fact]
    public async Task PlanAsync_MixedTree_KeepsParentBeforeChildrenDepthFirstOrder()
    {
        FakeLocalTree tree = new FakeLocalTree();
        tree.AddDirectory(@"C:\src\proj", Entry(@"C:\src\proj\readme.txt"), Dir(@"C:\src\proj\sub"));
        tree.AddDirectory(@"C:\src\proj\sub", Entry(@"C:\src\proj\sub\a.txt"), Entry(@"C:\src\proj\sub\b.log"));

        IReadOnlyList<RemoteUploadOp> ops = await RemoteUploadTreePlanner.PlanAsync(
            [Dir(@"C:\src\proj")],
            "/srv/T",
            tree.EnumerateChildrenAsync);

        Assert.Collection(
            ops,
            op => AssertMkDir(op, "/srv/T/proj"),
            op => AssertUpload(op, @"C:\src\proj\readme.txt", "/srv/T/proj/readme.txt"),
            op => AssertMkDir(op, "/srv/T/proj/sub"),
            op => AssertUpload(op, @"C:\src\proj\sub\a.txt", "/srv/T/proj/sub/a.txt"),
            op => AssertUpload(op, @"C:\src\proj\sub\b.log", "/srv/T/proj/sub/b.log"));
    }

    private static LocalUploadEntry Entry(string fullPath)
        => new LocalUploadEntry(fullPath, LeafName(fullPath), IsDirectory: false);

    private static LocalUploadEntry Dir(string fullPath)
        => new LocalUploadEntry(fullPath, LeafName(fullPath), IsDirectory: true);

    private static string LeafName(string fullPath)
    {
        int slash = fullPath.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? fullPath : fullPath[(slash + 1)..];
    }

    private static void AssertUpload(RemoteUploadOp op, string localPath, string remotePath)
    {
        Assert.Equal(RemoteUploadOpKind.UploadFile, op.Kind);
        Assert.Equal(localPath, op.LocalPath);
        Assert.Equal(remotePath, op.RemotePath);
    }

    private static void AssertMkDir(RemoteUploadOp op, string remotePath)
    {
        Assert.Equal(RemoteUploadOpKind.MakeDirectory, op.Kind);
        Assert.Equal(remotePath, op.RemotePath);
    }

    private sealed class FakeLocalTree
    {
        private readonly Dictionary<string, List<LocalUploadEntry>> _children =
            new Dictionary<string, List<LocalUploadEntry>>(StringComparer.Ordinal);

        public int EnumerateCallCount { get; private set; }

        /// <summary>Invoked before each enumeration, so a test can cancel part way through the walk.</summary>
        public Action<string>? OnEnumerate { get; set; }

        public void AddDirectory(string fullPath, params LocalUploadEntry[] children)
            => _children[fullPath] = [.. children];

        public IReadOnlyList<LocalUploadEntry> EnumerateChildren(string localDirectory)
            => _children.TryGetValue(localDirectory, out List<LocalUploadEntry>? children) ? children : [];

        // Deliberately does NOT observe the token: honouring cancellation is the planner's job,
        // and a fake that throws on its own would let the planner drop the token unnoticed.
        public Task<IReadOnlyList<LocalUploadEntry>> EnumerateChildrenAsync(
            string localDirectory,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            EnumerateCallCount++;
            OnEnumerate?.Invoke(localDirectory);
            return Task.FromResult(EnumerateChildren(localDirectory));
        }
    }
}
