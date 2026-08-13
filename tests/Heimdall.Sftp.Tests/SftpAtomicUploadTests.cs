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
    private const string FinalRemotePath = "/srv/app/config.txt";
    private const string TempRemotePath = "/srv/app/config.txt.part";

    [Fact]
    public void CommitRename_DoesNotConsultFallback_WhenAtomicRenameSucceeds()
    {
        List<(string Temp, string Final)> atomicCalls = new();
        List<(string Temp, string Final)> plainCalls = new();
        List<string> existsCalls = new();
        int predicateCalls = 0;

        SftpAtomicUpload.CommitRename(
            TempRemotePath,
            FinalRemotePath,
            atomicRename: (temp, final) => atomicCalls.Add((temp, final)),
            plainRename: (temp, final) => plainCalls.Add((temp, final)),
            remoteExists: path =>
            {
                existsCalls.Add(path);
                return true;
            },
            canDemoteAtomicRenameFailure: _ =>
            {
                predicateCalls++;
                return true;
            });

        Assert.Equal((TempRemotePath, FinalRemotePath), Assert.Single(atomicCalls));
        Assert.Empty(existsCalls);
        Assert.Empty(plainCalls);
        Assert.Equal(0, predicateCalls);
    }

    [Fact]
    public void CommitRename_RefusesFallbackReplacement_WhenDestinationExists()
    {
        Dictionary<string, string> remote = new(StringComparer.Ordinal)
        {
            [FinalRemotePath] = "old-content",
            [TempRemotePath] = "new-content",
        };
        List<(string Source, string Destination)> plainCalls = new();
        List<string> existsCalls = new();
        NotSupportedException atomicFailure = new("posix-rename extension unavailable");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (_, _) => throw atomicFailure,
                plainRename: (source, destination) =>
                {
                    plainCalls.Add((source, destination));
                    remote[destination] = remote[source];
                    remote.Remove(source);
                },
                remoteExists: path =>
                {
                    existsCalls.Add(path);
                    return remote.ContainsKey(path);
                },
                canDemoteAtomicRenameFailure: failure => failure is NotSupportedException));

        Assert.Contains(FinalRemotePath, exception.Message, StringComparison.Ordinal);
        Assert.Same(atomicFailure, exception.InnerException);
        Assert.Equal(FinalRemotePath, Assert.Single(existsCalls));
        Assert.Empty(plainCalls);
        Assert.Equal("old-content", remote[FinalRemotePath]);
        Assert.Equal("new-content", remote[TempRemotePath]);
        Assert.Equal(2, remote.Count);
    }

    [Fact]
    public void CommitRename_FailsClosed_WhenDestinationProbeFails()
    {
        List<(string Source, string Destination)> plainCalls = new();
        IOException probeFailure = new("stat refused");

        IOException exception = Assert.Throws<IOException>(() =>
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (_, _) => throw new NotSupportedException("posix-rename extension unavailable"),
                plainRename: (source, destination) => plainCalls.Add((source, destination)),
                remoteExists: _ => throw probeFailure,
                canDemoteAtomicRenameFailure: failure => failure is NotSupportedException));

        Assert.Same(probeFailure, exception);
        Assert.Empty(plainCalls);
    }

    [Fact]
    public void CommitRename_PropagatesPlainRenameFailure_WithoutCleanup()
    {
        Dictionary<string, string> remote = new(StringComparer.Ordinal)
        {
            [TempRemotePath] = "new-content",
        };
        List<(string Source, string Destination)> plainCalls = new();
        IOException renameFailure = new("plain rename failed");

        IOException exception = Assert.Throws<IOException>(() =>
            SftpAtomicUpload.CommitRename(
                TempRemotePath,
                FinalRemotePath,
                atomicRename: (_, _) => throw new NotSupportedException("posix-rename extension unavailable"),
                plainRename: (source, destination) =>
                {
                    plainCalls.Add((source, destination));
                    throw renameFailure;
                },
                remoteExists: remote.ContainsKey,
                canDemoteAtomicRenameFailure: failure => failure is NotSupportedException));

        Assert.Same(renameFailure, exception);
        Assert.Equal((TempRemotePath, FinalRemotePath), Assert.Single(plainCalls));
        Assert.Equal("new-content", remote[TempRemotePath]);
        Assert.DoesNotContain(FinalRemotePath, remote.Keys);
    }

    [Fact]
    public void Rollback_DeletesOnlyTempPath()
    {
        List<string> deletedPaths = new();

        SftpAtomicUpload.Rollback(
            TempRemotePath,
            temp => deletedPaths.Add(temp));

        Assert.Equal(TempRemotePath, Assert.Single(deletedPaths));
    }

    [Fact]
    public void CreateRemoteTempPath_KeepsSameRemoteDirectoryAndUsesSlashSeparators()
    {
        string tempPath = SftpAtomicUpload.CreateRemoteTempPath(FinalRemotePath);

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
