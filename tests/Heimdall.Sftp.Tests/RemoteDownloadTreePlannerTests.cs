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
/// Unit tests for the pure <see cref="RemoteDownloadTreePlanner"/>: dispatch by entry kind, recursive
/// fan-out with parents emitted before children, local path construction through the injected
/// containment rule, and the entries the walk deliberately leaves out. The remote tree is faked in
/// memory through the injected listing delegate.
/// </summary>
public sealed class RemoteDownloadTreePlannerTests
{
    private const string LocalRoot = @"C:\dst";

    [Fact]
    public async Task Plan_LooseFiles_EmitsDownloadsIntoTheTarget()
    {
        FakeRemoteTree tree = new();

        RemoteDownloadPlan plan = await Plan(tree, [File("/srv/a.txt", 5), File("/srv/b.log", 7)]);

        Assert.Collection(
            plan.Ops,
            op => AssertDownload(op, "/srv/a.txt", @"C:\dst\a.txt", 5),
            op => AssertDownload(op, "/srv/b.log", @"C:\dst\b.log", 7));
        Assert.Empty(plan.SkippedUnsupportedPaths);
        Assert.Empty(plan.SkippedUnsafeNames);
    }

    [Fact]
    public async Task Plan_NestedDirectory_EmitsDirectoriesBeforeTheirChildren()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/proj", File("/srv/proj/readme.txt", 1), Dir("/srv/proj/sub"));
        tree.AddDirectory("/srv/proj/sub", File("/srv/proj/sub/a.txt", 2));

        RemoteDownloadPlan plan = await Plan(tree, [Dir("/srv/proj")]);

