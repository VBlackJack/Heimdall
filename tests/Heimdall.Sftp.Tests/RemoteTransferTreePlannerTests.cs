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
/// Unit tests for the pure cross-endpoint <see cref="RemoteTransferTreePlanner"/>.
/// </summary>
public sealed class RemoteTransferTreePlannerTests
{
    [Fact]
    public async Task PlanAsync_NestedSourceTree_EmitsDirectoriesBeforeFilesWithDestinationPaths()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/src/proj", Entry("readme.txt", "/src/proj/readme.txt"), Dir("sub", "/src/proj/sub"));
        tree.AddDirectory("/src/proj/sub", Entry("a.txt", "/src/proj/sub/a.txt"), Entry("b.log", "/src/proj/sub/b.log"));

        IReadOnlyList<RemoteTransferOp> ops = await RemoteTransferTreePlanner.PlanAsync(
            [Dir("proj", "/src/proj")],
            "/dst",
            tree.ListDirectoryAsync);

        Assert.Collection(
            ops,
            op => AssertMkDir(op, "/src/proj", "/dst/proj"),
            op => AssertTransfer(op, "/src/proj/readme.txt", "/dst/proj/readme.txt"),
            op => AssertMkDir(op, "/src/proj/sub", "/dst/proj/sub"),
            op => AssertTransfer(op, "/src/proj/sub/a.txt", "/dst/proj/sub/a.txt"),
            op => AssertTransfer(op, "/src/proj/sub/b.log", "/dst/proj/sub/b.log"));
    }

    [Fact]
    public async Task PlanAsync_LooseFiles_EmitsFileTransfersIntoTargetDirectory()
    {
        FakeRemoteTree tree = new();

        IReadOnlyList<RemoteTransferOp> ops = await RemoteTransferTreePlanner.PlanAsync(
            [Entry("a.txt", "/src/a.txt"), Entry("b.log", "/src/b.log")],
            "/dst",
            tree.ListDirectoryAsync);

        Assert.Collection(
            ops,
            op => AssertTransfer(op, "/src/a.txt", "/dst/a.txt"),
            op => AssertTransfer(op, "/src/b.log", "/dst/b.log"));
    }

    [Fact]
    public async Task PlanAsync_UnsafeChildName_ThrowsIOException()
    {
        FakeRemoteTree tree = new();
        tree.AddDirectory("/src/proj", Entry("..", "/src/proj/.."));

        await Assert.ThrowsAsync<IOException>(() => RemoteTransferTreePlanner.PlanAsync(
            [Dir("proj", "/src/proj")],
            "/dst",
            tree.ListDirectoryAsync));
    }

    [Fact]
    public async Task PlanAsync_DepthCap_ThrowsIOException()
    {
        FakeRemoteTree tree = new();
        string current = "/src/root";
        for (int i = 0; i <= RemoteTransferTreePlanner.MaxTransferDepth + 1; i++)
        {
            string child = $"{current}/d{i}";
            tree.AddDirectory(current, Dir($"d{i}", child));
            current = child;
        }

        await Assert.ThrowsAsync<IOException>(() => RemoteTransferTreePlanner.PlanAsync(
            [Dir("root", "/src/root")],
            "/dst",
            tree.ListDirectoryAsync));
    }

    private static SftpFileInfo Entry(string name, string fullPath)
        => new SftpFileInfo(name, fullPath, Kind: RemoteEntryKind.File, 1, DateTime.UnixEpoch, "rw-r--r--", "1000", "1000");

    private static SftpFileInfo Dir(string name, string fullPath)
        => new SftpFileInfo(name, fullPath, Kind: RemoteEntryKind.Directory, 0, DateTime.UnixEpoch, "rwxr-xr-x", "1000", "1000");

    private static void AssertTransfer(RemoteTransferOp op, string source, string destination)
    {
        Assert.Equal(RemoteTransferOpKind.TransferFile, op.Kind);
        Assert.Equal(source, op.SourceRemotePath);
        Assert.Equal(destination, op.DestinationRemotePath);
    }

    private static void AssertMkDir(RemoteTransferOp op, string source, string destination)
    {
        Assert.Equal(RemoteTransferOpKind.MakeDirectory, op.Kind);
        Assert.Equal(source, op.SourceRemotePath);
        Assert.Equal(destination, op.DestinationRemotePath);
    }

    private sealed class FakeRemoteTree
    {
        private readonly Dictionary<string, IReadOnlyList<SftpFileInfo>> _children =
            new(StringComparer.Ordinal);

        public void AddDirectory(string path, params SftpFileInfo[] children)
        {
            _children[path] = children;
        }

        public Task<IReadOnlyList<SftpFileInfo>> ListDirectoryAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_children.TryGetValue(path, out IReadOnlyList<SftpFileInfo>? children)
                ? children
                : []);
        }
    }
}
