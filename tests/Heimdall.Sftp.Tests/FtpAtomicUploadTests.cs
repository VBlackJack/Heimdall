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

public sealed class FtpAtomicUploadTests
{
    [Fact]
    public async Task CommitRenameAsync_MovesTempOnly_WhenFinalPathIsAbsent()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        List<string> deleteCalls = [];

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (path, _) => Task.FromResult(remote.Contains(path)),
            (source, destination, _) =>
            {
                moveCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
                return Task.FromResult(true);
            },
            (path, _) =>
            {
                deleteCalls.Add(path);
                remote.Remove(path);
                return Task.CompletedTask;
            });

        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), Assert.Single(moveCalls));
        Assert.DoesNotContain(moveCalls, call => call.Destination.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Empty(deleteCalls);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
    }

    [Fact]
    public async Task CommitRenameAsync_BackupsCommitsAndCleansUp_InOrder_WhenFinalPathExists()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<string> operations = [];
        string? backupPath = null;

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (path, _) => Task.FromResult(remote.Contains(path)),
            (source, destination, _) =>
            {
                operations.Add($"move:{source}->{destination}");
                Assert.True(remote.Remove(source));
                remote.Add(destination);
                if (source == "/srv/app/config.txt")
                {
                    backupPath = destination;
                }

                return Task.FromResult(true);
            },
            (path, _) =>
            {
                operations.Add($"delete:{path}");
                Assert.True(remote.Remove(path));
                return Task.CompletedTask;
            });

        Assert.NotNull(backupPath);
        Assert.StartsWith("/srv/app/config.txt.", backupPath, StringComparison.Ordinal);
        Assert.EndsWith(".bak", backupPath, StringComparison.Ordinal);
        Assert.Equal(
            [
                $"move:/srv/app/config.txt->{backupPath}",
                "move:/srv/app/config.txt.part->/srv/app/config.txt",
                $"delete:{backupPath}"
            ],
            operations);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(backupPath, remote);
    }

    [Fact]
    public async Task CommitRenameAsync_RaisesNonAtomicReplacementOnce_WhenExistingTargetIsBackedUp()
    {
        int warningCount = 0;

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (path, _) => Task.FromResult(path == "/srv/app/config.txt"),
            (_, _, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            onNonAtomicReplacement: () => warningCount++);

        Assert.Equal(1, warningCount);
    }

    [Fact]
    public async Task CommitRenameAsync_DoesNotRaiseNonAtomicReplacement_WhenFinalPathIsAbsent()
    {
        int warningCount = 0;

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (_, _) => Task.FromResult(false),
            (_, _, _) => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            onNonAtomicReplacement: () => warningCount++);

        Assert.Equal(0, warningCount);
    }

    [Fact]
    public async Task CommitRenameAsync_RestoresBackupAndPropagates_WhenCommitMoveThrows()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        InvalidOperationException commitFailure = new("commit move failed");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FtpAtomicUpload.CommitRenameAsync(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                (path, _) => Task.FromResult(remote.Contains(path)),
                (source, destination, _) =>
                {
                    moveCalls.Add((source, destination));
                    if (source == "/srv/app/config.txt.part")
                    {
                        throw commitFailure;
                    }

                    Assert.True(remote.Remove(source));
                    remote.Add(destination);
                    return Task.FromResult(true);
                },
                (path, _) =>
                {
                    remote.Remove(path);
                    return Task.CompletedTask;
                }));

        Assert.Same(commitFailure, exception);
        Assert.Equal(3, moveCalls.Count);
        string backupPath = moveCalls[0].Destination;
        Assert.Equal(("/srv/app/config.txt", backupPath), moveCalls[0]);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), moveCalls[1]);
        Assert.Equal((backupPath, "/srv/app/config.txt"), moveCalls[2]);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(backupPath, remote);
    }

    [Fact]
    public async Task CommitRenameAsync_RestoresBackupAndThrows_WhenCommitMoveReturnsFalse()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            FtpAtomicUpload.CommitRenameAsync(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                (path, _) => Task.FromResult(remote.Contains(path)),
                (source, destination, _) =>
                {
                    moveCalls.Add((source, destination));
                    if (source == "/srv/app/config.txt.part")
                    {
                        return Task.FromResult(false);
                    }

                    Assert.True(remote.Remove(source));
                    remote.Add(destination);
                    return Task.FromResult(true);
                },
                (path, _) =>
                {
                    remote.Remove(path);
                    return Task.CompletedTask;
                }));

        Assert.Contains("commit move returned false", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, moveCalls.Count);
        string backupPath = moveCalls[0].Destination;
        Assert.Equal((backupPath, "/srv/app/config.txt"), moveCalls[2]);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(backupPath, remote);
    }

    [Fact]
    public async Task CommitRenameAsync_StopsBeforeMovingTemp_WhenBackupMoveReturnsFalse()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        List<string> deleteCalls = [];

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            FtpAtomicUpload.CommitRenameAsync(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                (path, _) => Task.FromResult(remote.Contains(path)),
                (source, destination, _) =>
                {
                    moveCalls.Add((source, destination));
                    return Task.FromResult(false);
                },
                (path, _) =>
                {
                    deleteCalls.Add(path);
                    return Task.CompletedTask;
                }));

        Assert.Contains("backup move returned false", exception.Message, StringComparison.Ordinal);
        Assert.Single(moveCalls);
        Assert.DoesNotContain(moveCalls, call => call.Source == "/srv/app/config.txt.part");
        Assert.Empty(deleteCalls);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
    }

    [Fact]
    public async Task CommitRenameAsync_ThrowsWithoutBackupOrDelete_WhenAbsentTargetMoveReturnsFalse()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        List<string> deleteCalls = [];

        IOException exception = await Assert.ThrowsAsync<IOException>(() =>
            FtpAtomicUpload.CommitRenameAsync(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                (path, _) => Task.FromResult(remote.Contains(path)),
                (source, destination, _) =>
                {
                    moveCalls.Add((source, destination));
                    return Task.FromResult(false);
                },
                (path, _) =>
                {
                    deleteCalls.Add(path);
                    return Task.CompletedTask;
                }));

        Assert.Contains("commit move returned false", exception.Message, StringComparison.Ordinal);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), Assert.Single(moveCalls));
        Assert.DoesNotContain(moveCalls, call => call.Destination.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Empty(deleteCalls);
        Assert.Contains("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain("/srv/app/config.txt", remote);
    }

    [Fact]
    public async Task CommitRenameAsync_SucceedsWithNewFinal_WhenBackupCleanupThrows()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        string? backupPath = null;

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (path, _) => Task.FromResult(remote.Contains(path)),
            (source, destination, _) =>
            {
                moveCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
                if (source == "/srv/app/config.txt")
                {
                    backupPath = destination;
                }

                return Task.FromResult(true);
            },
            (_, _) => throw new IOException("cleanup failed"));

        Assert.NotNull(backupPath);
        Assert.Equal(2, moveCalls.Count);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), moveCalls[1]);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
        Assert.Contains(backupPath, remote);
    }
}