        Assert.Collection(
            plan.Ops,
            op => AssertMakeDirectory(op, @"C:\dst\proj"),
            op => AssertDownload(op, "/srv/proj/readme.txt", @"C:\dst\proj\readme.txt", 1),
            op => AssertMakeDirectory(op, @"C:\dst\proj\sub"),
            op => AssertDownload(op, "/srv/proj/sub/a.txt", @"C:\dst\proj\sub\a.txt", 2));
    }

    [Fact]
    public async Task Plan_EmptyDirectory_EmitsOnlyTheDirectory()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/empty");

        RemoteDownloadPlan plan = await Plan(tree, [Dir("/srv/empty")]);

        AssertMakeDirectory(Assert.Single(plan.Ops), @"C:\dst\empty");
    }

    [Fact]
    public async Task Plan_SymbolicLinks_AreNeverFollowedAndAreReported()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/proj", Link("/srv/proj/loop"), File("/srv/proj/ok.txt", 3));

        RemoteDownloadPlan plan = await Plan(tree, [Dir("/srv/proj"), Link("/srv/top-link")]);

        Assert.Equal(["/srv/proj/loop", "/srv/top-link"], plan.SkippedUnsupportedPaths);
        Assert.DoesNotContain(plan.Ops, op => op.RemotePath.EndsWith("loop", StringComparison.Ordinal));
        Assert.Equal(1, tree.ListCount("/srv/proj"));
        Assert.Equal(0, tree.ListCount("/srv/proj/loop"));
    }

    [Fact]
    public async Task Plan_NameTheLocalRuleRefuses_IsSkippedWithItsSubtree()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/proj", File("/srv/proj/CON", 1), File("/srv/proj/fine.txt", 2));

        RemoteDownloadPlan plan = await RemoteDownloadTreePlanner.PlanAsync(
            [Dir("/srv/proj")],
            LocalRoot,
            tree.ListAsync,
            (parent, name) => name == "CON" ? null : Path.Combine(parent, name));

        Assert.Equal(["/srv/proj/CON"], plan.SkippedUnsafeNames);
        Assert.Contains(plan.Ops, op => op.RemotePath == "/srv/proj/fine.txt");
        Assert.DoesNotContain(plan.Ops, op => op.RemotePath == "/srv/proj/CON");
    }

    [Fact]
    public async Task Plan_UnsafeRemoteName_IsSkippedWithoutReachingTheLocalRule()
    {
        FakeRemoteTree tree = new();
        int ruleCalls = 0;

        RemoteDownloadPlan plan = await RemoteDownloadTreePlanner.PlanAsync(
            [File("/srv/..", 1)],
            LocalRoot,
            tree.ListAsync,
            (parent, name) =>
            {
                ruleCalls++;
                return Path.Combine(parent, name);
            });

        Assert.Equal(["/srv/.."], plan.SkippedUnsafeNames);
        Assert.Equal(0, ruleCalls);
        Assert.Empty(plan.Ops);
    }

    [Fact]
    public async Task Plan_DepthBeyondTheCap_Throws()
    {
        FakeRemoteTree tree = new();
        string path = "/d";
        tree.AddDirectory("/d", Dir("/d/d"));
        for (int level = 0; level <= RemoteDownloadTreePlanner.MaxDownloadDepth + 1; level++)
        {
            string next = path + "/d";
            tree.AddDirectory(next, Dir(next + "/d"));
            path = next;
        }

        await Assert.ThrowsAsync<IOException>(() => Plan(tree, [Dir("/d")]));
    }

    [Fact]
    public async Task Plan_Cancelled_StopsBeforeListing()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/proj", File("/srv/proj/a.txt", 1));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteDownloadTreePlanner.PlanAsync(
            [Dir("/srv/proj")],
            LocalRoot,
            tree.ListAsync,
            Combine,
            ct: cts.Token));
        Assert.Equal(0, tree.ListCount("/srv/proj"));
    }

    [Fact]
    public async Task Plan_ReportsHowManyEntriesItHasPlanned()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/srv/proj", File("/srv/proj/a.txt", 1), File("/srv/proj/b.txt", 1));
        List<int> reports = [];

        await RemoteDownloadTreePlanner.PlanAsync(
            [Dir("/srv/proj")],
            LocalRoot,
            tree.ListAsync,
            Combine,
            reports.Add);

        Assert.Equal([1, 2, 3], reports);
    }

    private static Task<RemoteDownloadPlan> Plan(FakeRemoteTree tree, IReadOnlyList<SftpFileInfo> roots)
        => RemoteDownloadTreePlanner.PlanAsync(roots, LocalRoot, tree.ListAsync, Combine);

    private static string? Combine(string parent, string name) => parent + "\\" + name;

    private static SftpFileInfo File(string path, long size)
        => Entry(path, RemoteEntryKind.File, size);

    private static SftpFileInfo Dir(string path)
        => Entry(path, RemoteEntryKind.Directory, 0);

    private static SftpFileInfo Link(string path)
        => Entry(path, RemoteEntryKind.SymbolicLink, 0);

    private static SftpFileInfo Entry(string path, RemoteEntryKind kind, long size)
        => new(
            path[(path.LastIndexOf('/') + 1)..],
            path,
            kind,
            size,
            DateTime.UnixEpoch,
            "rw-r--r--",
            "1000",
            "1000");

    private static void AssertDownload(RemoteDownloadOp op, string remote, string local, long size)
    {
        Assert.Equal(RemoteDownloadOpKind.DownloadFile, op.Kind);
        Assert.Equal(remote, op.RemotePath);
        Assert.Equal(local, op.LocalPath);
        Assert.Equal(size, op.Size);
    }

    private static void AssertMakeDirectory(RemoteDownloadOp op, string local)
    {
        Assert.Equal(RemoteDownloadOpKind.MakeDirectory, op.Kind);
        Assert.Equal(local, op.LocalPath);
    }

    private sealed class FakeRemoteTree
    {
        private readonly Dictionary<string, IReadOnlyList<SftpFileInfo>> _directories = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _listCounts = new(StringComparer.Ordinal);

        public void AddDirectory(string path, params SftpFileInfo[] children)
            => _directories[path] = children;

        public int ListCount(string path) => _listCounts.GetValueOrDefault(path);

        public Task<IReadOnlyList<SftpFileInfo>> ListAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _listCounts[path] = ListCount(path) + 1;
            return Task.FromResult(_directories.TryGetValue(path, out IReadOnlyList<SftpFileInfo>? children)
                ? children
                : []);
        }
    }
}
