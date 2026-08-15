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
    public async Task CommitRenameAsync_WarnsExactlyOnceAfterTheCommitMove_WhenAnExistingTargetIsReplaced()
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
            },
            onExistingTargetReplaced: () => operations.Add("warn"));

        // The position in the sequence is the assertion, not merely the count. A counter alone
        // cannot tell a warning raised after the replacement happened from one raised while it was
        // still reversible, and both mutants would keep the count at one.
        Assert.NotNull(backupPath);
        Assert.Equal(
            [
                $"move:/srv/app/config.txt->{backupPath}",
                "move:/srv/app/config.txt.part->/srv/app/config.txt",
                "warn",
                $"delete:{backupPath}"
            ],
            operations);
    }

    [Fact]
    public async Task CommitRenameAsync_DoesNotWarn_WhenFinalPathIsAbsent()
    {
        List<string> operations = [];

        await FtpAtomicUpload.CommitRenameAsync(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            (_, _) => Task.FromResult(false),
            (source, destination, _) =>
            {
                operations.Add($"move:{source}->{destination}");
                return Task.FromResult(true);
            },
            (path, _) =>
            {
                operations.Add($"delete:{path}");
                return Task.CompletedTask;
            },
            onExistingTargetReplaced: () => operations.Add("warn"));

        // Asserting the whole sequence rather than an empty warning list: a mutant that never
        // reached the commit at all would also raise nothing, and an absence-only oracle would
        // report that as a pass.
        Assert.Equal(["move:/srv/app/config.txt.part->/srv/app/config.txt"], operations);
    }

    [Fact]
    public async Task CommitRenameAsync_CommitsAndCleansUpBackup_WhenTheWarningSubscriberThrows()
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
            },
            onExistingTargetReplaced: () => throw new InvalidOperationException("subscriber failed"));

        // The destination already holds the new content when the subscriber runs. Not throwing is
        // only half the contract: the absence of any restore move proves the failure did not undo
        // the commit, and the trailing delete proves it did not skip the backup cleanup either.
        Assert.NotNull(backupPath);
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
    public async Task CommitRenameAsync_RestoresBackupAndPropagates_WhenCommitMoveThrows()
    {
        HashSet<string> remote = new(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        List<(string Source, string Destination)> moveCalls = [];
        List<string> warnings = [];
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
                },
                onExistingTargetReplaced: () => warnings.Add("warn")));

        Assert.Same(commitFailure, exception);

        // The backup was restored, so the original destination is still the original file. Warning
        // that its metadata was lost would be a false claim about a replacement that never landed.
        Assert.Empty(warnings);
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
        List<string> warnings = [];

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
                },
                onExistingTargetReplaced: () => warnings.Add("warn")));

        Assert.Contains("commit move returned false", exception.Message, StringComparison.Ordinal);
        Assert.Empty(warnings);
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
        List<string> warnings = [];

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
                },
                onExistingTargetReplaced: () => warnings.Add("warn")));

        Assert.Contains("backup move returned false", exception.Message, StringComparison.Ordinal);

        // The destination was never moved aside, so it still holds its original content and its
        // original metadata. Nothing was replaced and nothing may be announced as replaced.
        Assert.Empty(warnings);
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
