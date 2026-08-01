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

public sealed class SftpAtomicUploadTests
{
    [Fact]
    public void CommitRename_UsesAtomicRenameOnly_WhenAtomicRenameSucceeds()
    {
        var atomicCalls = new List<(string Temp, string Final)>();
        var plainCalls = new List<(string Temp, string Final)>();
        var deleteCalls = new List<string>();
        var existsCalls = new List<string>();

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (temp, final) => atomicCalls.Add((temp, final)),
            plainRename: (temp, final) => plainCalls.Add((temp, final)),
            remoteExists: path =>
            {
                existsCalls.Add(path);
                return false;
            },
            deleteRemote: path => deleteCalls.Add(path));

        var atomicCall = Assert.Single(atomicCalls);
        Assert.Equal("/srv/app/config.txt.part", atomicCall.Temp);
        Assert.Equal("/srv/app/config.txt", atomicCall.Final);
        Assert.Empty(plainCalls);
        Assert.Empty(existsCalls);
        Assert.Empty(deleteCalls);
    }

    [Fact]
    public void CommitRename_FallsBackWithBackupAndCleanup_WhenAtomicRenameThrows()
    {
        var remote = new HashSet<string>(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        var renameCalls = new List<(string Source, string Destination)>();
        var deleteCalls = new List<string>();

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new InvalidOperationException("extension unavailable"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            deleteRemote: path =>
            {
                deleteCalls.Add(path);
                remote.Remove(path);
            });

        Assert.Equal(2, renameCalls.Count);
        Assert.Equal("/srv/app/config.txt", renameCalls[0].Source);
        Assert.StartsWith("/srv/app/config.txt.", renameCalls[0].Destination, StringComparison.Ordinal);
        Assert.EndsWith(".bak", renameCalls[0].Destination, StringComparison.Ordinal);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), renameCalls[1]);
        Assert.Equal(renameCalls[0].Destination, Assert.Single(deleteCalls));
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(renameCalls[0].Destination, remote);
    }

    [Fact]
    public void CommitRename_RestoresBackup_WhenFallbackRenameFails()
    {
        var remote = new HashSet<string>(StringComparer.Ordinal)
        {
            "/srv/app/config.txt",
            "/srv/app/config.txt.part"
        };
        var renameCalls = new List<(string Source, string Destination)>();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SftpAtomicUpload.CommitRename(
                "/srv/app/config.txt.part",
                "/srv/app/config.txt",
                atomicRename: (_, _) => throw new InvalidOperationException("extension unavailable"),
                plainRename: (source, destination) =>
                {
                    renameCalls.Add((source, destination));
                    if (source == "/srv/app/config.txt.part")
                    {
                        throw new InvalidOperationException("plain rename failed");
                    }

                    Assert.True(remote.Remove(source));
                    remote.Add(destination);
                },
                remoteExists: remote.Contains,
                deleteRemote: path => remote.Remove(path)));

        Assert.Equal("plain rename failed", ex.Message);
        Assert.Equal(3, renameCalls.Count);
        string backupPath = renameCalls[0].Destination;
        Assert.Equal(("/srv/app/config.txt", backupPath), renameCalls[0]);
        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), renameCalls[1]);
        Assert.Equal((backupPath, "/srv/app/config.txt"), renameCalls[2]);
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.Contains("/srv/app/config.txt.part", remote);
        Assert.DoesNotContain(backupPath, remote);
    }

    [Fact]
    public void CommitRename_FallbackWithoutExistingFinal_RenamesTempOnly()
    {
        var remote = new HashSet<string>(StringComparer.Ordinal)
        {
            "/srv/app/config.txt.part"
        };
        var renameCalls = new List<(string Source, string Destination)>();

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new InvalidOperationException("extension unavailable"),
            plainRename: (source, destination) =>
            {
                renameCalls.Add((source, destination));
                Assert.True(remote.Remove(source));
                remote.Add(destination);
            },
            remoteExists: remote.Contains,
            deleteRemote: path => remote.Remove(path));

        Assert.Equal(("/srv/app/config.txt.part", "/srv/app/config.txt"), Assert.Single(renameCalls));
        Assert.Contains("/srv/app/config.txt", remote);
        Assert.DoesNotContain("/srv/app/config.txt.part", remote);
    }

    [Fact]
    public void CommitRename_RaisesNonAtomicReplacementOnce_WhenFallbackReplacesExistingTarget()
    {
        int warningCount = 0;

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new InvalidOperationException("extension unavailable"),
            plainRename: (_, _) => { },
            remoteExists: path => path == "/srv/app/config.txt",
            deleteRemote: _ => { },
            onNonAtomicReplacement: () => warningCount++);

        Assert.Equal(1, warningCount);
    }

    [Fact]
    public void CommitRename_DoesNotRaiseNonAtomicReplacement_WhenAtomicRenameSucceeds()
    {
        int warningCount = 0;

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => { },
            plainRename: (_, _) => { },
            remoteExists: _ => true,
            deleteRemote: _ => { },
            onNonAtomicReplacement: () => warningCount++);

        Assert.Equal(0, warningCount);
    }

    [Fact]
    public void CommitRename_DoesNotRaiseNonAtomicReplacement_WhenFallbackTargetIsAbsent()
    {
        int warningCount = 0;

        SftpAtomicUpload.CommitRename(
            "/srv/app/config.txt.part",
            "/srv/app/config.txt",
            atomicRename: (_, _) => throw new InvalidOperationException("extension unavailable"),
            plainRename: (_, _) => { },
            remoteExists: _ => false,
            deleteRemote: _ => { },
            onNonAtomicReplacement: () => warningCount++);

        Assert.Equal(0, warningCount);
    }

    [Fact]
    public void Rollback_DeletesOnlyTempPath()
    {
        var deletedPaths = new List<string>();

        SftpAtomicUpload.Rollback(
            "/srv/app/config.txt.part",
            temp => deletedPaths.Add(temp));

        Assert.Equal("/srv/app/config.txt.part", Assert.Single(deletedPaths));
    }

    [Fact]
    public void CreateRemoteTempPath_KeepsSameRemoteDirectoryAndUsesSlashSeparators()
    {
        string tempPath = SftpAtomicUpload.CreateRemoteTempPath("/srv/app/config.txt");

        Assert.StartsWith("/srv/app/config.txt.", tempPath, StringComparison.Ordinal);
        Assert.EndsWith(".part", tempPath, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', tempPath);
        Assert.Equal("/srv/app", GetRemoteDirectory(tempPath));
    }

    private static string GetRemoteDirectory(string remotePath)
    {
        int separator = remotePath.LastIndexOf('/');
        return separator <= 0 ? string.Empty : remotePath[..separator];
    }
}
